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
    /// <para>
    /// <b>Scenes.</b> Every component that opens a vault for the same player shares one vault,
    /// so a scene can have its own VaultBehaviour without opening the save a second time. The
    /// vault is closed, with a final save, when the last of them is destroyed. Turn on
    /// <c>Persist Across Scenes</c> to keep the component, and the vault, alive through scene
    /// loads; otherwise a scene change closes the vault and the next scene opens it again, which
    /// cancels claims that were in flight (they stay pending and are retried).
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

        [Tooltip("Keep this GameObject, and the vault, through scene loads. Must be on a root GameObject. " +
                 "A copy in a scene loaded later shares the vault instead of opening it again.")]
        [SerializeField] bool persistAcrossScenes;

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

        Task<Vault> _opening;
        Vault _pending;
        bool _destroyed;
        bool _ownsVault = true;

        /// <summary>The shared vault this component opened, or null for an attached one.</summary>
        SharedVault _shared;

        /// <summary>True when this component was kept with DontDestroyOnLoad.</summary>
        bool _persists;

        /// <summary>
        /// A vault shared by every component that opened one for the same player. It is closed
        /// when the last of them is destroyed.
        /// </summary>
        sealed class SharedVault
        {
            public readonly string PlayerId;
            public readonly List<VaultBehaviour> Users = new List<VaultBehaviour>();
            public readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
            public Task<Vault> Opening;

            /// <summary>Set when the last user leaves. Completes once the vault is closed.</summary>
            public Task Closed;

            public SharedVault(string playerId) => PlayerId = playerId;
        }

        static readonly object SharedGate = new object();
        static readonly Dictionary<string, SharedVault> Shared = new Dictionary<string, SharedVault>(StringComparer.Ordinal);

        static int s_mainThreadId = -1;

        /// <summary>The vault, or null until it has finished opening.</summary>
        public Vault Vault { get; private set; }

        public bool IsOpen => Vault != null;

        /// <summary>Whether this component survives scene loads.</summary>
        public bool PersistsAcrossScenes => _persists;

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
        /// <remarks>
        /// Raised once. A handler added afterwards never runs; use <see cref="WhenOpen"/> or
        /// <see cref="WhenOpenAsync"/>, which also cover a vault that is already open.
        /// </remarks>
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
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

            if (persistAcrossScenes) KeepAcrossScenes();

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
                var opening = OpenCoreAsync(config ?? CreateConfig());

                // An open that failed synchronously has already cleared _opening, and must stay
                // cleared so it can be retried.
                _opening = opening.IsFaulted || opening.IsCanceled ? null : opening;
                return opening;
            }
        }

        /// <summary>
        /// Uses a vault the game created and opened itself. The component provides main-thread
        /// events, the coroutine methods and the background flush for it.
        /// </summary>
        /// <param name="takeOwnership">
        /// When true (the default), the vault is closed with the GameObject. Pass false if the
        /// vault should outlive this scene.
        /// </param>
        /// <remarks>
        /// An attached vault is not shared with other components; <see cref="OpenAsync()"/> on
        /// another component for the same player fails, because the save is already in use.
        /// </remarks>
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

            PublishOnMainThread(PublishOpened);
        }

        async Task<Vault> OpenCoreAsync(VaultConfig config)
        {
            var shared = await JoinAsync(config).ConfigureAwait(false);

            lock (_gate)
            {
                // Destroyed while joining. OnDestroy did not see the shared vault, so leave it here.
                if (_destroyed)
                {
                    _opening = null;
                    Leave(shared);
                    throw new ObjectDisposedException(nameof(VaultBehaviour));
                }

                _shared = shared;
            }

            Vault vault;

            try
            {
                vault = await shared.Opening.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Cleared so the open can be retried. Every user of a failed open drops it, so
                // the retry starts a new one.
                lock (SharedGate)
                {
                    shared.Users.Remove(this);
                    if (Shared.TryGetValue(shared.PlayerId, out var current) && current == shared)
                        Shared.Remove(shared.PlayerId);
                }

                lock (_gate)
                {
                    _opening = null;
                    if (_shared == shared) _shared = null;
                }

                if (!_destroyed) PublishOnMainThread(() => PublishFailure(exception));
                throw;
            }

            lock (_gate)
            {
                // Destroyed while opening. OnDestroy has already left the shared vault, which
                // closes it if nobody else is using it.
                if (_destroyed) throw new ObjectDisposedException(nameof(VaultBehaviour));

                vault.BalanceChanged += OnVaultBalanceChanged;
                vault.ClaimStateChanged += OnVaultClaimStateChanged;

                // Vault is set on the main thread, never on a background one.
                _pending = vault;
            }

            PublishOnMainThread(PublishOpened);
            return vault;
        }

        /// <summary>
        /// Joins the vault already open or opening for this player, or starts opening one. Waits
        /// for a vault that is still closing, such as the previous scene's.
        /// </summary>
        async Task<SharedVault> JoinAsync(VaultConfig config)
        {
            var playerId = config.PlayerId ?? string.Empty;

            while (true)
            {
                Task closing;

                lock (SharedGate)
                {
                    if (!Shared.TryGetValue(playerId, out var shared))
                    {
                        shared = new SharedVault(playerId);
                        shared.Opening = Vault.OpenAsync(config, shared.Lifetime.Token);
                        Shared[playerId] = shared;
                    }

                    if (shared.Closed == null)
                    {
                        shared.Users.Add(this);
                        return shared;
                    }

                    closing = shared.Closed;
                }

                try { await closing.ConfigureAwait(false); }
                catch (Exception) { /* only the timing matters here */ }
            }
        }

        /// <summary>Stops using a shared vault, and closes it if this was the last user.</summary>
        void Leave(SharedVault shared)
        {
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (SharedGate)
            {
                if (!shared.Users.Remove(this) || shared.Users.Count > 0 || shared.Closed != null) return;
                shared.Closed = closed.Task;
            }

            _ = CloseSharedAsync(shared, closed);
        }

        static async Task CloseSharedAsync(SharedVault shared, TaskCompletionSource<bool> closed)
        {
            try
            {
                // Cancels an open still in progress.
                try { shared.Lifetime.Cancel(); } catch (Exception) { /* ignore errors during teardown */ }

                Vault vault;
                try { vault = await shared.Opening.ConfigureAwait(false); }
                catch (Exception) { return; }   // never opened, so nothing to close

                await vault.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                lock (SharedGate)
                {
                    if (Shared.TryGetValue(shared.PlayerId, out var current) && current == shared)
                        Shared.Remove(shared.PlayerId);
                }

                closed.TrySetResult(true);
            }
        }

        /// <summary>
        /// A live component that has, or is opening, a vault for the player. Pass null to accept
        /// any player, which suits a single-player game. Returns null if there is none.
        /// </summary>
        /// <remarks>
        /// For code in a scene that has no VaultBehaviour of its own, typically when the vault
        /// lives on a component with <c>Persist Across Scenes</c> in a bootstrap scene. Call it
        /// from the main thread.
        /// </remarks>
        public static VaultBehaviour Find(string playerId = null)
        {
            VaultBehaviour match = null;

            foreach (var candidate in FindObjectsByType<VaultBehaviour>(FindObjectsSortMode.None))
            {
                if (candidate._destroyed) continue;
                if (playerId != null && !string.Equals(candidate.PlayerId, playerId, StringComparison.Ordinal)) continue;

                // Prefer one that is already open, then one that will outlive the scene.
                if (candidate.IsOpen) return candidate;
                if (match == null || (candidate._persists && !match._persists)) match = candidate;
            }

            return match;
        }

        void KeepAcrossScenes()
        {
            if (transform.parent != null)
            {
                Debug.LogWarning(
                    $"[PlayerVault] '{name}' has Persist Across Scenes on but is not a root GameObject, " +
                    "so it is destroyed with its scene. Move it to the root of the hierarchy.", this);
                return;
            }

            // Returning to the scene that holds the persistent copy would otherwise add another
            // persistent copy each time. This one shares the vault and stays with its scene.
            foreach (var other in FindObjectsByType<VaultBehaviour>(FindObjectsSortMode.None))
            {
                if (other != this && other._persists &&
                    string.Equals(other.PlayerId, playerId, StringComparison.Ordinal))
                    return;
            }

            DontDestroyOnLoad(gameObject);
            _persists = true;
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

        static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == s_mainThreadId;

        /// <summary>
        /// Runs now when already on the main thread, so an open that completes there is visible
        /// in the same frame. Queues otherwise.
        /// </summary>
        void PublishOnMainThread(Action action)
        {
            if (OnMainThread) action();
            else RunOnMainThread(action);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnPlayModeStart()
        {
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

            // With domain reload turned off, statics survive between play sessions.
            lock (SharedGate) Shared.Clear();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void OnEditorLoad() => s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
#endif

        // ------------------------------------------------------------------ waiting for the open

        /// <summary>
        /// Runs <paramref name="onOpen"/> with the vault once it is open: immediately if it
        /// already is, otherwise on the main thread when it opens. It runs once.
        /// </summary>
        /// <remarks>
        /// Replaces checking <see cref="IsOpen"/> and otherwise subscribing to
        /// <see cref="Opened"/>, which waits forever if the subscription comes after the event.
        /// <paramref name="onFailed"/> runs for each failed open, including one that failed
        /// before this call; the hook keeps waiting, so a successful retry still runs
        /// <paramref name="onOpen"/>.
        /// </remarks>
        /// <returns>Dispose it to stop waiting, for example in the caller's OnDestroy.</returns>
        /// <example>
        /// <code>
        /// void Start() => _hook = vaultBehaviour.WhenOpen(OnVaultOpened, OnVaultOpenFailed);
        /// void OnDestroy() => _hook?.Dispose();
        /// </code>
        /// </example>
        public IDisposable WhenOpen(Action<Vault> onOpen, Action<Exception> onFailed = null)
        {
            if (onOpen == null) throw new ArgumentNullException(nameof(onOpen));

            if (IsOpen)
            {
                onOpen(Vault);
                return OpenHook.None;
            }

            var hook = new OpenHook(this, onOpen, onFailed);
            if (HasFailedToOpen) onFailed?.Invoke(OpenError);
            return hook;
        }

        /// <summary>
        /// Completes with the vault once it is open and <see cref="Vault"/> is set, so after
        /// <c>await behaviour.WhenOpenAsync()</c> the vault is always usable. Fails with the
        /// exception if the open fails, including one that failed before this call.
        /// </summary>
        public Task<Vault> WhenOpenAsync()
        {
            if (IsOpen) return Task.FromResult(Vault);
            if (HasFailedToOpen) return Task.FromException<Vault>(OpenError);

            var completion = new TaskCompletionSource<Vault>();
            IDisposable hook = null;

            hook = WhenOpen(
                vault => completion.TrySetResult(vault),
                exception =>
                {
                    hook?.Dispose();
                    completion.TrySetException(exception);
                });

            return completion.Task;
        }

        sealed class OpenHook : IDisposable
        {
            public static readonly IDisposable None = new OpenHook(null, null, null);

            readonly VaultBehaviour _owner;
            readonly Action<Vault> _onOpen;
            readonly Action<Exception> _onFailed;

            public OpenHook(VaultBehaviour owner, Action<Vault> onOpen, Action<Exception> onFailed)
            {
                _owner = owner;
                _onOpen = onOpen;
                _onFailed = onFailed;

                if (owner == null) return;
                owner.Opened += HandleOpened;
                if (onFailed != null) owner.OpenFailed += HandleFailed;
            }

            void HandleOpened(Vault vault)
            {
                Dispose();
                _onOpen(vault);
            }

            void HandleFailed(Exception exception) => _onFailed(exception);

            public void Dispose()
            {
                if (_owner == null) return;
                _owner.Opened -= HandleOpened;
                _owner.OpenFailed -= HandleFailed;
            }
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

        // ------------------------------------------------------------------ test player reset

        /// <summary>
        /// Deletes the save for this component's player id. Only while the vault is not open,
        /// because an open vault would write its state straight back: outside play mode, or in
        /// play mode before opening.
        /// </summary>
        [ContextMenu("Delete Save")]
        void DeleteSaveFromMenu()
        {
            if (IsOpen || _opening != null)
            {
                Debug.LogError(
                    $"[PlayerVault] The save for '{PlayerId}' is in use by the open vault. " +
                    "Exit play mode, then delete it.", this);
                return;
            }

            var storage = new JsonFileStorage();
            var path = storage.PathFor(PlayerId);

            try
            {
                Vault.DeleteSaveAsync(PlayerId, storage).GetAwaiter().GetResult();
                Debug.Log($"[PlayerVault] Deleted the save for '{PlayerId}' ({path}).", this);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[PlayerVault] Could not delete the save for '{PlayerId}': {exception.Message}", this);
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Show Save File")]
        void RevealSaveFromMenu()
        {
            var storage = new JsonFileStorage();
            var path = storage.PathFor(PlayerId);

            UnityEditor.EditorUtility.RevealInFinder(System.IO.File.Exists(path) ? path : storage.RootDirectory);
        }
#endif

        void OnDestroy()
        {
            Vault owned;
            SharedVault shared;
            bool closes;

            lock (_gate)
            {
                _destroyed = true;
                owned = Vault ?? _pending;
                shared = _shared;
                _shared = null;
                _pending = null;
                Vault = null;
                closes = _ownsVault;
            }

            if (owned != null)
            {
                owned.BalanceChanged -= OnVaultBalanceChanged;
                owned.ClaimStateChanged -= OnVaultClaimStateChanged;
            }

            // A shared vault closes when its last user leaves, including one still opening.
            // An attached vault closes here if the component owns it. Either way it is closed,
            // not just disposed, so debounced changes are saved, and a vault opened on the same
            // save meanwhile (the next scene's) waits for that final write.
            if (shared != null) Leave(shared);
            else if (owned != null && closes) _ = CloseQuietlyAsync(owned);
        }

        static async Task CloseQuietlyAsync(Vault vault)
        {
            try { await vault.CloseAsync().ConfigureAwait(false); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
