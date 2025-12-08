using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Net.Utilities;

namespace YARG.Networking
{
    /// <summary>
    /// Unity wrapper around YARG.Net.Utilities.StunResolver providing UniTask support.
    /// </summary>
    internal static class StunUtility
    {
        /// <summary>
        /// Attempts to resolve the public IP address using STUN servers.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The public IP address string, or empty string if resolution failed.</returns>
        public static async UniTask<string> TryResolvePublicAddressAsync(CancellationToken token)
        {
            try
            {
                string result = await StunResolver.ResolvePublicAddressAsync(token);
                return result ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[StunUtility] STUN resolution failed: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
