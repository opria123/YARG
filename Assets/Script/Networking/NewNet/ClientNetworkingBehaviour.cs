using System;
using UnityEngine;

namespace YARG.Networking.NewNet
{
    /// <summary>
    /// Simple MonoBehaviour that ensures <see cref="ClientNetworkingService"/> is initialized early.
    /// </summary>
    [DefaultExecutionOrder(-450)]
    public sealed class ClientNetworkingBehaviour : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Automatically initialize the networking service during Awake().")]
        private bool initializeOnAwake = true;

        [SerializeField]
        [Tooltip("Keep this behaviour alive between scene loads so the client runtime persists.")]
        private bool dontDestroyOnLoad = true;

        private bool _initialized;

        private void Awake()
        {
            if (!initializeOnAwake)
            {
                return;
            }

            TryInitializeService();
        }

        private void OnEnable()
        {
            if (!_initialized && initializeOnAwake)
            {
                TryInitializeService();
            }
        }

        private void OnApplicationQuit()
        {
            if (ClientNetworkingService.HasInstance)
            {
                ClientNetworkingService.Instance.Dispose();
            }
        }

        private void TryInitializeService()
        {
            try
            {
                ClientNetworkingService.Instance.Initialize();
                if (dontDestroyOnLoad)
                {
                    DontDestroyOnLoad(gameObject);
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ClientNetworkingBehaviour] Failed to initialize networking service: {ex.Message}");
            }
        }
    }
}
