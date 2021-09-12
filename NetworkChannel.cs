using Steamworks;
using Valve.Sockets;

namespace SharkkitDedicated
{
    /// <summary>
    /// Temporary abstraction: The Game uses NetworkChannels that are not supported by the dedicated networking backends.
    /// In the future we should probably invest into refactoring the Game's Networking code, but for now we just open
    /// multiple connections, one per channel.
    /// </summary>
    public abstract class NetworkChannel
    {
        /// <summary>
        /// Whether this Network Connection is a Client (connected to a server)
        /// </summary>
        public virtual bool IsClient => false;
        
        /// <summary>
        /// Whether this Network Connection is a Server (connected to clients)
        /// </summary>
        public virtual bool IsServer => false;
        
        // static shared message array to reduce Garbage Allocations
        protected readonly NetworkingMessage[] _messages = new NetworkingMessage[100];

        /// <summary>
        /// Try to get a <see cref="NetworkingSockets"/> socket (uint) for the given remoteId, that means:<br />
        /// - We as a server have an active connection to a client with that remoteId<br />
        /// - We as a client have an active connection to the server with that remoteId 
        /// </summary>
        /// <param name="remoteId">The sought after remoteId</param>
        /// <param name="socket">The socket to send packets to the target</param>
        /// <returns>Whether a socket was found or the field is invalid</returns>
        public abstract bool TryGetConnectionFor(CSteamID remoteId, out uint socket);

        public abstract void Deinitialize(NetworkingSockets networkingSockets);

        /// <summary>
        /// Tick this connection, that means polling for new messages that can then be queried by the API
        /// on NetworkWrapper
        /// </summary>
        /// <param name="networkingSockets"></param>
        public abstract void Tick(NetworkingSockets networkingSockets);

        protected abstract void EnqueueMessages(int nbMsgs);

        public abstract bool SendMessage(CSteamID steamIDRemote, byte[] data, SendFlags flags, int channel);
    }
}
