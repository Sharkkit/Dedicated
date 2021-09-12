using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;
using Valve.Sockets;

namespace SharkkitDedicated
{
    public class ClientNetworkChannel : NetworkChannel
    {
        /// <summary>
        /// The socket used to connect to the server
        /// </summary>
        public uint ClientSocket;
        
        /// <summary>
        /// The connection state/stage this client is in.<br />
        /// <b>Note:</b> This is NOT the current state of the socket, just in what stage the last handshake was in.
        /// </summary>
        public ClientConnectionState ConnectionState = ClientConnectionState.NotConnected;
        
        /// <summary>
        /// The SteamId of the remote server<br />
        /// Only valid when <see cref="ConnectionState"/> is <see cref="ClientConnectionState.FullyConnected"/>
        /// </summary>
        public CSteamID ServerId;
        
        // Internal stuff
        public readonly Queue<NetworkingMessage>[] PacketBuf = new Queue<NetworkingMessage>[2];  // TODO: Determine the maximum channel size dynamically 
        public HAuthTicket ClientAuthTicket;
        public StatusCallback ClientStatusCallback;
        
        ///
        
        /// <summary>
        /// Messages, that have been sent before the connection handshake was completely done.
        /// This happens because the came sends the intial packet right after connecting, but we return "async"
        /// and try to build up the connection without locking up the game. 
        /// </summary>
        public readonly Queue<ServerNetworkChannel.QueueablePacket> PreConnectBuf = new Queue<ServerNetworkChannel.QueueablePacket>();

        public override bool IsClient => true;

        public ClientNetworkChannel()
        {
            for (var i = 0; i < PacketBuf.Length; i++)
            {
                PacketBuf[i] = new Queue<NetworkingMessage>();
            }
        }

        public override bool TryGetConnectionFor(CSteamID remoteId, out uint socket)
        {
            if (remoteId.Equals(ServerId))
            {
                socket = ClientSocket;
                return true;
            }

            socket = 0;
            return false;
        }
        
        public override void Deinitialize(NetworkingSockets networkingSockets)
        {
            networkingSockets.CloseConnection(ClientSocket);
        }
        
        protected override void EnqueueMessages(int nbMsgs)
        {
            for (var i = 0; i < nbMsgs; i++)
            {
                var msg = _messages[i];

                if (ConnectionState == ClientConnectionState.AuthTicketSent && msg.length == 8)
                {
                    var id = (ulong)Marshal.ReadInt64(msg.data);

                    if (id == 0)
                    {
                        Debug.LogError("Auth ticket refused from the server. Cancelling connection!");
                        ConnectionState = ClientConnectionState.NotConnected;
                        // TODO: Proper teardown
                        return;
                    }

                    var steamId = new CSteamID(id);
                    Debug.Log($"Received AuthTicketResponse, Server SteamId is {steamId}");
                    ServerId = steamId;
                    ConnectionState = ClientConnectionState.FullyConnected;

                    while (PreConnectBuf.Count > 0)
                    {
                        var pkt = PreConnectBuf.Dequeue();
                        NetworkWrapper.SendBufToConnection(pkt.Data, pkt.Flags, pkt.Channel, ClientSocket);
                    }
                }
                else
                {
                    var nChannel = Marshal.ReadByte(msg.data);
                    if (nChannel >= PacketBuf.Length)
                    {
                        Debug.LogWarning($"Skipping a message of size {msg.length} because the channel {nChannel} is not available");
                        continue;
                    }

                    PacketBuf[nChannel].Enqueue(msg);
                }
            }
        }
        
        public void SendAuthPacket(uint socket)
        {
            Debug.Log("Sending the Client Auth Packet");
            var ms = new MemoryStream();
            var id = BitConverter.GetBytes(SteamUser.GetSteamID().m_SteamID);
            ms.Write(id, 0, id.Length);

            var ticketBuf = new byte[2048];
            ClientAuthTicket = SteamUser.GetAuthSessionTicket(ticketBuf, 2048, out var ticketLen);
            ms.Write(ticketBuf, 0, (int)ticketLen);
            NetworkWrapper._networkingSockets.SendMessageToConnection(socket, ms.ToArray(), SendFlags.Reliable);
        }

        public override void Tick(NetworkingSockets networkingSockets)
        {
            EnqueueMessages(networkingSockets.ReceiveMessagesOnConnection(ClientSocket, _messages, _messages.Length));
        }

        public override bool SendMessage(CSteamID steamIDRemote, byte[] data, SendFlags flags, int channel)
        {
            if (ConnectionState == ClientConnectionState.FullyConnected)
            {
                return NetworkWrapper.SendBufToConnection(data, flags, channel, ClientSocket);
            }
            else if (flags == SendFlags.Unreliable || flags == SendFlags.NoDelay)
            {
                Debug.LogWarning("Client is not yet fully connected, but the packet is unreliable, skipping");
                return true;
            } else
            {
                Debug.LogWarning("Client is not yet fully connected, queueing packet");
                PreConnectBuf.Enqueue(new ServerNetworkChannel.QueueablePacket(data, flags, channel));
                return true;
            }
        }
    }
}
