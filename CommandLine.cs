using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Steamworks;
using UnityEngine;

namespace SharkkitDedicated
{
    public static class CommandLine
    {
        // TODO: The SteamId should be determined at the initial handshake, so it doesn't need to be known pre-connect.
        // but then, what do we pass as fakeSteamId?
        public static bool ParseIpConnect(string arg, out string address, out ushort port, out CSteamID fakeSteamId)
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

        public static bool ParseDedicated(ICollection<string> args, out ListenArguments listenArguments)
        {
            var portArg = args.FirstOrDefault(x => x.StartsWith("--dedicated-port="));
            if (portArg == null)
            {
                listenArguments = null;
                return false;
            }

            if (!ushort.TryParse(portArg.Substring("--dedicated-port=".Length), out var port))
            {
                Debug.LogError($"Invalid dedicated port specified. Check your syntax near {portArg}");
                listenArguments = null;
                return false;
            }

            listenArguments = new ListenArguments(port)
            {
                GsUserToken = ExtractArg(args, "--dedicated-with-user-token="),
                ListenIp = ExtractArg(args, "--dedicated-with-listen-ip=")
            };
            //listenArguments.AdvertiseInSteamBrowser = ExtractArg("--dedicated-steam-advertise=")
            
            return true;
        }

        private static string ExtractArg(IEnumerable<string> args, string argName)
        {
            return args.Where(x => x.StartsWith(argName)).Select(x => x.Substring(argName.Length)).FirstOrDefault();
        }
    }
}
