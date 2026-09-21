using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault
{
    /// <summary>
    /// An optional MonoBehaviour wrapper around <see cref="Vault"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in the SDK needs this. It exists for three reasons a plain object cannot
    /// cover: configuration in the Inspector, a coroutine API for teams not using
    /// <c>async</c>, and — the one that actually matters on mobile — flushing state when
    /// the application is backgrounded, which is the last callback a game reliably gets
    /// before the OS kills it.
    /// </para>
    /// </remarks>
    [AddComponentMenu("PlayerVault/Vault Behaviour")]
    public sealed class VaultBehaviour : MonoBehaviour
    {
        [Serializable]
        public sealed class ResourceSetting
        {
            public string key;
            public long initial;
            public bool hasMax;
            public long max;
        }

        [Header("Player")]
        [SerializeField] string playerId = "test-player";

        [Header("Backend")]
        [SerializeField] string apiUrl = "https://httpbin.org/anything";

        [Header("Resources")]
        [SerializeField] List<ResourceSetting> resources = new List<ResourceSetting>();

        [Header("Behaviour")]
        [SerializeField] bool openOnAwake = true;
        [SerializeField] bool allowUndeclaredResources;
        [SerializeField] bool flushOnApplicationPause = true;

        /// <summary>The wrapped vault. Null until <see cref="OpenAsync"/> has completed.</summary>
        public Vault Vault { get; private set; }

        /// <summary>Raised once the vault has loaded and is safe to use.</summary>
        public event Action<Vault> Opened;

        public bool IsOpen => Vault != null;

        async void Awake()
        {
            if (openOnAwake) await OpenAsync();
        }

        public async Task<Vault> OpenAsync()
        {
            if (Vault != null) return Vault;

            var config = new VaultConfig
            {
                PlayerId = playerId,
                ApiUrl = apiUrl,
                AllowUndeclaredResources = allowUndeclaredResources
            };

            foreach (var setting in resources)
                config.Resources.Add(new ResourceDefinition(
                    setting.key,
                    setting.initial,
                    setting.hasMax ? setting.max : (long?)null));

            Vault = await Vault.OpenAsync(config);
            Opened?.Invoke(Vault);
            return Vault;
        }

        /// <summary>
        /// Coroutine form of <see cref="Vault.ClaimAsync"/>, for codebases that have not
        /// moved to async/await.
        /// </summary>
        public IEnumerator ClaimRoutine(string rewardId, string resource, long amount, Action<ClaimResult> onComplete = null)
        {
            while (Vault == null) yield return null;

            var task = Vault.ClaimAsync(rewardId, resource, amount);
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            onComplete?.Invoke(task.Result);
        }

        void OnApplicationPause(bool paused)
        {
            // On mobile this is the last callback that reliably arrives before the OS is
            // free to kill the process, so it is the real save point — not OnApplicationQuit,
            // which Android and iOS frequently never deliver.
            if (paused && flushOnApplicationPause && Vault != null) _ = FlushQuietlyAsync();
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused && flushOnApplicationPause && Vault != null) _ = FlushQuietlyAsync();
        }

        async Task FlushQuietlyAsync()
        {
            try { await Vault.FlushAsync(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        void OnDestroy() => Vault?.Dispose();
    }
}
