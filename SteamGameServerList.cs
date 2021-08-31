using System;
using Steamworks;
using UnityEngine;

namespace SharkkitDedicated
{
    public class SteamGameServerList
    {
        public static CSteamID ServerId => SteamGameServer.GetSteamID();

        private Callback<SteamServersConnected_t> _connectedCallback;
        private Callback<SteamServersDisconnected_t> disconnectedCallback;
        private Callback<SteamServerConnectFailure_t> connectFailureCallback;
        private Callback<ValidateAuthTicketResponse_t> authTicketResponse;
        private Callback<GSPolicyResponse_t> policyResponse;

        public bool ConnectedToSteam { get; private set; }

        public void Init()
        {
            if (_connectedCallback != null)
            {
                return;
            }

            _connectedCallback = Callback<SteamServersConnected_t>.CreateGameServer(t =>
            {
                Debug.Log("Successfully connected to Steam");
                ConnectedToSteam = true;
                Debug.Log(ServerId);

                UpdateGameInformation();
            });

            disconnectedCallback = Callback<SteamServersDisconnected_t>.CreateGameServer(t =>
            {
                Debug.Log($"Disconnected from Steam: {t.m_eResult}");
                ConnectedToSteam = false;
            });

            connectFailureCallback = Callback<SteamServerConnectFailure_t>.CreateGameServer(t =>
            {
                Debug.Log(
                    $"Failure when trying to connect to Steam: {t.m_eResult}, still trying: {t.m_bStillRetrying}");
            });

            authTicketResponse = Callback<ValidateAuthTicketResponse_t>.CreateGameServer(t =>
            {
                Debug.Log($"Auth response! {t.m_eAuthSessionResponse}, id {t.m_SteamID}, owner {t.m_OwnerSteamID}");
            });

            policyResponse = Callback<GSPolicyResponse_t>.CreateGameServer(t =>
            {
                // VAC: t.m_bSecure
            });
        }

        public bool RegisterServer(ushort gamePort = 1337, ushort queryPort = 1336, string gsUserToken = null)
        {
            const int appId = 648800;
            Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());
            if (!GameServer.Init(0 /* ANY */, 8765 /* outgoing port */, gamePort, queryPort,
                EServerMode.eServerModeAuthenticationAndSecure, "1.0.0.0"))
            {
                return false;
            }
            
            SteamGameServer.SetModDir("spacewar"); // TODO: RaftDedicated? SharkkitDedicated?
            SteamGameServer.SetProduct("SharkkitDedicated");
            SteamGameServer.SetGameDescription("Sharkkit Dedicated");

            if (gsUserToken == null)
            {
                SteamGameServer.LogOnAnonymous();
            }
            else
            {
                SteamGameServer.LogOn(gsUserToken);
            }

            SteamGameServer.EnableHeartbeats(true);
            return true;
        }

        public EBeginAuthSessionResult BeginAuthentication(byte[] authTicket, CSteamID steamId)
        {
            return SteamGameServer.BeginAuthSession(authTicket, authTicket.Length, steamId);
        }

        public void EndAuthentication(CSteamID steamID)
        {
            SteamGameServer.EndAuthSession(steamID);
        }

        public void Tick()
        {
            // TODO: Register callbacks for auth....
            GameServer.RunCallbacks();
            UpdateGameInformation();
        }

        public void UpdateGameInformation()
        {
            // TODO: First called when steam callback is there? What happens if we call that too early?
            SteamGameServer.SetMaxPlayerCount(5);
            SteamGameServer.SetPasswordProtected(false);
            SteamGameServer.SetServerName("Raft Test Dedicated Server");
            SteamGameServer.SetDedicatedServer(true);
            SteamGameServer.SetMapName("Pacific Ocean");
            SteamGameServer.SetGameTags("sharkkit");
        }

        public void Shutdown()
        {
            SteamGameServer.LogOff();
            GameServer.Shutdown();
        }
    } 
}
