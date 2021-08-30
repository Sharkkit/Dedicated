using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;
using Valve.Sockets;
using Debug = UnityEngine.Debug;

namespace SharkkitDedicated
{
    /// <summary>
    /// Wrapper used to handle SteamNetworking API calls and selectively use dedicated networking _or_ the P2P API
    /// </summary>
    public static class NetworkWrapper
    {
        private static NetworkingSockets _networkingSockets;
        private static NetworkChannel _channel;
        // static shared message array to reduce Garbage Allocations, but it requires non MT access or locking.
        private static readonly NetworkingMessage[] _messages = new NetworkingMessage[100];
        private static SteamGameServerList _serverList = new SteamGameServerList();
        
        /// <summary>
        /// Connections that have been made, but that still wait for the steam auth token package to verify.
        /// </summary>
        private static HashSet<uint> PendingConnections = new HashSet<uint>();

        public static void Initialize()
        {
            _serverList.Init();
            if (_networkingSockets != null)
            {
                return; // Prevent Double Init.
            }
            
            Library.Initialize();
            _networkingSockets = new NetworkingSockets();
            
            _channel = new NetworkChannel
            {
                PollGroup = _networkingSockets.CreatePollGroup()
            };
            
            _channel.ServerStatusCallback = (ref StatusInfo info) =>
            {
                ServerConnectionStatusChanged(ref info, _channel);
            };

            // TODO: the nagle time is 200ms for the old steam API, 5ms here. Change?
            var confCbStatusChanged = new Configuration
            {
                dataType = ConfigurationDataType.FunctionPtr,
                value = ConfigurationValue.ConnectionStatusChanged,
                data = new Configuration.ConfigurationData()
            };
            
            confCbStatusChanged.data.FunctionPtr = Marshal.GetFunctionPointerForDelegate(_channel.ServerStatusCallback);
        
            var addr = new Address();
            addr.SetAddress("0.0.0.0", (ushort)(1337));
            
            _channel.ServerSocket = _networkingSockets.CreateListenSocket(ref addr, new []{ confCbStatusChanged });

                // TODO: HostGame() hooking.
            Debug.Log($"Trying to register with Steam Master-Server: {_serverList.RegisterServer()}");
            Debug.Log($"ServerId: "+ SteamGameServerList.ServerId);
        }

        public static void Deinitialize()
        {
            _serverList.Shutdown();
            _networkingSockets.DestroyPollGroup(_channel.PollGroup);
            // TODO: For all _networkingSockets.CloseConnection()
            _networkingSockets.CloseListenSocket(_channel.ServerSocket);
            _networkingSockets.CloseConnection(_channel.ClientSocket);

            Library.Deinitialize();
        }

        public static void ConnectToLocalHost(CSteamID hostId)
        {
            _channel = new NetworkChannel();
            var addr = new Address();
            addr.SetAddress("127.0.0.1", (ushort)(1337));

            var confCbStatusChanged = new Configuration
            {
                dataType = ConfigurationDataType.FunctionPtr,
                value = ConfigurationValue.ConnectionStatusChanged,
                data = new Configuration.ConfigurationData()
            };

            _channel.ClientStatusCallback = (ref StatusInfo info) =>
            {
                switch (info.connectionInfo.state)
                {
                    case ConnectionState.Connected:
                        _channel.ClientConnected = true;
                        break;
                    case ConnectionState.Connecting:
                        break;
                    default:
                        _channel.ClientConnected = true; // Error, don't hang up forever.
                        break;
                }
            };

            confCbStatusChanged.data.FunctionPtr = Marshal.GetFunctionPointerForDelegate(_channel.ClientStatusCallback);
            _channel.ClientSocket = _networkingSockets.Connect(ref addr, new[] { confCbStatusChanged });

            // Unexpected blocking that can freeze the client, but I don't know what happens if Send() is called before being connected.
            // Raft itself immediately sends a message after the call to Connect.
            while (!_channel.ClientConnected)
            {
                Thread.Sleep(20);
                _networkingSockets.RunCallbacks();
            }

            var status = new ConnectionStatus();
            if (_networkingSockets.GetQuickConnectionStatus(_channel.ClientSocket, ref status))
            {
                if (status.state != ConnectionState.Connected)
                {
                    Debug.LogError("Error when trying to connect");
                    _channel.ClientConnected = false;
                    _channel.ClientSocket = 0;
                    return;
                }
            }

            _channel.DedicatedConnections.Add(hostId, _channel.ClientSocket);
        }
        
