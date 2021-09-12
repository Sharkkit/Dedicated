namespace SharkkitDedicated
{
    /// <summary>
    /// The class that contains all the parameters relevant for starting the server (listening on the socket)
    /// </summary>
    public class ListenArguments
    {
        public string ListenIp = "0.0.0.0";
        public readonly ushort Port;
        public ushort QueryPort;
        public bool AdvertiseInSteamBrowser = true;
        public string GsUserToken = null;

        public ListenArguments(ushort port)
        {
            Port = port;
            QueryPort = (ushort)(Port + 1u);
        }
    }
}