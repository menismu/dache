using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;

namespace Dache.Core.Configuration
{
    /// <summary>
    /// Abstract class where define the common fields in configuration for the other projects (e.g client or host).
    /// </summary>
    public class AbstractCacheConfigurationSection : ConfigurationSection
    {

        /// <summary>
        /// Wheter or not to auto detect cache hosts.
        /// </summary>
        [ConfigurationProperty("autoDetectCacheHosts", IsRequired = false, DefaultValue = false)]
        public bool AutoDetectCacheHosts
        {
            get
            {
                return (bool)this["autoDetectCacheHosts"];
            }
            set
            {
                this["autoDetectCacheHosts"] = value;
            }
        }

        /// <summary>
        /// IP to connect to the multicast group.
        /// </summary>
        [ConfigurationProperty("udpMulticastIp", IsRequired = false)]
        public string UdpMulticastIp
        {
            get
            {
                return (string)this["udpMulticastIp"];
            }
            set
            {
                this["udpMulticastIp"] = value;
            }
        }

        [ConfigurationProperty("udpMulticastPort", IsRequired = false)]
        public int UdpMulticastPort
        {
            get
            {
                return (int)this["udpMulticastPort"];
            }
            set
            {
                this["udpMulticastPort"] = value;
            }
        }
    }
}
