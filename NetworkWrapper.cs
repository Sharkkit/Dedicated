using System;
using System.Linq;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;
using Valve.Sockets;

namespace SharkkitDedicated
{
    /// <summary>
    /// Wrapper used to handle SteamNetworking API calls and selectively use dedicated networking _or_ the P2P API.
    /// The Wrapper methods are not safe, that means if you didn't have an existing Client or Server channel, it will
    /// just fail and throw a cryptic (NullReference typically) Exception.
    ///
    /// TODO: Make safe and use fallback p2p.
    /// </summary>
    public static class NetworkWrapper
    {
        internal static NetworkingSockets _networkingSockets;
        
        /// <summary>
        /// This is either _serverChannel or _clientChannel, depending on the actual mode this is operating on.
        /// </summary>
        private static NetworkChannel _channel;
        
        internal static readonly SteamGameServerList _serverList = new SteamGameServerList();

        public static void Initialize()
        {
            _serverList.Init();
            if (_networkingSockets != null)
            {
                return; // Prevent Double Init.
            }
            
            Library.Initialize();
            _networkingSockets = new NetworkingSockets();
        }
        
        public static void Deinitialize()
        {
            _serverList.Shutdown();
            _channel.Deinitialize(_networkingSockets);
            Library.Deinitialize();
        }

        public static bool ListenOn(ListenArguments listenArguments)
        {
            var channel = new ServerNetworkChannel
            {
                PollGroup = _networkingSockets.CreatePollGroup()
            };

            channel.ServerStatusCallback = (ref StatusInfo info) =>
            {
                if (info.connectionInfo.state == ConnectionState.Connecting)
                {
                    // TODO: Emit the event for the legacy sockets. The implementation that Raft currently has doesn't do
                    // anything besides sending a packet that is probably not needed, so for now it's good enough.
                    _networkingSockets.SetConnectionPollGroup(channel.PollGroup, info.connection);
                    channel.PendingConnections.Add(info.connection);
                    var res = _networkingSockets.AcceptConnection(info.connection);
                }
                else if (info.connectionInfo.state == ConnectionState.Connected)
                {
                }
                else if (info.connectionInfo.state == ConnectionState.ClosedByPeer)
                {
                    var con = info.connection; // "Cannot use ref parameter within lambda"

                    _networkingSockets.CloseConnection(con); // Connection dropped.
                    if (channel.ClientSockets.Any(x => x.Value == con))
                    {
                        var steamId = channel.ClientSockets.First(x => x.Value == con);
                        channel.ClientSockets.Remove(steamId.Key);
                    }
                    channel.PendingConnections.Remove(con);
                }
                else
                {
                    Debug.LogWarning("Unknown Connection State. What should I do?");
                }
            };

            // TODO: the nagle time is 200ms for the old steam API, 5ms here. Change?
            var confCbStatusChanged = new Configuration
            {
                dataType = ConfigurationDataType.FunctionPtr,
                value = ConfigurationValue.ConnectionStatusChanged,
                data = new Configuration.ConfigurationData
                {
                    FunctionPtr = Marshal.GetFunctionPointerForDelegate(channel.ServerStatusCallback)
                }
            };

            var addr = AddressFromIPAndPort(listenArguments.ListenIp, listenArguments.Port);
            channel.ServerSocket = _networkingSockets.CreateListenSocket(ref addr, new[] { confCbStatusChanged });
            
            if (listenArguments.AdvertiseInSteamBrowser)
            {
                // TODO: More RegisterServer API
                var success = _serverList.RegisterServer(listenArguments.Port, listenArguments.QueryPort,
                    listenArguments.GsUserToken);
                Debug.Log($"Trying to register with Steam Master-Server: {success}");
            }

            _channel = channel;
            return true;
        }
        
        public static void ConnectToIP(CSteamID fakeHostId, string ipAddress, ushort port)
        {
            var channel = new ClientNetworkChannel();
            var addr = AddressFromIPAndPort(ipAddress, port);
        
            var confCbStatusChanged = new Configuration
            {
                dataType = ConfigurationDataType.FunctionPtr,
                value = ConfigurationValue.ConnectionStatusChanged,
                data = new Configuration.ConfigurationData()
            };
            
            channel.ClientStatusCallback = (ref StatusInfo info) =>
            {
                switch (info.connectionInfo.state)
                {
                    case ConnectionState.Connected:
                        if (channel.ConnectionState == ClientConnectionState.Connecting)
                        {
                            channel.ConnectionState = ClientConnectionState.AuthTicketSent;
                            channel.SendAuthPacket(channel.ClientSocket);
                        }

                        break;
                    case ConnectionState.Connecting:
                        break;
                }
            };
            
            confCbStatusChanged.data.FunctionPtr = Marshal.GetFunctionPointerForDelegate(channel.ClientStatusCallback);
            channel.ClientSocket = _networkingSockets.Connect(ref addr, new []{ confCbStatusChanged });
            channel.ConnectionState = ClientConnectionState.Connecting;
            _channel = channel;
        }

        public static void Tick()
        {
            _serverList.Tick();
            if (_networkingSockets == null)
            {
                return;
            }
            
            _networkingSockets.RunCallbacks();
            _channel?.Tick(_networkingSockets);
        }
        
