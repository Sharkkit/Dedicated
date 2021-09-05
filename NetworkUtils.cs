using System;
using Steamworks;
using Valve.Sockets;

namespace SharkkitDedicated
{
    public static class NetworkUtils
    {
        public static SendFlags ConvertFlags(EP2PSend flags)
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
    }
}
