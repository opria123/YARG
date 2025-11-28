using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using YARG.Core.Song;
using YARG.Multiplayer;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Adapter that wraps YargNetworkManager (Mirror implementation) to conform to INetworkingService.
    /// This allows existing Mirror networking to work through the abstraction layer without changes.
    /// </summary>
    public class MirrorNetworkingAdapter : INetworkingService
    {
        private YargNetworkManager _mirrorManager;

        #region Properties

        public int MaxPlayers => _mirrorManager?.MaxPlayers ?? 0;

        public int MaxLocalPlayersPerClient => _mirrorManager?.MaxLocalPlayersPerClient ?? 0;

        public LobbyInfo CurrentLobby
        {
            get
            {
                if (_mirrorManager == null || _mirrorManager.CurrentLobby.lobbyId == null)
                    return null;

                // Convert Mirror's LobbyInfo to our abstraction's LobbyInfo
                var mirrorLobby = _mirrorManager.CurrentLobby;
                return new LobbyInfo
                {
                    LobbyId = mirrorLobby.lobbyId,
                    LobbyName = mirrorLobby.lobbyName,
                    HostName = mirrorLobby.hostName,
                    CurrentPlayers = mirrorLobby.currentPlayers,
                    MaxPlayers = mirrorLobby.maxPlayers,
                    PrivacyMode = (LobbyPrivacyMode)(int)mirrorLobby.privacyMode,
                    HasPassword = mirrorLobby.hasPassword,
                    Password = mirrorLobby.password,
                    IsActive = mirrorLobby.isActive,
                    IpAddress = mirrorLobby.ipAddress,
                    Port = mirrorLobby.port,
                    PublicPort = mirrorLobby.publicPort,
                    PublicAddress = mirrorLobby.publicAddress,
                    TransportId = mirrorLobby.transportId,
                    PlayerNames = mirrorLobby.playerNames,
                    PlayerInstruments = mirrorLobby.playerInstruments
                };
            }
        }

        public string PlayerName => _mirrorManager?.PlayerName ?? string.Empty;

        public bool IsHosting => _mirrorManager?.IsHosting ?? false;

        public bool IsDedicatedServer => _mirrorManager?.IsDedicatedServer ?? false;

        public bool IsConnected => _mirrorManager?.IsConnected ?? false;

        public bool IsJoinInProgress => _mirrorManager?.IsJoinInProgress ?? false;

        public int DefaultPort => _mirrorManager?.DefaultPort ?? 7777;

        #endregion

        #region Events

        public event Action<LobbyInfo> OnLobbyCreated;
        public event Action<LobbyInfo> OnLobbyJoined;
        public event Action OnLobbyLeft;
        public event Action<List<LobbyInfo>> OnLobbyListUpdated;
        public event Action<NetworkPlayerData> OnPlayerJoined;
        public event Action<NetworkPlayerData> OnPlayerLeft;
        public event Action<string> OnNetworkError;

        #endregion

        #region Initialization

        public void Initialize()
        {
            // Find the existing YargNetworkManager singleton
            _mirrorManager = YargNetworkManager.Instance;

            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] YargNetworkManager.Instance is null! Mirror networking will not work.");
                return;
            }

            // Subscribe to Mirror events and forward them through our abstraction
            _mirrorManager.OnLobbyCreated += HandleMirrorLobbyCreated;
            _mirrorManager.OnLobbyJoined += HandleMirrorLobbyJoined;
            _mirrorManager.OnLobbyLeft += HandleMirrorLobbyLeft;
            _mirrorManager.OnLobbyListUpdated += HandleMirrorLobbyListUpdated;
            _mirrorManager.OnPlayerJoined += HandleMirrorPlayerJoined;
            _mirrorManager.OnPlayerLeft += HandleMirrorPlayerLeft;
            _mirrorManager.OnNetworkError += HandleMirrorNetworkError;

            Debug.Log("[MirrorNetworkingAdapter] Initialized and subscribed to YargNetworkManager events");
        }

        public void Shutdown()
        {
            if (_mirrorManager != null)
            {
                // Unsubscribe from events
                _mirrorManager.OnLobbyCreated -= HandleMirrorLobbyCreated;
                _mirrorManager.OnLobbyJoined -= HandleMirrorLobbyJoined;
                _mirrorManager.OnLobbyLeft -= HandleMirrorLobbyLeft;
                _mirrorManager.OnLobbyListUpdated -= HandleMirrorLobbyListUpdated;
                _mirrorManager.OnPlayerJoined -= HandleMirrorPlayerJoined;
                _mirrorManager.OnPlayerLeft -= HandleMirrorPlayerLeft;
                _mirrorManager.OnNetworkError -= HandleMirrorNetworkError;
            }

            _mirrorManager = null;
        }

        public void Update()
        {
            // Mirror handles its own update loop via MonoBehaviour
            // No additional work needed here
        }

        #endregion

        #region Event Handlers (Mirror -> Abstraction)

        private void HandleMirrorLobbyCreated(YargNetworkManager.LobbyInfo mirrorLobby)
        {
            var abstractionLobby = ConvertMirrorLobbyInfo(mirrorLobby);
            OnLobbyCreated?.Invoke(abstractionLobby);
        }

        private void HandleMirrorLobbyJoined(YargNetworkManager.LobbyInfo mirrorLobby)
        {
            var abstractionLobby = ConvertMirrorLobbyInfo(mirrorLobby);
            OnLobbyJoined?.Invoke(abstractionLobby);
        }

        private void HandleMirrorLobbyLeft()
        {
            OnLobbyLeft?.Invoke();
        }

        private void HandleMirrorLobbyListUpdated(List<YargNetworkManager.LobbyInfo> mirrorLobbies)
        {
            var abstractionLobbies = mirrorLobbies.Select(ConvertMirrorLobbyInfo).ToList();
            OnLobbyListUpdated?.Invoke(abstractionLobbies);
        }

        private void HandleMirrorPlayerJoined(NetworkPlayerData player)
        {
            OnPlayerJoined?.Invoke(player);
        }

        private void HandleMirrorPlayerLeft(NetworkPlayerData player)
        {
            OnPlayerLeft?.Invoke(player);
        }

        private void HandleMirrorNetworkError(string error)
        {
            OnNetworkError?.Invoke(error);
        }

        #endregion

        #region Lobby Management

        public LobbyInfo CreateLobby(string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, string password = "")
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot create lobby: YargNetworkManager is null");
                return null;
            }

            // Convert abstraction enum to Mirror enum
            var mirrorPrivacyMode = (YargNetworkManager.LobbyPrivacyMode)(int)privacyMode;

            // Call Mirror's CreateLobby and convert result
            var mirrorLobby = _mirrorManager.CreateLobby(lobbyName, maxPlayers, mirrorPrivacyMode, password);
            return ConvertMirrorLobbyInfo(mirrorLobby);
        }

        public void JoinLobby(string endpoint, string password = "")
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot join lobby: YargNetworkManager is null");
                return;
            }

            _mirrorManager.JoinLobby(endpoint, password);
        }

        public void JoinDiscoveredLobby(LobbyInfo lobby, string password = "")
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot join discovered lobby: YargNetworkManager is null");
                return;
            }

            // Convert abstraction LobbyInfo back to Mirror LobbyInfo
            var mirrorLobby = ConvertToMirrorLobbyInfo(lobby);
            _mirrorManager.JoinDiscoveredLobby(mirrorLobby, password);
        }

        public void LeaveLobby()
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot leave lobby: YargNetworkManager is null");
                return;
            }

            _mirrorManager.LeaveLobby();
        }

        public async Task<LobbyInfo?> ProbeLobby(string address, int port)
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot probe lobby: YargNetworkManager is null");
                return null;
            }

            // Mirror's ProbeLobbyAsync returns LobbyInfo? via UniTask
            var mirrorResult = await _mirrorManager.ProbeLobbyAsync(address, port);
            
            if (mirrorResult.HasValue)
            {
                return ConvertMirrorLobbyInfo(mirrorResult.Value);
            }

            return null;
        }

        #endregion

        #region Player Management

        public void SetPlayerName(string name)
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot set player name: YargNetworkManager is null");
                return;
            }

            _mirrorManager.SetPlayerName(name);
        }

        public Dictionary<object, List<NetworkPlayerData>> GetConnectedPlayers()
        {
            if (_mirrorManager == null)
            {
                return new Dictionary<object, List<NetworkPlayerData>>();
            }

            // Convert Mirror's Dictionary<NetworkConnectionToClient, List<NetworkPlayerData>>
            // to Dictionary<object, List<NetworkPlayerData>> for abstraction
            var connectedPlayers = _mirrorManager.ConnectedPlayers;
            var result = new Dictionary<object, List<NetworkPlayerData>>();

            foreach (var kvp in connectedPlayers)
            {
                result[kvp.Key] = kvp.Value;
            }

            return result;
        }

        #endregion

        #region Song Selection and Gameplay

        public void StartSongSelection()
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot start song selection: YargNetworkManager is null");
                return;
            }

            _mirrorManager.StartSongSelection();
        }

        public void StartMultiplayerSong(SongEntry song)
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot start multiplayer song: YargNetworkManager is null");
                return;
            }

            _mirrorManager.StartMultiplayerSong(song);
        }

        public void StartMultiplayerGameplay()
        {
            if (_mirrorManager == null)
            {
                Debug.LogError("[MirrorNetworkingAdapter] Cannot start multiplayer gameplay: YargNetworkManager is null");
                return;
            }

            _mirrorManager.StartMultiplayerGameplay();
        }

        #endregion

        #region Helper Methods

        private LobbyInfo ConvertMirrorLobbyInfo(YargNetworkManager.LobbyInfo mirrorLobby)
        {
            return new LobbyInfo
            {
                LobbyId = mirrorLobby.lobbyId,
                LobbyName = mirrorLobby.lobbyName,
                HostName = mirrorLobby.hostName,
                CurrentPlayers = mirrorLobby.currentPlayers,
                MaxPlayers = mirrorLobby.maxPlayers,
                PrivacyMode = (LobbyPrivacyMode)(int)mirrorLobby.privacyMode,
                HasPassword = mirrorLobby.hasPassword,
                Password = mirrorLobby.password,
                IsActive = mirrorLobby.isActive,
                IpAddress = mirrorLobby.ipAddress,
                Port = mirrorLobby.port,
                PublicPort = mirrorLobby.publicPort,
                PublicAddress = mirrorLobby.publicAddress,
                TransportId = mirrorLobby.transportId,
                PlayerNames = mirrorLobby.playerNames,
                PlayerInstruments = mirrorLobby.playerInstruments
            };
        }

        private YargNetworkManager.LobbyInfo ConvertToMirrorLobbyInfo(LobbyInfo abstractionLobby)
        {
            return new YargNetworkManager.LobbyInfo
            {
                lobbyId = abstractionLobby.LobbyId,
                lobbyName = abstractionLobby.LobbyName,
                hostName = abstractionLobby.HostName,
                currentPlayers = abstractionLobby.CurrentPlayers,
                maxPlayers = abstractionLobby.MaxPlayers,
                privacyMode = (YargNetworkManager.LobbyPrivacyMode)(int)abstractionLobby.PrivacyMode,
                hasPassword = abstractionLobby.HasPassword,
                password = abstractionLobby.Password,
                isActive = abstractionLobby.IsActive,
                ipAddress = abstractionLobby.IpAddress,
                port = abstractionLobby.Port,
                publicPort = abstractionLobby.PublicPort,
                publicAddress = abstractionLobby.PublicAddress,
                transportId = abstractionLobby.TransportId,
                playerNames = abstractionLobby.PlayerNames,
                playerInstruments = abstractionLobby.PlayerInstruments
            };
        }

        #endregion
    }
}
