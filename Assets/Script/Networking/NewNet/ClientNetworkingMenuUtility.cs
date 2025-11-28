using System;
using UnityEngine;
using YARG.Localization;
using YARG.Menu.Persistent;
using YARG.Networking;
using YARG.Net.Runtime;
using YARG.Player;

namespace YARG.Networking.NewNet
{
    /// <summary>
    /// Shared helpers for menu flows that drive <see cref="ClientNetworkingService"/>.
    /// </summary>
    internal static class ClientNetworkingMenuUtility
    {
        public static bool IsServiceAvailable
        {
            get
            {
                if (!ClientNetworkingService.HasInstance)
                {
                    _ = ClientNetworkingService.Instance;
                }

                return ClientNetworkingService.HasInstance;
            }
        }

        public static ClientConnectionParameters BuildParameters(string host, int port, string? password)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host must be provided.", nameof(host));
            }

            if (port <= 0 || port > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
            }

            string playerName = ResolvePlayerName();
            string? sanitizedPassword = string.IsNullOrWhiteSpace(password) ? null : password.Trim();
            return new ClientConnectionParameters(host.Trim(), port, playerName, sanitizedPassword, Application.version);
        }

        public static void ShowConnectingDialog(string endpoint)
        {
            if (DialogManager.Instance == null || DialogManager.Instance.IsDialogShowing)
            {
                return;
            }

            DialogManager.Instance.ShowMessage(
                Localize.Key("Menu", "LobbyBrowser", "ConnectingTitle"),
                Localize.KeyFormat(("Menu", "LobbyBrowser", "ConnectingDescription"), endpoint));
        }

        public static void ShowConnectionError(string? details)
        {
            if (DialogManager.Instance == null)
            {
                return;
            }

            string body = string.IsNullOrWhiteSpace(details)
                ? Localize.Key("Menu", "LobbyBrowser", "StatusNotConnected")
                : Localize.KeyFormat(("Menu", "LobbyBrowser", "StatusError"), details);

            DialogManager.Instance.ShowMessage(
                Localize.Key("Menu", "LobbyBrowser", "ConnectionErrorTitle"),
                body);
        }

        private static string ResolvePlayerName()
        {
            if (YargNetworkManager.Instance != null && !string.IsNullOrWhiteSpace(YargNetworkManager.Instance.PlayerName))
            {
                return YargNetworkManager.Instance.PlayerName;
            }

            foreach (var player in PlayerContainer.Players)
            {
                if (player?.Profile != null && !string.IsNullOrWhiteSpace(player.Profile.Name))
                {
                    return player.Profile.Name;
                }
            }

            return "Player";
        }
    }
}
