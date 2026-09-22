using System;
using System.Collections;
using System.Collections.Concurrent;
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
    /// Nothing in the SDK needs this. It exists for four reasons a plain object cannot cover:
    /// configuration in the Inspector, a coroutine API for teams not using <c>async</c>, flushing
    /// state when the application is backgrounded — the last callback a mobile game reliably gets
    /// before the OS kills it — and, the one that turns out to matter most, <b>marshalling the
    /// vault's events back onto Unity's main thread</b>.
    /// </para>
    /// <para>
    /// That last one is not a convenience. <see cref="Vault"/> is a plain C# object and raises its
    /// events on whichever thread finished the work; a claim resolves on the thread pool, so a
    /// handler that updates a HUD label would reach <c>Graphic.SetVerticesDirty</c> and throw
    /// <c>get_isActiveAndEnabled can only be called from the main thread</c>. Every Unity consumer
    /// would otherwise have to write this pump itself, so the SDK writes it once.
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

        /// <summary>
        /// Work handed back from background threads, drained in <see cref="Update"/>. A queue rather
        /// than a captured <c>SynchronizationContext</c> because Unity's context is not reliably
        /// installed yet during the first scene's <c>Awake</c>, which is exactly when the vault opens.
        /// </summary>
        readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        Task<Vault> _opening;
        Vault _pending;

        /// <summary>
        /// The wrapped vault. Null until the open has completed <i>and</i> been published on the main
        /// thread, so reading it from game code is always safe.
        /// </summary>
        public Vault Vault { get; private set; }

        public bool IsOpen => Vault != null;

        /// <summary>Raised on the main thread once the vault has loaded and is safe to use.</summary>
        public event Action<Vault> Opened;

        /// <summary><see cref="Vault.BalanceChanged"/>, re-raised on the main thread.</summary>
        public event Action<string, long> BalanceChanged;

        /// <summary><see cref="Vault.ClaimStateChanged"/>, re-raised on the main thread.</summary>
        public event Action<ClaimRecord> ClaimStateChanged;

        async void Awake()
        {
            if (openOnAwake) await OpenAsync();
        }

        public Task<Vault> OpenAsync()
        {
            // Returning the in-flight task rather than starting a second open: two vaults over one
            // storage file would race each other's writes.
            return _opening ??= OpenCoreAsync();
        }

        async Task<Vault> OpenCoreAsync()
        {
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

            var vault = await Vault.OpenAsync(config);

            vault.BalanceChanged += OnVaultBalanceChanged;
            vault.ClaimStateChanged += OnVaultClaimStateChanged;

            // Published through the pump, not assigned here: Vault must not become non-null on a
            // background thread, or a caller polling IsOpen would start touching the engine from one.
            _pending = vault;
            RunOnMainThread(PublishOpened);

            return vault;
        }

        void PublishOpened()
        {
            if (_pending == null) return;

            Vault = _pending;
            _pending = null;
            Opened?.Invoke(Vault);
        }

        void OnVaultBalanceChanged(string resource, long balance) =>
            RunOnMainThread(() => BalanceChanged?.Invoke(resource, balance));

        void OnVaultClaimStateChanged(ClaimRecord record) =>
            RunOnMainThread(() => ClaimStateChanged?.Invoke(record));

        /// <summary>
        /// Queues an action to run on Unity's main thread on the next frame. Public because a game
        /// that awaits <see cref="Vault.ClaimAsync"/> directly resumes on the thread pool and needs
        /// the same escape hatch the wrapper uses internally.
        /// </summary>
        public void RunOnMainThread(Action action)
        {
            if (action != null) _mainThread.Enqueue(action);
        }

        void Update()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                // One bad handler must not stall the queue behind it.
                try { action(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        /// <summary>
        /// Coroutine form of <see cref="Vault.ClaimAsync"/>, for codebases that have not moved to
        /// async/await — and the simplest way to get a claim result on the main thread, since a
        /// coroutine is driven by Unity's own loop.
        /// </summary>
        public IEnumerator ClaimRoutine(string rewardId, string resource, long amount, Action<ClaimResult> onComplete = null)
        {
            if (!IsOpen) yield return OpenRoutine();

            var task = Vault.ClaimAsync(rewardId, resource, amount);
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            onComplete?.Invoke(task.Result);
        }

        /// <summary>Yields until the vault is open. Useful from a game's own start-up coroutine.</summary>
        public IEnumerator OpenRoutine()
        {
            var task = OpenAsync();
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            // The task completing is not enough — Vault is published by the pump, a frame later.
            while (!IsOpen) yield return null;
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

        void OnDestroy()
        {
            var vault = Vault ?? _pending;
            if (vault == null) return;

            vault.BalanceChanged -= OnVaultBalanceChanged;
            vault.ClaimStateChanged -= OnVaultClaimStateChanged;
            vault.Dispose();
        }
    }
}
