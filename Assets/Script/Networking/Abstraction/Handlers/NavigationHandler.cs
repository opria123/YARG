using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Menu;
using YARG.Net;
using YARG.Net.Packets;
using YARG.Net.Transport;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles menu navigation and browsing state synchronization between host and clients.
    /// </summary>
    public sealed class NavigationHandler : IDisposable
    {
        private bool _isBrowsingSongs;
        private bool _isHosting;
        
        /// <summary>
        /// Fired when the browsing state changes.
        /// </summary>
        public event Action<bool> OnBrowsingStateChanged;
        
        /// <summary>
        /// Fired when a navigation command is received.
        /// </summary>
        public event Action<MenuTarget> OnNavigateToMenu;

        #region Properties

        public bool IsBrowsingSongs => _isBrowsingSongs;

        #endregion

        public void SetHostingState(bool isHosting)
        {
            _isHosting = isHosting;
        }

        #region Browsing State

        /// <summary>
        /// Set the browsing state (host only).
        /// </summary>
        public void SetBrowsingState(bool isBrowsing)
        {
            if (_isBrowsingSongs == isBrowsing) return;
            
            _isBrowsingSongs = isBrowsing;
            NetworkLogger.Info("Browsing state: {0}", isBrowsing);
            OnBrowsingStateChanged?.Invoke(isBrowsing);
        }

        /// <summary>
        /// Build a lobby state packet for broadcasting.
        /// </summary>
        public byte[] BuildLobbyStatePacket()
        {
            return NavigationBinaryPackets.BuildLobbyStatePacket(_isBrowsingSongs);
        }

        /// <summary>
        /// Handle incoming lobby state message (client only).
        /// </summary>
        public void HandleLobbyStateMessage(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting) return;
            
            if (!NavigationBinaryPackets.TryParseLobbyStatePacket(payload.Span, out bool isBrowsing))
            {
                NetworkLogger.Warn("Invalid lobby state message");
                return;
            }
            
            NetworkLogger.Client("Received lobby state, browsing={0}", isBrowsing);
            
            if (_isBrowsingSongs != isBrowsing)
            {
                _isBrowsingSongs = isBrowsing;
                OnBrowsingStateChanged?.Invoke(isBrowsing);
            }
        }

        #endregion

        #region Navigation

        /// <summary>
        /// Build a navigate to menu packet for broadcasting.
        /// </summary>
        public byte[] BuildNavigatePacket(MenuTarget target)
        {
            return NavigationBinaryPackets.BuildNavigatePacket(target);
        }

        /// <summary>
        /// Build a host disconnect notification packet.
        /// </summary>
        public byte[] BuildHostDisconnectPacket()
        {
            return NavigationBinaryPackets.BuildHostDisconnectPacket();
        }

        /// <summary>
        /// Handle incoming navigate to menu message (client only).
        /// </summary>
        public void HandleNavigateToMenuMessage(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting) return;
            
            if (!NavigationBinaryPackets.TryParseNavigatePacket(payload.Span, out var menuTarget))
            {
                NetworkLogger.Warn("Invalid navigate message");
                return;
            }
            
            NetworkLogger.Client("Received navigate to menu command, target={0}", menuTarget);
            OnNavigateToMenu?.Invoke(menuTarget);
        }

        /// <summary>
        /// Handle navigation to a menu target (performs actual Unity navigation).
        /// </summary>
        public void NavigateToMenu(MenuTarget target, Action onNavigateToMusicLibrary = null, Action onNavigateToLobby = null)
        {
            switch (target)
            {
                case MenuTarget.MusicLibrary:
                    NavigateToMusicLibrary();
                    onNavigateToMusicLibrary?.Invoke();
                    break;
                    
                case MenuTarget.LobbyRoom:
                    NavigateToLobbyRoom();
                    onNavigateToLobby?.Invoke();
                    break;
                    
                default:
                    NetworkLogger.Warn("Unknown menu target: {0}", target);
                    break;
            }
        }

        private void NavigateToMusicLibrary()
        {
            NetworkLogger.Info("Navigating to Music Library");
            
            if (MenuManager.Instance != null)
            {
                // Check if we're already on the music library to avoid duplicate pushes
                if (MenuManager.Instance.CurrentMenu != MenuManager.Menu.MusicLibrary)
                {
                    MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary);
                }
            }
            else
            {
                NetworkLogger.Warn("MenuManager.Instance is null, cannot navigate to music library");
            }
        }

        private void NavigateToLobbyRoom()
        {
            NetworkLogger.Info("Navigating to Lobby Room");
            
            if (MenuManager.Instance != null)
            {
                // Pop back to lobby room
                while (MenuManager.Instance.MenuStackCount > 1 && 
                       MenuManager.Instance.CurrentMenu != MenuManager.Menu.LobbyRoom)
                {
                    MenuManager.Instance.PopMenu();
                }
            }
            else
            {
                NetworkLogger.Warn("MenuManager.Instance is null, cannot navigate to lobby room");
            }
        }

        /// <summary>
        /// Navigate to difficulty select after a delay (used for advancing to next song).
        /// </summary>
        public async UniTaskVoid DelayedNavigateToDifficultySelect()
        {
            // Wait for the menu scene to fully load
            await UniTask.Delay(500);
            await UniTask.SwitchToMainThread();
            
            NetworkLogger.Info("Navigating to difficulty select for next song");
            
            if (MenuManager.Instance != null)
            {
                MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
            }
            else
            {
                NetworkLogger.Warn("MenuManager.Instance is null, cannot navigate to difficulty select");
            }
        }

        #endregion

        #region Handle Host Disconnect

        /// <summary>
        /// Handle host disconnect notification (client only).
        /// </summary>
        public void HandleHostDisconnectMessage(Action onDisconnected)
        {
            NetworkLogger.Client("Received host disconnect notification");
            
            _isBrowsingSongs = false;
            onDisconnected?.Invoke();
        }

        #endregion

        public void Reset()
        {
            _isBrowsingSongs = false;
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