        public static bool SendP2PPacket(CSteamID steamIDRemote, byte[] pubData, uint cubData, EP2PSend eP2PSendType,
            int nChannel = 0)
        {
            Debug.Assert(pubData.Length == cubData);
            var flags = NetworkUtils.ConvertFlags(eP2PSendType);
            Debug.Log($"Trying to send {cubData} Bytes to {steamIDRemote}. Flags {eP2PSendType}, {flags}");

            return _channel?.SendMessage(steamIDRemote, pubData, flags, nChannel) ?? 
                   SteamNetworking.SendP2PPacket(steamIDRemote, pubData, cubData, eP2PSendType, nChannel);
        }
        
        internal static bool SendBufToConnection(byte[] pubData, SendFlags flags, int nChannel, uint con)
        {
            if (nChannel >= 2)
            {
                throw new ArgumentOutOfRangeException(nameof(nChannel), "Invalid Channel");
            }
            
            // TODO: Use marshalling, because that otherwise happens internally anyway and we otherwise copy a lot of garbage.
            var buf = new byte[pubData.Length + 1];
            Array.Copy(pubData, 0, buf, 1, pubData.Length);
            buf[0] = (byte)nChannel; // TODO: Change API/ABI?
            return _networkingSockets.SendMessageToConnection(con, buf, buf.Length, flags) == Result.OK;
        }

        public static bool IsP2PPacketAvailable(out uint pcubMsgSize, int nChannel = 0)
        {
            if (_channel.IsServer)
            {
                var snc = (ServerNetworkChannel)_channel;
                var q = snc.PacketBufs.Values.Select(x => x[nChannel])
                    .FirstOrDefault(x => x.Count > 0);
                if (q != null)
                {
                    pcubMsgSize = (uint)q.Peek().length - 1; // the byte indicating the gameChannel
                    return true;
                }
            } else if (_channel.IsClient)
            {
                var cnc = (ClientNetworkChannel)_channel;
                if (cnc.PacketBuf[nChannel].Count > 0)
                {
                    pcubMsgSize = (uint)cnc.PacketBuf[nChannel].Peek().length - 1; // the byte indicating the gameChannel
                    return true;
                }
            }

            return SteamNetworking.IsP2PPacketAvailable(out pcubMsgSize, nChannel);
        }

        public static bool ReadP2PPacket(byte[] pubDest, uint cubDest, out uint pcubMsgSize, out CSteamID psteamIDRemote, int nChannel = 0)
        {
            if (_channel.IsServer)
            {
                var snc = (ServerNetworkChannel)_channel;
                var q = snc.PacketBufs.Values.Select(x => x[nChannel])
                    .FirstOrDefault(x => x.Count > 0);
                if (q != null)
                {
                    // TODO: Move the logic into Enqueue() that increments IntPtr and reduces the length?
                    var msg = q.Dequeue();
                    pcubMsgSize = (uint)msg.length - 1;

                    // TODO: Throws exceptions, if First is not found.
                    psteamIDRemote = snc.ClientSockets.First(x => x.Value == msg.connection).Key;
                    Marshal.Copy(msg.data + 1, pubDest, 0, (int)pcubMsgSize);
                    return true;
                }
            } else if (_channel.IsClient)
            {
                var cnc = (ClientNetworkChannel)_channel;
                if (cnc.PacketBuf[nChannel].Count > 0)
                {
                    var msg = cnc.PacketBuf[nChannel].Dequeue();
                    pcubMsgSize = (uint)msg.length - 1;
                    psteamIDRemote = cnc.ServerId;
                    Marshal.Copy(msg.data + 1, pubDest, 0, (int)pcubMsgSize);
                    return true;
                }
            }

            return SteamNetworking.ReadP2PPacket(pubDest, cubDest, out pcubMsgSize, out psteamIDRemote, nChannel);
        }

        public static bool CloseP2PSessionWithUser(CSteamID steamIDRemote)
        {
            // TODO: Proper handling game packet wise
            if (_channel.TryGetConnectionFor(steamIDRemote, out var con))
            {
                _networkingSockets.CloseConnection(con);

                if (_channel.IsServer)
                {
                    var snc = (ServerNetworkChannel)_channel;
                    snc.ClientSockets.Remove(steamIDRemote);
                    snc.PacketBufs.Remove(steamIDRemote); // No flushing
                }

                return true;
            }
            
            return SteamNetworking.CloseP2PSessionWithUser(steamIDRemote);
        }

        public static bool AcceptP2PSessionWithUser(CSteamID steamIDRemote)
        {
            return _channel.TryGetConnectionFor(steamIDRemote, out var con) /* TODO */ || 
                   SteamNetworking.AcceptP2PSessionWithUser(steamIDRemote);
        }

        public static bool GetP2PSessionState(CSteamID steamIDRemote, out P2PSessionState_t pConnectionState)
        {
            if (_channel.TryGetConnectionFor(steamIDRemote, out var con))
            {
                throw new NotImplementedException();
                // TODO: Implement for Dedicated connections.
            }
            
            return SteamNetworking.GetP2PSessionState(steamIDRemote, out pConnectionState);
        }
        
        public static bool AllowP2PPacketRelay(bool bAllow)
        {
            return SteamNetworking.AllowP2PPacketRelay(bAllow);
        }

        private static Address AddressFromIPAndPort(string ip, ushort port)
        {
            var a = new Address();
            a.SetAddress(ip, port);
            return a;
        }
    }
}
