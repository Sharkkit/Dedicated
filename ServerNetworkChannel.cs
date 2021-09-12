using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;
using Valve.Sockets;

namespace SharkkitDedicated
{
    public class ServerNetworkChannel : NetworkChannel
    {
        /// <summary>
        /// A PollGroup is used to efficiently poll multiple connections at once (here: all clients)
        /// </summary>
        public uint PollGroup;
        
        /// <summary>
        /// The Listen socket of the server
        /// </summary>
        public uint ServerSocket;
        
        // Internal stuff
        public readonly Dictionary<CSteamID, uint> ClientSockets = new Dictionary<CSteamID, uint>();
        public readonly Dictionary<CSteamID, Queue<NetworkingMessage>[]> PacketBufs =
            new Dictionary<CSteamID, Queue<NetworkingMessage>[]>();
        public StatusCallback ServerStatusCallback;
                
        /// <summary>
        /// Connections that have been made, but that still wait for the steam auth token package to verify.
        /// </summary>
        public readonly HashSet<uint> PendingConnections = new HashSet<uint>();
        
        public override bool IsServer => true;
        
        public override bool TryGetConnectionFor(CSteamID remoteId, out uint socket)
        {
            return ClientSockets.TryGetValue(remoteId, out socket);
        }

        public override void Deinitialize(NetworkingSockets networkingSockets)
        {
            networkingSockets.DestroyPollGroup(PollGroup);
            // TODO: For all _networkingSockets.CloseConnection()
            networkingSockets.CloseListenSocket(ServerSocket);
        }
        
        protected override void EnqueueMessages(int nbMsgs)
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
                        var con = ClientSockets.FirstOrDefault(x => x.Value == msg.connection);
                        if (!PacketBufs.ContainsKey(con.Key))
                        {
                            // TODO: Determine the maximum channel size dynamically
                            var quArray = new Queue<NetworkingMessage>[2];
                            quArray[0] = new Queue<NetworkingMessage>();
                            quArray[1] = new Queue<NetworkingMessage>();
                            PacketBufs.Add(con.Key, quArray);
                        }

                        var buf = PacketBufs[con.Key];
                        var nChannel = Marshal.ReadByte(msg.data);
                        
                        if (!PacketBufs.ContainsKey(con.Key))
                        {
                            Debug.LogWarning($"Received a packet from an unknown connection {con.Key}");
                        }

                        if (nChannel >= buf.Length)
                        {
                            Debug.LogWarning($"Skipping a message of size {msg.length} because the channel {nChannel} is not available");
                            continue;
                        }
                        
                        PacketBufs[con.Key][nChannel].Enqueue(msg);
                    }
                }
                catch (KeyNotFoundException knf)
                {
                    Debug.LogError($"Could not find a connection for the Id {msg.connection}. {knf}");
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Debug.LogError(ex);
                }
            }
        }
        
        private void HandleAuthMessage(NetworkingMessage m)
        {
            var buf = new byte[m.length];
            var ticket = new byte[m.length - 8];
            Marshal.Copy(m.data, buf, 0, m.length);
            var steamId = new CSteamID(BitConverter.ToUInt64(buf, 0));
            Array.Copy(buf, 8, ticket, 0, ticket.Length);

            var res = NetworkWrapper._serverList.BeginAuthentication(ticket, steamId);
            Debug.Log($"Steam Authentication Result for {steamId}: {res}");
            
            if (res == EBeginAuthSessionResult.k_EBeginAuthSessionResultOK || 
                res == EBeginAuthSessionResult.k_EBeginAuthSessionResultGameMismatch /* spacewars */)
            {
                PendingConnections.Remove(m.connection);
                NetworkWrapper._networkingSockets.SendMessageToConnection(m.connection,
                    BitConverter.GetBytes(SteamGameServerList.ServerId.m_SteamID), SendFlags.Reliable);
                ClientSockets.Add(steamId, m.connection);
            }
            else
            {
                Debug.LogError("Error validating the Auth Ticket!");
                NetworkWrapper._networkingSockets.SendMessageToConnection(m.connection, BitConverter.GetBytes(0UL),
                    SendFlags.Reliable);
            }
        }

        public override void Tick(NetworkingSockets networkingSockets)
        {
            EnqueueMessages(networkingSockets.ReceiveMessagesOnPollGroup(PollGroup, _messages, _messages.Length));
        }

        public override bool SendMessage(CSteamID steamIDRemote, byte[] data, SendFlags flags, int channel)
        {
            return ClientSockets.TryGetValue(steamIDRemote, out var con) && 
                   NetworkWrapper.SendBufToConnection(data, flags, channel, con);
        }

        public class QueueablePacket
        {
            public byte[] Data;
            public SendFlags Flags;
            public int Channel;
            
            public QueueablePacket(byte[] pubData, SendFlags flags, int nChannel)
            {
                Data = pubData;
                Flags = flags;
                Channel = nChannel;
            }
        }
    }
}
