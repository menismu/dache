﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.Serialization;
using System.Threading;
using Dache.Client.Configuration;
using Dache.Client.Exceptions;
using Dache.Client.Serialization;
using Dache.Core.Communication;
using Dache.Core.Logging;
using SharpMemoryCache;
using SimplSockets;
using System.Net;

namespace Dache.Client
{
    /// <summary>
    /// The client for cache host communication. This should be used as a singleton/static reference and only created once. Thread safe.
    /// </summary>
    public class CacheClient : ICacheClient
    {
        // The list of cache host buckets
        private readonly List<CacheHostBucket> _cacheHostBuckets = new List<CacheHostBucket>(10);
        // The offline cache host bucket indexes
        private readonly HashSet<int> _offlineCacheHostBucketIndexes = new HashSet<int>();

        // The lock used to ensure state
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        // The binary serializer
        private readonly IBinarySerializer _binarySerializer = null;
        // The logger
        private readonly ILogger _logger = null;

        private readonly ICacheHostAutoDetectManager _cacheDiscoveryManager = null;

        private readonly Thread _multicastThread = null;

        /// <summary>
        /// The constructor that derives configuration from file.
        /// </summary>
        public CacheClient() : this(CacheClientConfigurationSection.Settings)
        {

        }

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="configuration">The configuration to use for the cache client.</param>
        public CacheClient(CacheClientConfigurationSection configuration)
        {
            // Sanitize
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }

            // Load custom logging
            _logger = CustomTypesLoader.LoadLogger(configuration);

            // Configure custom serializer
            _binarySerializer = CustomTypesLoader.LoadSerializer(configuration);

            CacheHostBucket currentCacheHostBucket = new CacheHostBucket();

            // checks the auto discover hosts is enabled, in this case hosts configuration will be discarded
            if (configuration.AutoDetectCacheHosts)
            {
                if (configuration.UdpMulticastIp == null)
                {
                    throw new ArgumentNullException("UdpMulticastIp");
                }

                if (!(configuration.UdpMulticastPort > 0))
                {
                    throw new ArgumentException("UdpMulticastPort");
                }

                _logger.Info("Launching multicast", "Launching the auto discover thread to discover cache hosts connected...");

                _cacheDiscoveryManager = new MulticastUDPCacheHostAutoDetectManager(IPAddress.Parse(configuration.UdpMulticastIp), configuration.UdpMulticastPort);
                _multicastThread = new Thread(new ThreadStart(_cacheDiscoveryManager.Run));
                _multicastThread.Start();
            }

            PerformCacheHostsFromConfiguration(configuration, currentCacheHostBucket);
        }

