using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault
{
    /// <summary>
    /// Optional component that owns a <see cref="Vault"/>, configures it from the Inspector or
    /// from code, and re-raises its events on Unity's main thread.
    /// </summary>
    /// <remarks>
    /// The SDK works without it. It adds Inspector configuration, coroutine versions of the
    /// async methods, a flush when the app goes to the background (the last callback a mobile
    /// game can rely on before the OS kills it), and main-thread events. <see cref="Vault"/>
    /// raises events on whichever thread finished the work, so a handler that updates UI
    /// directly would throw <c>get_isActiveAndEnabled can only be called from the main
    /// thread</c>.
    /// <para>
    /// The <see cref="VaultConfig"/> settings a game is likely to tune are exposed as serialized
    /// fields, so changing a timeout does not require giving up the component.
    /// </para>
    /// <para>
    /// For a signed-in player or a custom storage or transport, turn off <c>Open On Awake</c>.
    /// Then either set <see cref="PlayerId"/> and call <see cref="OpenAsync()"/>, or call
    /// <see cref="CreateConfig"/>, change it and pass it to <see cref="OpenAsync(VaultConfig)"/>.
    /// A vault built entirely in code can be handed over with <see cref="Attach"/>.
    /// </para>
    /// </remarks>
    [AddComponentMenu("PlayerVault/Vault Behaviour")]
    public sealed class VaultBehaviour : MonoBehaviour
    {
        /// <summary>
        /// Inspector version of <see cref="ResourceDefinition"/>. Unity cannot serialize
        /// <c>long?</c>, so the optional maximum is a flag plus a value.
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
        [Tooltip("Identifies the player. Also used as the save file key, so changing it switches save. " +
                 "For a logged-in player, turn off Open On Awake and set PlayerId from code before opening.")]
        [SerializeField] string playerId = "test-player";

        [Header("Backend")]
        [SerializeField] string apiUrl = "https://httpbin.org/anything";

        [Header("Resources")]
        [SerializeField] List<ResourceSetting> resources = new List<ResourceSetting>();

        [Header("Behaviour")]
        [Tooltip("Open the vault in Awake. Turn off to open it yourself once a player id is known.")]
        [SerializeField] bool openOnAwake = true;

        [Tooltip("Create resources not listed above on first use. Leave off to catch typos.")]
        [SerializeField] bool allowUndeclaredResources;

        [Tooltip("Save when the app goes to the background. This is the last callback a mobile game can rely on.")]
        [SerializeField] bool flushOnApplicationPause = true;

        [Header("Reliability")]
        [Tooltip("Total attempts per session for a claim, including the first.")]
        [SerializeField, Min(1)] int maxAttempts = 3;

        [Tooltip("Per-attempt request timeout, in seconds. Kept long because a timed-out claim " +
                 "has an unknown outcome.")]
        [SerializeField, Min(0.1f)] float requestTimeoutSeconds = 15f;

        [Tooltip("Delay before the second attempt, in seconds. Doubles thereafter.")]
        [SerializeField, Min(0f)] float retryBaseDelaySeconds = 0.5f;

        [Tooltip("Upper bound on any single backoff wait, in seconds.")]
        [SerializeField, Min(0f)] float retryMaxDelaySeconds = 10f;

        [Tooltip("Random variation applied to each delay. 0.25 means +/-25%, so clients do not " +
                 "all retry at the same moment after an outage.")]
        [SerializeField, Range(0f, 1f)] float retryJitter = 0.25f;

        [Tooltip("Retry unfinished claims when the vault opens. Runs in the background.")]
        [SerializeField] bool resumePendingOnOpen = true;

        [Header("Storage")]
        [Tooltip("Immediate writes on every change. Debounced combines writes within the interval below.")]
        [SerializeField] FlushMode flushMode = FlushMode.Immediate;

        [Tooltip("Only used when Flush Mode is Debounced.")]
        [SerializeField, Min(0.05f)] float debounceIntervalSeconds = 1f;

        [Tooltip("Quarantine moves an unreadable save aside and starts a new one. Throw fails the open.")]
        [SerializeField] CorruptDataPolicy onCorruptData = CorruptDataPolicy.Quarantine;

        /// <summary>
        /// Work queued from background threads and run in <see cref="Update"/>. A queue is used
        /// instead of a captured SynchronizationContext, which may not be set yet during the first
        /// scene's Awake, when the vault opens.
        /// </summary>
        readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        /// <summary>
        /// Guards the handover between the opening task and <see cref="OnDestroy"/>. Without it,
        /// a component destroyed while opening would leave the vault that opens afterwards
        /// running and never disposed.
        /// </summary>
        readonly object _gate = new object();

        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        Task<Vault> _opening;
        Vault _pending;
        bool _destroyed;
        bool _ownsVault = true;

        /// <summary>The vault, or null until it has finished opening.</summary>
        public Vault Vault { get; private set; }

        public bool IsOpen => Vault != null;

        /// <summary>
        /// The error from the last failed open, or null. Set before <see cref="OpenFailed"/> is
        /// raised and cleared when a new open starts.
        /// </summary>
        public Exception OpenError { get; private set; }

        /// <summary>True when an open has been attempted and failed, and none is in flight.</summary>
        public bool HasFailedToOpen => OpenError != null && _opening == null;

        /// <summary>
        /// The player the vault opens for. Set it before opening, for example after sign-in.
        /// Setting it after the vault is open throws.
        /// </summary>
        public string PlayerId
        {
            get => IsOpen ? Vault.PlayerId : playerId;
            set
            {
                if (IsOpen || _opening != null)
                    throw new InvalidOperationException(
                        "VaultBehaviour.PlayerId cannot change once the vault is open. " +
                        "Destroy or Attach a different vault to switch player.");

                playerId = value;
            }
        }

        /// <summary>Raised on the main thread once the vault is open and safe to use.</summary>
        public event Action<Vault> Opened;

        /// <summary>
        /// Raised on the main thread when opening fails. The component can still be used: fix
        /// the cause and call <see cref="OpenAsync()"/> again. Usually a
        /// <see cref="VaultStorageException"/> for an unreadable save, which a game may want to
        /// show as "could not load your progress".
        /// </summary>
        public event Action<Exception> OpenFailed;

        /// <summary><see cref="Vault.BalanceChanged"/>, re-raised on the main thread.</summary>
        public event Action<string, long> BalanceChanged;

        /// <summary><see cref="Vault.ClaimStateChanged"/>, re-raised on the main thread.</summary>
        public event Action<ClaimRecord> ClaimStateChanged;

        async void Awake()
        {
            if (!openOnAwake) return;

            // Wrapped in try because an unhandled exception from async void crashes, and an open
            // can fail for ordinary reasons such as a full disk. OpenFailed has already been
            // raised by the time this catch runs.
            try { await OpenAsync(); }
            catch (Exception) { /* reported through OpenFailed and OpenError */ }
        }

        /// <summary>Opens the vault with the Inspector settings, or returns the open already in progress.</summary>
        public Task<Vault> OpenAsync() => OpenAsync(null);

        /// <summary>
        /// Opens the vault with a config built by the game, for example to set a signed-in
        /// player, a custom backend or custom storage. Pass null to use the Inspector settings.
        /// </summary>
        /// <remarks>
        /// If an open is already in progress, returns it instead of starting another. Two vaults
        /// on one save file would overwrite each other's writes.
        /// </remarks>
        public Task<Vault> OpenAsync(VaultConfig config)
        {
            lock (_gate)
            {
                if (_destroyed)
                    return Task.FromException<Vault>(new ObjectDisposedException(nameof(VaultBehaviour)));

                if (Vault != null) return Task.FromResult(Vault);
                if (_opening != null) return _opening;

                OpenError = null;
                return _opening = OpenCoreAsync(config ?? CreateConfig());
            }
        }

        /// <summary>
        /// Uses a vault the game created and opened itself. The component provides main-thread
        /// events, the coroutine methods and the background flush for it.
        /// </summary>
        /// <param name="takeOwnership">
        /// When true (the default), the vault is disposed with the GameObject. Pass false if the
        /// vault should outlive this scene.
        /// </param>
        public void Attach(Vault vault, bool takeOwnership = true)
        {
            if (vault == null) throw new ArgumentNullException(nameof(vault));

            lock (_gate)
            {
                if (_destroyed) throw new ObjectDisposedException(nameof(VaultBehaviour));
                if (Vault != null || _opening != null)
                    throw new InvalidOperationException("This VaultBehaviour already has a vault.");

                _ownsVault = takeOwnership;
                _opening = Task.FromResult(vault);
                OpenError = null;

                vault.BalanceChanged += OnVaultBalanceChanged;
                vault.ClaimStateChanged += OnVaultClaimStateChanged;
                _pending = vault;
            }

            RunOnMainThread(PublishOpened);
        }

        async Task<Vault> OpenCoreAsync(VaultConfig config)
        {
            Vault vault;

            try
            {
                vault = await Vault.OpenAsync(config, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Cleared so the open can be retried.
                lock (_gate) _opening = null;
                RunOnMainThread(() => PublishFailure(exception));
                throw;
            }

            lock (_gate)
            {
                if (_destroyed)
                {
                    // The component was destroyed while opening, so nothing else will dispose
                    // this vault.
                    vault.Dispose();
                    throw new ObjectDisposedException(nameof(VaultBehaviour));
                }

                vault.BalanceChanged += OnVaultBalanceChanged;
                vault.ClaimStateChanged += OnVaultClaimStateChanged;

                // Set through the main-thread queue instead of here, so Vault never becomes non-null
                // on a background thread.
                _pending = vault;
            }

            RunOnMainThread(PublishOpened);
            return vault;
        }

        /// <summary>
        /// The Inspector settings as a <see cref="VaultConfig"/>, which can be changed and passed
        /// to <see cref="OpenAsync(VaultConfig)"/>.
        /// </summary>
        /// <remarks>
        /// Transport, Storage, Clock and Logger are not serialized fields because they are code
        /// dependencies, not scene settings. Set them on the returned config to keep the Inspector
        /// settings and use your own implementations.
        /// </remarks>
        public VaultConfig CreateConfig()
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

            return config;
        }

        void PublishOpened()
        {
            Vault opened;
            lock (_gate)
            {
                if (_pending == null) return;
                opened = _pending;
                _pending = null;
                Vault = opened;
            }

            Opened?.Invoke(opened);
        }

        void PublishFailure(Exception exception)
        {
            OpenError = exception;
            Debug.LogError($"[PlayerVault] Opening the vault failed: {exception.Message}");

            if (OpenFailed != null) OpenFailed.Invoke(exception);
            else Debug.LogException(exception);
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
                catch (Exception exception) { Debug.LogException(exception); }  // keep draining if one handler throws
            }
        }

        /// <summary>
        /// Coroutine version of <see cref="Vault.ClaimAsync"/>. The callback runs on the main
        /// thread. It does not run if the vault could not be opened; use
        /// <see cref="OpenFailed"/> for that case.
        /// </summary>
        public IEnumerator ClaimRoutine(string rewardId, string resource, long amount, Action<ClaimResult> onComplete = null)
        {
            if (!IsOpen) yield return OpenRoutine();
            if (!EnsureOpen(nameof(ClaimRoutine))) yield break;

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
        /// Coroutine version of <see cref="Vault.SpendAsync"/>. The callback runs once the spend
        /// is saved. For purchases, prefer <see cref="TransactRoutine"/>, which charges and
        /// delivers in one write.
        /// </summary>
        public IEnumerator SpendRoutine(string resource, long amount, Action<SpendResult> onComplete = null)
        {
            if (!IsOpen) yield return OpenRoutine();
            if (!EnsureOpen(nameof(SpendRoutine))) yield break;

            var task = Vault.SpendAsync(resource, amount);
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            onComplete?.Invoke(task.Result);
        }

        /// <summary>
        /// Coroutine version of <see cref="Vault.TransactAsync"/>. The callback runs once the
        /// transaction is saved.
        /// </summary>
        public IEnumerator TransactRoutine(VaultTransaction transaction, Action<TransactionResult> onComplete = null)
        {
            if (!IsOpen) yield return OpenRoutine();
            if (!EnsureOpen(nameof(TransactRoutine))) yield break;

            var task = Vault.TransactAsync(transaction);
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            onComplete?.Invoke(task.Result);
        }

        /// <summary>Coroutine version of <see cref="Vault.ResumePendingAsync"/>. Retries unfinished claims now.</summary>
        public IEnumerator ResumePendingRoutine(Action onComplete = null)
        {
            if (!IsOpen) yield return OpenRoutine();
            if (!EnsureOpen(nameof(ResumePendingRoutine))) yield break;

            var task = Vault.ResumePendingAsync();
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }

            onComplete?.Invoke();
        }

        /// <summary>
        /// Waits until the vault is open and available on the main thread. Ends early if the open
        /// failed; check <see cref="IsOpen"/> afterwards.
        /// </summary>
        public IEnumerator OpenRoutine()
        {
            var task = OpenAsync();
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted || task.IsCanceled) yield break;   // already reported through OpenFailed

            while (!IsOpen && !_destroyed) yield return null;   // Vault is set by the main-thread queue a frame later
        }

        /// <summary>Stops the coroutines from using a vault that never opened.</summary>
        bool EnsureOpen(string operation)
        {
            if (IsOpen) return true;

            Debug.LogError(
                $"[PlayerVault] {operation} was skipped because the vault is not open" +
                (OpenError != null ? $": {OpenError.Message}" : "."));

            return false;
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
            Vault owned;
            bool disposes;

            lock (_gate)
            {
                _destroyed = true;
                owned = Vault ?? _pending;
                _pending = null;
                Vault = null;
                disposes = _ownsVault;
            }

            // Cancels an open still in progress. If it has already created a vault, it sees
            // _destroyed inside the lock and disposes the vault itself.
            try { _lifetime.Cancel(); } catch (Exception) { /* ignore errors during teardown */ }

            if (owned == null) return;

            owned.BalanceChanged -= OnVaultBalanceChanged;
            owned.ClaimStateChanged -= OnVaultClaimStateChanged;
            if (disposes) owned.Dispose();
        }
    }
}
