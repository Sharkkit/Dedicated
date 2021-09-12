namespace SharkkitDedicated
{
    /// <summary>
    /// This is the stage, the connection handshake is in.
    /// It thus is not related to the actual socket state (e.g. when a timeout/error occurs).
    /// Sending messages is only ever useful in <see cref="FullyConnected"/>, in other phases
    /// packets may be queued (if they are reliable) and flushed after the handshake.
    /// </summary>
    public enum ClientConnectionState
    {
        /// <summary>
        /// This client is not connected to any server.
        /// That is also the right state when being the server/host.
        /// </summary>
        NotConnected,
        
        /// <summary>
        /// Trying to establish a connection to the server.
        /// </summary>
        Connecting,
        
        /// <summary>
        /// We sent the server our auth ticket and are waiting for an acknowledgement
        /// </summary>
        AuthTicketSent,
        
        /// <summary>
        /// The handshake has been completed, we're connected.
        /// This is not updated when a connection error occurs, though.
        /// </summary>
        FullyConnected
    }
}