        public static void ConnectToIP(CSteamID fakeHostId, string ipAddress, ushort port)
        {
            _channel = new NetworkChannel();
            var addr = new Address();
            addr.SetAddress(ipAddress, port);
        
            var confCbStatusChanged = new Configuration
            {
                dataType = ConfigurationDataType.FunctionPtr,
                value = ConfigurationValue.ConnectionStatusChanged,
                data = new Configuration.ConfigurationData()
            };
            
            _channel.ClientStatusCallback = (ref StatusInfo info) =>
            {
                switch (info.connectionInfo.state)
                {
                    case ConnectionState.Connected:
                        _channel.ClientConnected = true;
                        break;
                    case ConnectionState.Connecting:
                        break;
                    default:
                        _channel.ClientConnected = true; // Error, don't hang up forever.
                        break;
                }
            };
            
            confCbStatusChanged.data.FunctionPtr = Marshal.GetFunctionPointerForDelegate(_channel.ClientStatusCallback);
            _channel.ClientSocket = _networkingSockets.Connect(ref addr, new []{ confCbStatusChanged });

            // Unexpected blocking that can freeze the client, but I don't know what happens if Send() is called before being connected.
            // Raft itself immediately sends a message after the call to Connect.
            while (!_channel.ClientConnected)
            {
                Thread.Sleep(20);
                _networkingSockets.RunCallbacks();
            }

            var status = new ConnectionStatus();
            if (_networkingSockets.GetQuickConnectionStatus(_channel.ClientSocket, ref status))
            {
                if (status.state != ConnectionState.Connected)
                {
                    Debug.LogError("Error when trying to connect");
                    _channel.ClientConnected = false;
                    _channel.ClientSocket = 0;
                    return;
                }
            }

            ClientAuth(_channel.ClientSocket);
            // TODO: Get the true hostId as result from the auth package and then be non-blocking
            _channel.DedicatedConnections.Add(fakeHostId, _channel.ClientSocket);
        }

        public static void Tick()
        {
            _serverList.Tick();
            if (_networkingSockets == null)
            {
                return;
            }
            
            _networkingSockets.RunCallbacks();
            EnqueueMessages(_networkingSockets.ReceiveMessagesOnPollGroup(_channel.PollGroup, _messages, 100));
            if (_channel.ClientConnected)
            {
                EnqueueMessages(_networkingSockets.ReceiveMessagesOnConnection(_channel.ClientSocket, _messages, 100));
            }
        }

        private static void EnqueueMessages(int nbMsgs)
        {
            for (var i = 0; i < nbMsgs; i++)
            {
                var msg = _messages[i];
                try
                {
                    if (PendingConnections.Contains(msg.connection))
                    {
                        HandleAuthMessage(msg);
                    }
                    else
                    {
                        var con = _channel.DedicatedConnections.FirstOrDefault(x => x.Value == msg.connection);
                        if (!_channel.PacketBufs.ContainsKey(con.Key))
                        {
                            var quArray = new Queue<NetworkingMessage>[2]; // TODO: Determine the maximum channel size dynamically
                            quArray[0] = new Queue<NetworkingMessage>();
                            quArray[1] = new Queue<NetworkingMessage>();
                            _channel.PacketBufs.Add(con.Key, quArray);
                        }

                        var nChannel = Marshal.ReadByte(msg.data);
                        _channel.PacketBufs[con.Key][nChannel].Enqueue(msg);
                    }
                }
                catch (KeyNotFoundException knf)
                {
                    Debug.LogError($"Could not find a connection for the Id {msg.connection}. {knf}");
                }
            }
        }

