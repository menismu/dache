using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Dache.Core.Communication
{
    /// <summary>
    /// Implementation of auto discover manager based on Multicast UDP.
    /// </summary>
    public class MulticastUDPCacheHostAutoDetectManager : ICacheHostAutoDetectManager
    {
        private const string MESSAGE_SEPARATOR = " ";

        private const int THREAD_WAIT_TIME_MS = 100;

        private readonly IPAddress _ipAddress;

        private readonly UdpClient _udpClient;

        private IPEndPoint remoteEndPoint;

        private bool running;

        public MulticastUDPCacheHostAutoDetectManager(IPAddress ipAddress, int multicastPort)
        {
            this._ipAddress = ipAddress;
            this.remoteEndPoint = new IPEndPoint(_ipAddress, multicastPort);
            this._udpClient = new UdpClient();
        }

        public void Run()
        {
            _udpClient.JoinMulticastGroup(_ipAddress);

            while (running)
            {
                // reading incoming message bytes were received
                if (_udpClient.Available > 0)
                {
                    Byte[] data = _udpClient.Receive(ref remoteEndPoint);

                    ParseMessage(data, remoteEndPoint.Address.ToString());
                }

                Thread.Sleep(THREAD_WAIT_TIME_MS);
            }
        }

        public void TryStop()
        {
            running = false;
        }

        /// <summary>
        /// Parse the message given in the data buffer.
        /// </summary>
        /// <param name="data">Bytes containing the message to be parsed</param>
        /// <param name="remoteIp">Ip adderess of the remote cache host</param>
        private void ParseMessage(byte[] data, string remoteIp)
        {
            var dataString = Encoding.Unicode.GetString(data);
            var messageParts = dataString.Split(new string[] { MESSAGE_SEPARATOR }, StringSplitOptions.RemoveEmptyEntries);

            if (messageParts.Length > 0)
            {
                switch (messageParts[0])
                {
                    case "HELO":
                        // add the new cache host to the list

                        break;

                    case "BYE":
                        // remove cache host in the list

                        break;
                }
            }
        }
    }
}