        /// <summary>
        /// Gets the object stored at the given cache key from the cache.
        /// </summary>
        /// <typeparam name="T">The expected type.</typeparam>
        /// <param name="cacheKey">The cache key.</param>
        /// <param name="value">The value or default for that type if the method returns false.</param>
        /// <returns>true if successful, false otherwise.</returns>
        public bool TryGet<T>(string cacheKey, out T value)
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "cacheKey");
            }

            // Do remote work
            List<byte[]> rawValues = null;

            do
            {
                var cacheHostBucket = DetermineBucket(cacheKey);

                try
                {
                    rawValues = cacheHostBucket.GetNext().Get(new[] { cacheKey });
                    break;
                }
                catch
                {
                    // Try a different cache host if this one could not be reached
                }
            } while (true);

            // If we got nothing back, return false and the default value for the type;
            if (rawValues == null || rawValues.Count == 0)
            {
                value = default(T);
                return false;
            }

            // Deserialize
            try
            {
                value = (T)_binarySerializer.Deserialize(rawValues[0]);
                return true;
            }
            catch
            {
                // Log serialization error
                _logger.Error("Serialization Error", string.Format("The object at cache key \"{0}\" could not be deserialized to type {1}", cacheKey, typeof(T)));

                value = default(T);
                return false;
            }
        }

        /// <summary>
        /// Gets the objects stored at the given cache keys from the cache.
        /// </summary>
        /// <typeparam name="T">The expected type.</typeparam>
        /// <param name="cacheKeys">The cache keys.</param>
        /// <returns>A list of the objects stored at the cache keys, or null if none were found.</returns>
        public List<T> Get<T>(IEnumerable<string> cacheKeys)
        {
            // Sanitize
            if (cacheKeys == null)
            {
                throw new ArgumentNullException("cacheKeys");
            }
            if (!cacheKeys.Any())
            {
                throw new ArgumentException("must have at least one element", "cacheKeys");
            }

            // Do remote work
            List<byte[]> rawResults = null;

            do
            {
                // Need to batch up requests
                var routingDictionary = new Dictionary<CacheHostBucket, List<string>>(_cacheHostBuckets.Count);
                List<string> clientCacheKeys = null;
                foreach (var cacheKey in cacheKeys)
                {
                    // Get the cache host bucket
                    var cacheHostBucket = DetermineBucket(cacheKey);
                    if (!routingDictionary.TryGetValue(cacheHostBucket, out clientCacheKeys))
                    {
                        clientCacheKeys = new List<string>(10);
                        routingDictionary.Add(cacheHostBucket, clientCacheKeys);
                    }

                    clientCacheKeys.Add(cacheKey);
                }

                try
                {
                    // Now we've batched them, do the work
                    rawResults = null;
                    foreach (var routingDictionaryEntry in routingDictionary)
                    {
                        var getResults = routingDictionaryEntry.Key.GetNext().Get(routingDictionaryEntry.Value);
                        if (getResults != null)
                        {
                            if (rawResults == null)
                            {
                                rawResults = getResults;
                                continue;
                            }

                            rawResults.AddRange(getResults);
                        }
                    }

                    // If we got here we did all of the work successfully
                    break;
                }
                catch
                {
                    // Rebalance and try again if a cache host could not be reached
                }
            } while (true);

            // If we got nothing back, return null
            if (rawResults == null)
            {
                return null;
            }

            var results = new List<T>(rawResults.Count);

            // Deserialize
            for (int i = 0; i < rawResults.Count; i++)
            {
                try
                {
                    results.Add((T)_binarySerializer.Deserialize(rawResults[i]));
                }
                catch
                {
                    results.Add(default(T));
                    // Log serialization error
                    _logger.Error("Serialization Error", string.Format("The object returned in a Get call at index {0} could not be deserialized to type {1}", i, typeof(T)));
                }
            }

            return results;
        }

        /// <summary>
        /// Gets the objects stored at the given tag name from the cache.
        /// </summary>
        /// <typeparam name="T">The expected type.</typeparam>
        /// <param name="tagName">The tag name.</param>
        /// <param name="pattern">The search pattern (RegEx). Optional. If not specified, the default of "*" is used to indicate match all.</param>
        /// <returns>A list of the objects stored at the tag name, or null if none were found.</returns>
        public List<T> GetTagged<T>(string tagName, string pattern = "*")
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(tagName))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "tagName");
            }

            // Do remote work
            IList<byte[]> rawResults = null;

            do
            {
                // Use the tag's cache host bucket
                var cacheHostBucket = DetermineBucket(tagName);

                try
                {
                    rawResults = cacheHostBucket.GetNext().GetTagged(new[] { tagName }, pattern: pattern);
                    break;
                }
                catch
                {
                    // Try a different cache host if this one could not be reached
                }
            } while (true);

            // If we got nothing back, return null
            if (rawResults == null)
            {
                return null;
            }

            var results = new List<T>(rawResults.Count);

            // Deserialize
            for (int i = 0; i < rawResults.Count; i++)
            {
                try
                {
                    results.Add((T)_binarySerializer.Deserialize(rawResults[i]));
                }
                catch
                {
                    results.Add(default(T));
                    // Log serialization error
                    _logger.Error("Serialization Error", string.Format("An object returned in a GetTagged call at index {0} could not be deserialized to type {1}", i, typeof(T)));
                }
            }

            return results;
        }

        /// <summary>
        /// Adds or updates an object in the cache at the given cache key.
        /// </summary>
        /// <param name="cacheKey">The cache key.</param>
        /// <param name="value">The value.</param>
        /// <param name="tagName">The tag name.</param>
        /// <param name="absoluteExpiration">The absolute expiration. NOTE: if both absolute and sliding expiration are set, sliding expiration will be ignored.</param>
        /// <param name="slidingExpiration">The sliding expiration. NOTE: if both absolute and sliding expiration are set, sliding expiration will be ignored.</param>
        /// <param name="notifyRemoved">Whether or not to notify the client when the cached item is removed from the cache.</param>
        /// <param name="isInterned">Whether or not to intern the objects. NOTE: interned objects use significantly less memory when 
        /// placed in the cache multiple times however cannot expire or be evicted. You must remove them manually when appropriate 
        /// or else you will face a memory leak. If specified, absoluteExpiration, slidingExpiration, and notifyRemoved are ignored.</param>
        public void AddOrUpdate(string cacheKey, object value, string tagName = null, DateTimeOffset? absoluteExpiration = null, TimeSpan? slidingExpiration = null, bool notifyRemoved = false, bool isInterned = false)
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "cacheKey");
            }
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }

            byte[] bytes = null;
            try
            {
                // Serialize
                bytes = _binarySerializer.Serialize(value);
            }
            catch (Exception ex)
            {
                throw new SerializationException("value could not be serialized.", ex);
            }

            do
            {
                // Get the cache host bucket - use tagName if specified
                var cacheHostBucket = DetermineBucket(tagName != null ? tagName : cacheKey);

                try
                {
                    cacheHostBucket.PerformActionOnAll(c => c.AddOrUpdate(new[] { new KeyValuePair<string, byte[]>(cacheKey, bytes) }, tagName: tagName, absoluteExpiration: absoluteExpiration, slidingExpiration: slidingExpiration, notifyRemoved: notifyRemoved, isInterned: isInterned));
                    break;
                }
                catch
                {
                    // Try a different cache host if this one could not be reached
                }
            } while (true);
        }

        /// <summary>
        /// Adds or updates many objects in the cache at the given cache keys.
        /// </summary>
        /// <param name="cacheKeysAndObjects">The cache keys and their associated objects.</param>
        /// <param name="tagName">The tag name.</param>
        /// <param name="absoluteExpiration">The absolute expiration. NOTE: if both absolute and sliding expiration are set, sliding expiration will be ignored.</param>
        /// <param name="slidingExpiration">The sliding expiration. NOTE: if both absolute and sliding expiration are set, sliding expiration will be ignored.</param>
        /// <param name="notifyRemoved">Whether or not to notify the client when the cached item is removed from the cache.</param>
        /// <param name="isInterned">Whether or not to intern the objects. NOTE: interned objects use significantly less memory when 
        /// placed in the cache multiple times however cannot expire or be evicted. You must remove them manually when appropriate 
        /// or else you will face a memory leak. If specified, absoluteExpiration, slidingExpiration, and notifyRemoved are ignored.</param>
        public void AddOrUpdate(IEnumerable<KeyValuePair<string, object>> cacheKeysAndObjects, string tagName = null, DateTimeOffset? absoluteExpiration = null, TimeSpan? slidingExpiration = null, bool notifyRemoved = false, bool isInterned = false)
        {
            // Sanitize
            if (cacheKeysAndObjects == null)
            {
                throw new ArgumentNullException("cacheKeysAndObjects");
            }
            if (!cacheKeysAndObjects.Any())
            {
                throw new ArgumentException("must have at least one element", "cacheKeysAndObjects");
            }

            var routingDictionary = new Dictionary<CacheHostBucket, List<KeyValuePair<string, byte[]>>>(_cacheHostBuckets.Count);
            List<KeyValuePair<string, byte[]>> clientCacheKeysAndObjects = null;
            byte[] bytes = null;
            var useTagName = tagName != null;

            do
            {
                foreach (var cacheKeyAndObjectKvp in cacheKeysAndObjects)
                {
                    try
                    {
                        // Serialize
                        bytes = _binarySerializer.Serialize(cacheKeyAndObjectKvp.Value);
                    }
                    catch
                    {
                        // Log serialization error
                        _logger.Error("Serialization Error", string.Format("An object added via an AddOrUpdateMany call at cache key \"{0}\" could not be serialized", cacheKeyAndObjectKvp.Key));
                        continue;
                    }

                    // Get the cache host bucket - use tagName if specified
                    var cacheHostBucket = DetermineBucket(useTagName ? tagName : cacheKeyAndObjectKvp.Key);
                    if (!routingDictionary.TryGetValue(cacheHostBucket, out clientCacheKeysAndObjects))
                    {
                        clientCacheKeysAndObjects = new List<KeyValuePair<string, byte[]>>(10);
                        routingDictionary.Add(cacheHostBucket, clientCacheKeysAndObjects);
                    }

                    clientCacheKeysAndObjects.Add(new KeyValuePair<string, byte[]>(cacheKeyAndObjectKvp.Key, bytes));
                }

                // Ensure we're doing something
                if (clientCacheKeysAndObjects.Count == 0)
                {
                    return;
                }

                try
                {
                    foreach (var routingDictionaryEntry in routingDictionary)
                    {
                        routingDictionaryEntry.Key.PerformActionOnAll(c => c.AddOrUpdate(routingDictionaryEntry.Value, tagName: tagName, absoluteExpiration: absoluteExpiration, slidingExpiration: slidingExpiration, notifyRemoved: notifyRemoved, isInterned: isInterned));
                    }

                    // If we got here we did all of the work successfully
                    break;
                }
                catch
                {
                    // Rebalance and try again if a cache host could not be reached
                }
            } while (true);
        }

        /// <summary>
        /// Removes the object at the given cache key from the cache.
        /// </summary>
        /// <param name="cacheKey">The cache key.</param>
        public void Remove(string cacheKey)
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "cacheKey");
            }

            do
            {
                var cacheHostBucket = DetermineBucket(cacheKey);

                try
                {
                    cacheHostBucket.PerformActionOnAll(c => c.Remove(new[] { cacheKey }));
                    break;
                }
                catch
                {
                    // Try a different cache host if this one could not be reached
                }
            } while (true);
        }

        /// <summary>
        /// Removes the objects at the given cache keys from the cache.
        /// </summary>
        /// <param name="cacheKeys">The cache keys.</param>
        public void Remove(IEnumerable<string> cacheKeys)
        {
            // Sanitize
            if (cacheKeys == null)
            {
                throw new ArgumentNullException("cacheKeys");
            }
            if (!cacheKeys.Any())
            {
                throw new ArgumentException("must have at least one element", "cacheKeys");
            }

            do
            {
                // Need to batch up requests
                var routingDictionary = new Dictionary<CacheHostBucket, List<string>>(_cacheHostBuckets.Count);
                List<string> clientCacheKeys = null;
                foreach (var cacheKey in cacheKeys)
                {
                    // Get the cache host bucket
                    var cacheHostBucket = DetermineBucket(cacheKey);
                    if (!routingDictionary.TryGetValue(cacheHostBucket, out clientCacheKeys))
                    {
                        clientCacheKeys = new List<string>(10);
                        routingDictionary.Add(cacheHostBucket, clientCacheKeys);
                    }

                    clientCacheKeys.Add(cacheKey);
                }

                try
                {
                    // Now we've batched them, do the work
                    foreach (var routingDictionaryEntry in routingDictionary)
                    {
                        routingDictionaryEntry.Key.PerformActionOnAll(c => c.Remove(routingDictionaryEntry.Value));
                    }

                    // If we got here we did all of the work successfully
                    break;
                }
                catch
                {
                    // Rebalance and try again if a cache host could not be reached
                }
            } while (true);
        }

        /// <summary>
        /// Removes all serialized objects associated with the given tag name and optionally with keys matching the given pattern.
        /// WARNING: THIS IS A VERY EXPENSIVE OPERATION FOR LARGE TAG CACHES. USE WITH CAUTION.
        /// </summary>
        /// <param name="tagName">The tag name.</param>
        /// <param name="pattern">The search pattern (RegEx). Optional. If not specified, the default of "*" is used to indicate match all.</param>
        public void RemoveTagged(string tagName, string pattern = "*")
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(tagName))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "tagName");
            }
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "pattern");
            }

            do
            {
                var cacheHostBucket = DetermineBucket(tagName);

                try
                {
                    cacheHostBucket.PerformActionOnAll(c => c.RemoveTagged(new[] { tagName }, pattern));
                    break;
                }
                catch
                {
                    // Try a different cache host if this one could not be reached
                }
            } while (true);
        }

        /// <summary>
        /// Removes all serialized objects associated with the given tag names and optionally with keys matching the given pattern.
        /// WARNING: THIS IS A VERY EXPENSIVE OPERATION FOR LARGE TAG CACHES. USE WITH CAUTION.
        /// </summary>
        /// <param name="tagNames">The tag names.</param>
        /// <param name="pattern">The search pattern (RegEx). Optional. If not specified, the default of "*" is used to indicate match all.</param>
        public void RemoveTagged(IEnumerable<string> tagNames, string pattern = "*")
        {
            // Sanitize
            if (tagNames == null)
            {
                throw new ArgumentNullException("tagNames");
            }
            if (!tagNames.Any())
            {
                throw new ArgumentException("must have at least one element", "tagNames");
            }
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "pattern");
            }

            do
            {
                // Need to batch up requests
                var routingDictionary = new Dictionary<CacheHostBucket, List<string>>(_cacheHostBuckets.Count);
                List<string> clientTagNames = null;
                foreach (var tagName in tagNames)
                {
                    // Get the cache host bucket
                    var cacheHostBucket = DetermineBucket(tagName);
                    if (!routingDictionary.TryGetValue(cacheHostBucket, out clientTagNames))
                    {
                        clientTagNames = new List<string>(10);
                        routingDictionary.Add(cacheHostBucket, clientTagNames);
                    }

                    clientTagNames.Add(tagName);
                }

                try
                {
                    // Now we've batched them, do the work
                    foreach (var routingDictionaryEntry in routingDictionary)
                    {
                        routingDictionaryEntry.Key.PerformActionOnAll(c => c.RemoveTagged(routingDictionaryEntry.Value, pattern));
                    }

                    // If we got here we did all of the work successfully
                    break;
                }
                catch
                {
                    // Rebalance and try again if a cache host could not be reached
                }
            } while (true);
        }

        /// <summary>
        /// Gets all cache keys, optionally matching the provided pattern.
        /// WARNING: THIS IS A VERY EXPENSIVE OPERATION FOR LARGE CACHES. USE WITH CAUTION.
        /// </summary>
        /// <param name="pattern">The search pattern (RegEx). Optional. If not specified, the default of "*" is used to indicate match all.</param>
        /// <returns>The list of cache keys matching the provided pattern.</returns>
        public List<string> GetCacheKeys(string pattern = "*")
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "pattern");
            }

            do
            {
                List<string> results = new List<string>();

                // Enumerate all cache hosts
                try
                {
                    foreach (var cacheHostBucket in _cacheHostBuckets)
                    {
                        var communicationClient = cacheHostBucket.GetNext();
                        // Ignore offline
                        if (communicationClient == null)
                        {
                            continue;
                        }

                        var rawResults = communicationClient.GetCacheKeys(pattern);

                        // Ensure we got some results
                        if (rawResults == null)
                        {
                            // Skip client
                            continue;
                        }

                        // Add to overall results
                        results.AddRange(rawResults);
                    }

                    return results;
                }
                catch
                {
                    // Rebalance and try again if a cache host could not be reached or the list changed
                }
            } while (true);
        }

        /// <summary>
        /// Gets all cache keys associated with the given tag name and optionally matching the given pattern.
        /// WARNING: THIS IS A VERY EXPENSIVE OPERATION FOR LARGE TAG CACHES. USE WITH CAUTION.
        /// </summary>
        /// <param name="tagName">The tag name.</param>
        /// <param name="pattern">The search pattern (RegEx). Optional. If not specified, the default of "*" is used to indicate match all.</param>
        /// <returns>The list of cache keys matching the provided pattern.</returns>
        public List<string> GetCacheKeysTagged(string tagName, string pattern = "*")
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(tagName))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "tagName");
            }
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "pattern");
            }

            do
            {
                var cacheHostBucket = DetermineBucket(tagName);

                try
                {
                    var rawResults = cacheHostBucket.GetNext().GetCacheKeys(pattern);

                    // Ensure we got some results
                    if (rawResults == null)
                    {
                        return null;
                    }

                    return rawResults;
                }
                catch
                {
                    // Try a different cache host if this one could not be reached
                }
            } while (true);
        }

        /// <summary>
        /// Gets all cache keys associated with the given tag names and optionally matching the given pattern.
        /// WARNING: THIS IS A VERY EXPENSIVE OPERATION FOR LARGE TAG CACHES. USE WITH CAUTION.
        /// </summary>
        /// <param name="tagNames">The tag names.</param>
        /// <param name="pattern">The search pattern (RegEx). Optional. If not specified, the default of "*" is used to indicate match all.</param>
        /// <returns>The list of cache keys matching the provided pattern.</returns>
        public List<string> GetCacheKeysTagged(IEnumerable<string> tagNames, string pattern = "*")
        {
            // Sanitize
            if (tagNames == null)
            {
                throw new ArgumentNullException("tagNames");
            }
            if (!tagNames.Any())
            {
                throw new ArgumentException("must have at least one element", "tagNames");
            }
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("cannot be null, empty, or white space", "pattern");
            }

            do
            {
                List<string> results = new List<string>(100);

                // Need to batch up requests
                var routingDictionary = new Dictionary<CacheHostBucket, List<string>>(_cacheHostBuckets.Count);
                List<string> clientTagNames = null;
                foreach (var tagName in tagNames)
                {
                    // Get the cache host bucket
                    var cacheHostBucket = DetermineBucket(tagName);
                    if (!routingDictionary.TryGetValue(cacheHostBucket, out clientTagNames))
                    {
                        clientTagNames = new List<string>(10);
                        routingDictionary.Add(cacheHostBucket, clientTagNames);
                    }

                    clientTagNames.Add(tagName);
                }

                try
                {
                    // Now we've batched them, do the work
                    foreach (var routingDictionaryEntry in routingDictionary)
                    {
                        var rawResults = routingDictionaryEntry.Key.GetNext().GetCacheKeysTagged(routingDictionaryEntry.Value, pattern);

                        // Ensure we got some results for this host
                        if (rawResults == null)
                        {
                            // Skip host
                            continue;
                        }

                        // Add to overall results
                        results.AddRange(rawResults);
                    }

                    // Ensure we got some results
                    if (results.Count == 0)
                    {
                        return null;
                    }

                    return results;
                }
                catch
                {
                    // Rebalance and try again if a cache host could not be reached
                }
            } while (true);
        }

        /// <summary>
        /// Clears the cache.
        /// </summary>
        public void Clear()
        {
            do
            {
                // Enumerate all cache hosts
                try
                {
                    foreach (var cacheHostBucket in _cacheHostBuckets)
                    {
                        cacheHostBucket.PerformActionOnAll(c => c.Clear());
                    }

                    // If we got here we succeeded
                    break;
                }
                catch
                {
                    // Rebalance and try again if a cache host could not be reached or the list changed
                }   
            } while (true);
        }

        /// <summary>
        /// Shuts down the connection. Call this when unloading an app domain to gracefully exit.
        /// </summary>
        public void Shutdown()
        {
            do
            {
                // Enumerate all cache hosts
                try
                {
                    foreach (var cacheHostBucket in _cacheHostBuckets)
                    {
                        cacheHostBucket.PerformActionOnAll(c => c.Disconnect());
                    }

                    // shutdown auto discover thread
                    if (_cacheDiscoveryManager != null)
                    {
                        _cacheDiscoveryManager.TryStop();
                    }

                    // If we got here we succeeded
                    break;
                }
                catch
                {
                    // Rebalance and try again if a cache host could not be reached or the list changed
                }
            } while (true);
        }

        /// <summary>
        /// Event that fires when the cache client is disconnected from a cache host.
        /// </summary>
        public event EventHandler HostDisconnected;

        /// <summary>
        /// Event that fires when the cache client is successfully reconnected to a disconnected cache host.
        /// </summary>
        public event EventHandler HostReconnected;

        /// <summary>
        /// Event that fires when a cached item has expired out of the cache.
        /// </summary>
        public event EventHandler<CacheItemExpiredArgs> CacheItemExpired;

        /// <summary>
        /// Triggered when a client is disconnected from a cache host.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The event args.</param>
        private void OnClientDisconnected(object sender, EventArgs e)
        {
            var client = (CommunicationClient)sender;

            // Take cache host offline
            _lock.EnterWriteLock();

            try
            {
                var result = _cacheHostBuckets.Any(i => i.TakeOffline(client));
                if (!result)
                {
                    // Already done
                    return;
                }

                // See if any buckets have 0 online hosts
                for (int i = 0; i < _cacheHostBuckets.Count; i++)
                {
                    var cacheHostBucket = _cacheHostBuckets[i];

                    if (cacheHostBucket.GetNext() == null)
                    {
                        // Needs to be offline
                       _offlineCacheHostBucketIndexes.Add(i);
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            // Log the event
            _logger.Warn("Cache Host Disconnected", "The cache client has been disconnected from the cache host located at " + client.ToString() + " - it will be reconnected automatically as soon it can be successfully contacted.");

            var hostDisconnected = HostDisconnected;
            if (hostDisconnected != null)
            {
                hostDisconnected(sender, e);
            }
        }

        /// <summary>
        /// Triggered when a client is reconnected from a cache host.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The event args.</param>
        private void OnClientReconnected(object sender, EventArgs e)
        {
            var client = (CommunicationClient)sender;

            // Bring cache host online
            _lock.EnterWriteLock();

            try
            {
                var result = _cacheHostBuckets.Any(i => i.BringOnline(client));
                if (!result)
                {
                    // Already done
                    return;
                }

                // See if any buckets have > 0 online hosts
                for (int i = 0; i < _cacheHostBuckets.Count; i++)
                {
                    var cacheHostBucket = _cacheHostBuckets[i];

                    if (_offlineCacheHostBucketIndexes.Contains(i) && cacheHostBucket.GetNext() != null)
                    {
                        // Needs to be online
                        _offlineCacheHostBucketIndexes.Remove(i);
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            // Log the event
            _logger.Warn("Cache Host Reconnected", "The cache client has successfully reconnected to the cache host located at " + client.ToString());

            var hostReconnected = HostReconnected;
            if (hostReconnected != null)
            {
                hostReconnected(sender, e);
            }
        }

        /// <summary>
        /// Determines the cache host bucket based on the cache key.
        /// </summary>
        /// <param name="cacheKey">The cache key.</param>
        /// <returns>The cache host bucket.</returns>
        private CacheHostBucket DetermineBucket(string cacheKey)
        {
            _lock.EnterReadLock();

            try
            {
                // Ensure a client is available
                if (_offlineCacheHostBucketIndexes.Count == _cacheHostBuckets.Count)
                {
                    throw new NoCacheHostsAvailableException("There are no reachable cache hosts available. Verify your client settings and ensure that all cache hosts can be successfully communicated with from this client.");
                }

                // Compute hash code
                var hashCode = ComputeHashCode(cacheKey);

                // The index to use is value of the (fairly evenly distributed) hashcode modulus the total cache host count
                var index = Math.Abs(hashCode % _cacheHostBuckets.Count);

                // Consistent hashing: if current is offline, use the one above it (rolling over to 0)
                while (_offlineCacheHostBucketIndexes.Contains(index))
                {
                    index = ++index % _cacheHostBuckets.Count;
                }

                return _cacheHostBuckets[index];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Computes an integer hash code for a cache key.
        /// </summary>
        /// <param name="cacheKey">The cache key.</param>
        /// <returns>A hash code.</returns>
        private static int ComputeHashCode(string cacheKey)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in cacheKey)
                {
                    // Very modulus-friendly
                    hash += c;
                }
                return hash;
            }
        }

        /// <summary>
        /// Computers an integer hash code that is guarantee to be identical for the same set of cache keys presented in any order.
        /// </summary>
        /// <param name="cacheKeys">The cache keys.</param>
        /// <returns>A hash code.</returns>
        private static int ComputeOrderIndependentHashCode(IEnumerable<string> cacheKeys)
        {
            int resultHash = 0;
            foreach (var cacheKey in cacheKeys)
            {
                int hash = 17;
                unchecked
                {
                    foreach (char c in cacheKey)
                    {
                        // Very modulus-friendly
                        hash += c;
                    }
                }
                resultHash ^= hash;
            }

            return resultHash;
        }

        private void ReceiveMessage(object sender, MessageReceivedArgs e)
        {
            var command = e.ReceivedMessage.Message;
            if (command == null || command.Length == 0)
            {
                return;
            }

            // Get the command string
            int position = 0;
            var commandString = DacheProtocolHelper.CommunicationEncoding.GetString(DacheProtocolHelper.Extract(command, ref position));

            // Right now this is only used for invalidating cache keys, so there will never be a reply
            ProcessCommand(commandString, command, position);
        }

        private void ProcessCommand(string command, byte[] data, int position)
        {
            // Sanitize
            if (string.IsNullOrWhiteSpace(command) || data == null || data.Length == 0 || position <= -1)
            {
                return;
            }

            string[] commandParts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (commandParts.Length == 0)
            {
                return;
            }

            // Determine command
            if (string.Equals(commandParts[0], "expire", StringComparison.OrdinalIgnoreCase))
            {
                // Invalidate local cache keys
                while (position < data.Length)
                {
                    var result = DacheProtocolHelper.CommunicationEncoding.GetString(DacheProtocolHelper.Extract(data, ref position));
                    // Fire the cache item expired event
                    var cacheItemExpired = CacheItemExpired;
                    if (cacheItemExpired != null)
                    {
                        cacheItemExpired(this, new CacheItemExpiredArgs(result));
                    }
                }
            }
        }
        
        /// <summary>
        /// Performs the configuration of cache hosts configured in the application configuration file.
        /// </summary>
        /// <param name="configuration">Configuration data.</param>
        /// <param name="currentCacheHostBucket">Current cache host list.</param>
        private void PerformCacheHostsFromConfiguration(CacheClientConfigurationSection configuration, CacheHostBucket currentCacheHostBucket)
        {
            // Get the host redundancy layers from configuration
            var hostRedundancyLayers = configuration.HostRedundancyLayers;

            // Get the cache hosts from configuration
            var cacheHosts = configuration.CacheHosts;

            // Sanitize
            if (cacheHosts == null)
            {
                _logger.Info("No hosts configured", "No cache hosts configured in config file");

                return;
            }

            // Assign the cache hosts to buckets in a specified order
            foreach (CacheHostElement cacheHost in cacheHosts.OfType<CacheHostElement>().OrderBy(i => i.Address).ThenBy(i => i.Port))
            {
                // Instantiate a communication client
                var communicationClient = new CommunicationClient(cacheHost.Address, cacheHost.Port, configuration.HostReconnectIntervalSeconds * 1000,
                    configuration.MessageBufferSize, configuration.CommunicationTimeoutSeconds * 1000, configuration.MaximumMessageSizeKB * 1024);

                // Hook up the disconnected and reconnected events
                communicationClient.Disconnected += OnClientDisconnected;
                communicationClient.Reconnected += OnClientReconnected;

                // Hook up the message receive event
                communicationClient.MessageReceived += ReceiveMessage;

                // Add to cache host bucket
                currentCacheHostBucket.AddCacheHost(communicationClient);

                // check if done with bucket
                if (currentCacheHostBucket.Count == hostRedundancyLayers + 1)
                {
                    _cacheHostBuckets.Add(currentCacheHostBucket);
                    currentCacheHostBucket = new CacheHostBucket();
                }
            }

            // Final safety check for uneven cache host distributions
            if (currentCacheHostBucket.Count > 0)
            {
                _cacheHostBuckets.Add(currentCacheHostBucket);
            }

            _logger.Info("Cache Host Assignment", string.Format("Assigned {0} cache hosts to {1} cache host buckets ({2} per bucket)", cacheHosts.Count, _cacheHostBuckets.Count, hostRedundancyLayers + 1));

            // Now attempt to connect to each host
            foreach (var cacheHostBucket in _cacheHostBuckets)
            {
                cacheHostBucket.PerformActionOnAll(c => c.Connect());
            }
        }

        /// <summary>
        /// Provides cache host bucket information.
        /// </summary>
        private class CacheHostBucket
        {
            // The cache hosts
            private readonly List<CommunicationClient> _cacheHosts = new List<CommunicationClient>();
            // The offline cache hosts
            private readonly List<CommunicationClient> _offlineCacheHosts = new List<CommunicationClient>();
            // The current cache host index
            private volatile int _currentCacheHostIndex = 0;
            // The cache host count
            private int _cacheHostCount = 0;
            // The lock
            private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

            /// <summary>
            /// Adds a cache host to this bucket.
            /// </summary>
            /// <param name="cacheHost">The cache host.</param>
            public void AddCacheHost(CommunicationClient cacheHost)
            {
                // Sanitize
                if (cacheHost == null)
                {
                    throw new ArgumentNullException("cacheHost");
                }

                _lock.EnterWriteLock();
                try
                {
                    _cacheHosts.Add(cacheHost);
                    _cacheHostCount++;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            /// <summary>
            /// Gets the next communication client.
            /// </summary>
            /// <returns>The next communication client, or null if none are available.</returns>
            public CommunicationClient GetNext()
            {
                _lock.EnterReadLock();
                try
                {
                    if (_cacheHosts.Count == 0)
                    {
                        return null;
                    }

                    unchecked
                    {
                        // Should be close enough to atomic and never escape the bounds of the list
                        return _cacheHosts[_currentCacheHostIndex++ % _cacheHosts.Count];
                    }
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }

            /// <summary>
            /// Performs an action on all cache hosts in the bucket.
            /// </summary>
            /// <param name="cacheHostFunc">The action to perform.</param>
            public void PerformActionOnAll(Action<CommunicationClient> cacheHostFunc)
            {
                List<CommunicationClient> cacheHosts = null;

                _lock.EnterReadLock();
                try
                {
                    cacheHosts = _cacheHosts.ToList();
                }
                finally
                {
                    _lock.ExitReadLock();
                }

                // Enumerate outside of the lock so disconnect events don't deadlock
                foreach (var cacheHost in cacheHosts)
                {
                    cacheHostFunc(cacheHost);
                }
            }

            /// <summary>
            /// Takes the specified cache host offline.
            /// </summary>
            /// <param name="cacheHost">The cache host.</param>
            /// <returns>true if successful.</returns>
            public bool TakeOffline(CommunicationClient cacheHost)
            {
                _lock.EnterWriteLock();
                try
                {
                    var result = _cacheHosts.Remove(cacheHost);
                    if (result)
                    {
                        _offlineCacheHosts.Add(cacheHost);

                        // Reset the current host to be safe
                        _currentCacheHostIndex = 0;
                    }

                    return result;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            /// <summary>
            /// Brings the specified cache host online.
            /// </summary>
            /// <param name="cacheHost">The cache host.</param>
            /// <returns>true if successful.</returns>
            public bool BringOnline(CommunicationClient cacheHost)
            {
                _lock.EnterWriteLock();
                try
                {
                    var result = _offlineCacheHosts.Remove(cacheHost);
                    if (result)
                    {
                        _cacheHosts.Add(cacheHost);
                    }

                    return result;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            /// <summary>
            /// The cache host count.
            /// </summary>
            public int Count
            {
                get
                {
                    return _cacheHostCount;
                }
            }
        }
    }
}