        private static void ServerConnectionStatusChanged(ref StatusInfo info, NetworkChannel channel)
        {
            Debug.LogWarning($"ConnectionStatus: {info.connectionInfo.state}");
            if (info.connectionInfo.state == ConnectionState.Connecting)
            {
                // TODO: Emit the event for the legacy sockets. The implementation that Raft currently has doesn't do
                // anything besides sending a packet that is probably not needed, so for now it's good enough.
                _networkingSockets.SetConnectionPollGroup(channel.PollGroup, info.connection);
                PendingConnections.Add(info.connection);
                var res = _networkingSockets.AcceptConnection(info.connection);
            } else if (info.connectionInfo.state == ConnectionState.Connected)
            {
            }
            else
            {
                var con = info.connection; // "Cannot use ref parameter within lambda"
                // Connection dropped.
                _networkingSockets.CloseConnection(con);
                var steamId = channel.DedicatedConnections.First(x => x.Value == con);
                channel.DedicatedConnections.Remove(steamId.Key);
            }
        }

        private static void HandleAuthMessage(NetworkingMessage m)
        {
            var buf = new byte[m.length];
            var ticket = new byte[m.length - 8];
            Marshal.Copy(m.data, buf, 0, m.length);
            var steamId = new CSteamID(BitConverter.ToUInt64(buf, 0));
            Array.Copy(buf, 8, ticket, 0, ticket.Length);

            var res = _serverList.BeginAuthentication(ticket, steamId);
            Debug.Log($"Steam Authentication Result for {steamId}: {res}");
            if (res == EBeginAuthSessionResult.k_EBeginAuthSessionResultOK || 
                res == EBeginAuthSessionResult.k_EBeginAuthSessionResultGameMismatch /* spacewars */)
            {
                PendingConnections.Remove(m.connection);
                _networkingSockets.SendMessageToConnection(m.connection,
                    BitConverter.GetBytes(SteamGameServerList.ServerId.m_SteamID), SendFlags.Reliable);
                _channel.DedicatedConnections.Add(steamId, m.connection);
            }
        }
        
        public static HAuthTicket ClientAuth(uint socket)
        {
            var ms = new MemoryStream();
            var id = BitConverter.GetBytes(SteamUser.GetSteamID().m_SteamID);
            ms.Write(id, 0, id.Length);

            var ticketBuf = new byte[2048];
            var ticket = SteamUser.GetAuthSessionTicket(ticketBuf, 2048, out var ticketLen);
            ms.Write(ticketBuf, 0, (int)ticketLen);
            
            _networkingSockets.SendMessageToConnection(socket, ms.ToArray(), SendFlags.Reliable);
            return ticket;
        }

        private static SendFlags ConvertFlags(EP2PSend flags)
        {
            switch (flags)
            {
                case EP2PSend.k_EP2PSendReliable: 
                    return SendFlags.Reliable;
                case EP2PSend.k_EP2PSendUnreliable:
                    return SendFlags.Unreliable;
                case EP2PSend.k_EP2PSendUnreliableNoDelay:
                    return SendFlags.NoDelay;
                case EP2PSend.k_EP2PSendReliableWithBuffering: // TODO: Validate
                    return SendFlags.NoNagle;
                default:
                    throw new ArgumentOutOfRangeException(nameof(flags), flags, null);
            }
        }

