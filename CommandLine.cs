using Steamworks;
using UnityEngine;

namespace SharkkitDedicated
{
    public static class CommandLine
    {
        // TODO: The SteamId should be determined at the initial handshake, so it doesn't need to be known pre-connect.
        public static bool Parse(string arg, out string address, out ushort port, out CSteamID fakeSteamId)
        {
            if (!arg.StartsWith("ConIp:"))
            {
                address = null;
                port = 0;
                fakeSteamId = CSteamID.Nil;
                return false;
            }

            // ConIp:127.0.0.1:1337:987654321 -> IP:Port:SteamId
            var parts = arg.Substring("ConIp:".Length).Split(':');
            if (parts.Length != 3)
            {
                Debug.LogWarning($"Invalid ConIp Format \"{arg}\". {parts.Length} parts.");
                address = null;
                port = 0;
                fakeSteamId = CSteamID.Nil;
                return false;
            }

            address = parts[0];
            port = ushort.Parse(parts[1]);
            fakeSteamId = new CSteamID(ulong.Parse(parts[2]));
            return true;
        }
    }
}
