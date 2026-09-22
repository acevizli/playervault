using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault
{
    /// <summary>
    /// Optional convenience component. Owns a <see cref="Vault"/>, configures it from the Inspector,
    /// and re-raises its events on Unity's main thread.
    /// </summary>
    /// <remarks>
    /// Nothing in the SDK needs this. It exists for four reasons a plain object cannot cover:
    /// configuration in the Inspector, a coroutine API for teams not using async, flushing
    /// state when the application is backgrounded — the last callback a mobile game reliably gets
    /// before the OS kills it — and, the one that turns out to matter most, marshalling the
    /// vault's events back onto Unity's main thread. <see cref="Vault"/> raises its events on
    /// whichever thread finished the work, so a handler that updates a HUD label would reach
    /// Graphic.SetVerticesDirty and throw <c>get_isActiveAndEnabled can only be called from the
    /// main thread</c>. Every Unity consumer would otherwise have to write this pump itself, so
    /// the SDK writes it once.
    /// <para>
    /// Every seam on <see cref="VaultConfig"/> that a shipping game plausibly tunes is mirrored
    /// here as a serialized field. A convenience layer that hides the knobs is not a convenience:
    /// it forces the first team that needs a shorter timeout to abandon the component entirely,
    /// and with it the main-thread pump and the background flush.
    /// </para>
    /// </remarks>
    [AddComponentMenu("PlayerVault/Vault Behaviour")]
    public sealed class VaultBehaviour : MonoBehaviour
    {
        /// <summary>
        /// Inspector form of <see cref="ResourceDefinition"/>. Unity cannot serialize
        /// <c>long?</c>, so the optional maximum is split into a flag and a value.
        /// </summary>
        [Serializable]
        public sealed class ResourceSetting
        {
            public string key;
            public long initial;
            public bool hasMax;
            public long max;
        }

        [Header("Player")]
        [Tooltip("Identifies the player. Also keys the storage file, so changing it switches ledger.")]
        [SerializeField] string playerId = "test-player";

        [Header("Backend")]
        [SerializeField] string apiUrl = "https://httpbin.org/anything";

        [Header("Resources")]
        [SerializeField] List<ResourceSetting> resources = new List<ResourceSetting>();

        [Header("Behaviour")]
        [Tooltip("Open the vault in Awake. Turn off to open it yourself once a player id is known.")]
        [SerializeField] bool openOnAwake = true;

        [Tooltip("Let resources not listed above spring into existence on first use. Off catches typos.")]
        [SerializeField] bool allowUndeclaredResources;

        [Tooltip("Flush to disk when the app is backgrounded — the last callback a mobile game reliably gets.")]
        [SerializeField] bool flushOnApplicationPause = true;

        [Header("Reliability")]
        [Tooltip("Total attempts per session for a claim, including the first.")]
        [SerializeField, Min(1)] int maxAttempts = 3;

        [Tooltip("Per-attempt request timeout, in seconds. Generous by design: a timeout is an " +
                 "indeterminate outcome, which is the one thing the SDK works hardest to avoid.")]
        [SerializeField, Min(0.1f)] float requestTimeoutSeconds = 15f;

        [Tooltip("Delay before the second attempt, in seconds. Doubles thereafter.")]
        [SerializeField, Min(0f)] float retryBaseDelaySeconds = 0.5f;

        [Tooltip("Upper bound on any single backoff wait, in seconds.")]
        [SerializeField, Min(0f)] float retryMaxDelaySeconds = 10f;

        [Tooltip("Random spread applied to each delay. 0.25 means +/-25%, so a crowd of clients " +
                 "does not retry in lockstep after an outage.")]
        [SerializeField, Range(0f, 1f)] float retryJitter = 0.25f;

        [Tooltip("Replay unfinished claims when the vault opens. Runs detached; opening never blocks.")]
        [SerializeField] bool resumePendingOnOpen = true;

        [Header("Storage")]
        [Tooltip("Immediate writes on every mutation. Debounced coalesces writes inside the interval below.")]
        [SerializeField] FlushMode flushMode = FlushMode.Immediate;

        [Tooltip("Only used when Flush Mode is Debounced.")]
        [SerializeField, Min(0.05f)] float debounceIntervalSeconds = 1f;

        [Tooltip("Quarantine moves an unreadable save aside and starts fresh. Throw fails loudly at open.")]
        [SerializeField] CorruptDataPolicy onCorruptData = CorruptDataPolicy.Quarantine;

        /// <summary>
        /// Work handed back from background threads, drained in <see cref="Update"/>. A queue rather
        /// than a captured SynchronizationContext because Unity's context is not reliably installed
        /// yet during the first scene's Awake, which is exactly when the vault opens.
        /// </summary>
        readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        Task<Vault> _opening;
        Vault _pending;

        /// <summary>The vault, or null until it has finished opening.</summary>
        public Vault Vault { get; private set; }

        public bool IsOpen => Vault != null;

        /// <summary>Raised on the main thread once the vault is open and safe to use.</summary>
        public event Action<Vault> Opened;

        /// <summary><see cref="Vault.BalanceChanged"/>, re-raised on the main thread.</summary>
        public event Action<string, long> BalanceChanged;

        /// <summary><see cref="Vault.ClaimStateChanged"/>, re-raised on the main thread.</summary>
        public event Action<ClaimRecord> ClaimStateChanged;

        async void Awake()
        {
            if (openOnAwake) await OpenAsync();
        }

        /// <summary>Opens the vault, or returns the open already in flight.</summary>
        public Task<Vault> OpenAsync()
        {
            // Returning the in-flight task rather than starting a second open: two vaults over one
            // storage file would race each other's writes.
            return _opening ??= OpenCoreAsync();
        }

        async Task<Vault> OpenCoreAsync()
        {
            var vault = await Vault.OpenAsync(BuildConfig());
            vault.BalanceChanged += OnVaultBalanceChanged;
            vault.ClaimStateChanged += OnVaultClaimStateChanged;

            // Published through the pump, not assigned here: Vault must not become non-null on a
            // background thread, or a caller polling IsOpen would start touching the engine from one.
            _pending = vault;
            RunOnMainThread(PublishOpened);
            return vault;
        }

        /// <summary>Translates the Inspector fields into a <see cref="VaultConfig"/>.</summary>
        VaultConfig BuildConfig()
        {
            var config = new VaultConfig
            {
                PlayerId = playerId,
                ApiUrl = apiUrl,
                AllowUndeclaredResources = allowUndeclaredResources,
                ResumePendingOnOpen = resumePendingOnOpen,
                OnCorruptData = onCorruptData,
                FlushMode = flushMode,
                DebounceInterval = TimeSpan.FromSeconds(debounceIntervalSeconds),
                Retry = new RetryPolicy
                {
                    MaxAttempts = maxAttempts,
                    Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds),
                    BaseDelay = TimeSpan.FromSeconds(retryBaseDelaySeconds),
                    MaxDelay = TimeSpan.FromSeconds(retryMaxDelaySeconds),
                    Jitter = retryJitter
                }
            };

            foreach (var setting in resources)
            {
                config.Resources.Add(new ResourceDefinition(
                    setting.key, setting.initial, setting.hasMax ? setting.max : (long?)null));
            }

            // Transport, Storage, Clock and Logger are deliberately not exposed here. They are
            // constructor seams for tests and custom backends, not values a designer sets in a
            // scene; a game that needs them builds its own VaultConfig and skips this component.
            return config;
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

        /// <summary>Queues an action to run on the main thread at the next Update.</summary>
        public void RunOnMainThread(Action action)
        {
            if (action != null) _mainThread.Enqueue(action);
        }

        void Update()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception exception) { Debug.LogException(exception); }  // One bad handler must not stall the queue.
            }
        }

        /// <summary>Coroutine form of <see cref="Vault.ClaimAsync"/>. The callback runs on the main thread.</summary>
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

        /// <summary>
        /// Coroutine form of <see cref="Vault.SpendAsync"/>: the callback runs once the deduction is
        /// on disk. Use it for a purchase, where handing over the goods before the write lands would
        /// let a crash give them away for free.
        /// </summary>
        public IEnumerator SpendRoutine(string resource, long amount, Action<SpendResult> onComplete = null)
        {
            if (!IsOpen) yield return OpenRoutine();

            var task = Vault.SpendAsync(resource, amount);
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            onComplete?.Invoke(task.Result);
        }

        /// <summary>Coroutine form of <see cref="Vault.ResumePendingAsync"/> — retry unfinished claims now.</summary>
        public IEnumerator ResumePendingRoutine(Action onComplete = null)
        {
            if (!IsOpen) yield return OpenRoutine();

            var task = Vault.ResumePendingAsync();
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            onComplete?.Invoke();
        }

        /// <summary>Waits for the vault to be open and published on the main thread.</summary>
        public IEnumerator OpenRoutine()
        {
            var task = OpenAsync();
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            while (!IsOpen) yield return null;   // Vault is published by the pump, a frame later.
        }

        void OnApplicationPause(bool paused)
        {
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