        public static bool SendP2PPacket(CSteamID steamIDRemote, byte[] pubData, uint cubData, EP2PSend eP2PSendType,
            int nChannel = 0)
        {
            if (_channel.DedicatedConnections.TryGetValue(steamIDRemote, out var con))
            {
                var flags = ConvertFlags(eP2PSendType);
                Debug.LogWarning($"Trying to send {cubData} Bytes to {steamIDRemote} on con {con}. Flags {eP2PSendType}, {flags}");
                Debug.Assert(pubData.Length == cubData);
                // TODO: Use marshalling, because that otherwise happens internally anyway and we otherwise copy a lot of garbage.
                var buf = new byte[pubData.Length + 1];
                Array.Copy(pubData, 0, buf, 1, pubData.Length);
                buf[0] = (byte)nChannel; // TODO: Change API/ABI?
                return _networkingSockets.SendMessageToConnection(con, buf, buf.Length, flags) == Result.OK;
            }
            else
            {
                return SteamNetworking.SendP2PPacket(steamIDRemote, pubData, cubData, eP2PSendType, nChannel);
            }
        }
        
        public static bool IsP2PPacketAvailable(out uint pcubMsgSize, int nChannel = 0)
        {
            var q = _channel.PacketBufs.Values.Select(x => x[nChannel])
                .FirstOrDefault(x => x.Count > 0);
            if (q != null)
            {
                pcubMsgSize = (uint)q.Peek().length - 1; // the byte indicating the gameChannel
                return true;
            }
            
            return SteamNetworking.IsP2PPacketAvailable(out pcubMsgSize, nChannel);
        }

        public static bool ReadP2PPacket(
            byte[] pubDest,
            uint cubDest,
            out uint pcubMsgSize,
            out CSteamID psteamIDRemote,
            int nChannel = 0)
        {
            var q = _channel.PacketBufs.Values.Select(x => x[nChannel])
                .FirstOrDefault(x => x.Count > 0);
            if (q != null)
            {
                // TODO: Move the logic into Enqueue() that increments IntPtr and reduces the length?
                var msg = q.Dequeue();
                pcubMsgSize = (uint)msg.length - 1;
                
                // TODO: Throws exceptions
                psteamIDRemote = _channel.DedicatedConnections.First(x => x.Value == msg.connection).Key;
                Marshal.Copy(msg.data + 1, pubDest, 0, (int)pcubMsgSize);
                return true;
            }
            
            return SteamNetworking.ReadP2PPacket(pubDest, cubDest, out pcubMsgSize, out psteamIDRemote, nChannel);
        }

        public static bool CloseP2PSessionWithUser(CSteamID steamIDRemote)
        {
            if (_channel.DedicatedConnections.TryGetValue(steamIDRemote, out var con))
            {
                _networkingSockets.CloseConnection(con);
            }
            
            return SteamNetworking.CloseP2PSessionWithUser(steamIDRemote);
        }

        public static bool AcceptP2PSessionWithUser(CSteamID steamIDRemote)
        {
            return _channel.DedicatedConnections.ContainsKey(steamIDRemote) || 
                   SteamNetworking.AcceptP2PSessionWithUser(steamIDRemote);
        }

        public static bool GetP2PSessionState(
            CSteamID steamIDRemote,
            out P2PSessionState_t pConnectionState)
        {
            if (_channel.DedicatedConnections.ContainsKey(steamIDRemote))
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
    }

    /// <summary>
    /// Temporary abstraction: The Game uses NetworkChannels that are not supported by the dedicated networking backends.
    /// In the future we should probably invest into refactoring the Game's Networking code, but for now we just open
    /// multiple connections, one per channel.
    /// </summary>
    public class NetworkChannel
    {
        public readonly Dictionary<CSteamID, uint> DedicatedConnections = new Dictionary<CSteamID, uint>();
        public uint ServerSocket;
        public uint ClientSocket; // Connection TO the server.
        public bool ClientConnected;
        public uint PollGroup;
        public readonly Dictionary<CSteamID, Queue<NetworkingMessage>[]> PacketBufs =
            new Dictionary<CSteamID, Queue<NetworkingMessage>[]>();

        public StatusCallback ServerStatusCallback;
        public StatusCallback ClientStatusCallback;
    }
}
