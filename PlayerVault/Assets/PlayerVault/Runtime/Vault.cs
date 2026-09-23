using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayerVault.Internal;
using UnityEngine;

namespace PlayerVault
{
    /// <summary>
    /// A player's resource balances and reward claims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain C# object owned by the game. It is not a singleton or a MonoBehaviour, so a game
    /// can hold one per player and a test can create one without a scene.
    /// <see cref="VaultBehaviour"/> wraps it as a component.
    /// </para>
    /// <para>
    /// The class is sealed. Replaceable parts (transport, storage, clock, logger, retry policy)
    /// are interfaces on <see cref="VaultConfig"/>. The claim state machine is not replaceable,
    /// because its write ordering is what prevents a reward from being granted twice.
    /// </para>
    /// <para>
    /// <b>Threading.</b> All public members can be called from any thread. One lock guards
    /// balances, claim records and the granted-reward set. Each durable operation changes state
    /// and takes its snapshot under that lock, so a snapshot never contains a half-applied
    /// change. Network and disk waits happen outside the lock, so a pending claim does not block
    /// a spend. Events are also raised outside the lock, so handlers can call back into the vault.
    /// </para>
    /// <para>
    /// <b>Durability.</b> The <c>Async</c> methods complete after the change is on disk, and undo
    /// the change in memory if the write fails. <see cref="Spend"/> and <see cref="GrantLocal"/>
    /// schedule the write and return immediately. Use them for small income such as coin
    /// pickups, and the durable methods for anything the game acts on.
    /// </para>
    /// </remarks>
    public sealed class Vault : IDisposable
    {
        readonly IVaultTransport _transport;
        readonly IVaultStorage _storage;
        readonly IVaultClock _clock;
        readonly IVaultLogger _logger;
        readonly RetryPolicy _retry;
        readonly System.Random _random = new System.Random();

        // Copied from the config at construction. The caller may keep editing the config, and a
        // PlayerId changed after opening would write one player's data into another's file.
        readonly string _playerId;
        readonly string _apiUrl;
        readonly bool _allowUndeclaredResources;
        readonly CorruptDataPolicy _onCorruptData;
        readonly FlushMode _flushMode;
        readonly TimeSpan _debounceInterval;

        /// <summary>Declared resources and their optional maximums.</summary>
        readonly Dictionary<string, long?> _declared = new Dictionary<string, long?>(StringComparer.Ordinal);

        /// <summary>Guards every field below. See the threading note on the class.</summary>
        readonly object _sync = new object();

        readonly Dictionary<string, long> _balances = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly Dictionary<string, ClaimRecord> _claims = new Dictionary<string, ClaimRecord>(StringComparer.Ordinal);

        /// <summary>
        /// Reward ids that have been granted. Rebuilt from the claim records on load instead of
        /// being stored separately, so the two cannot get out of sync.
        /// </summary>
        readonly HashSet<string> _granted = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Claims in progress, so concurrent calls for one reward share a single request.</summary>
        readonly Dictionary<string, Task<ClaimResult>> _inFlight =
            new Dictionary<string, Task<ClaimResult>>(StringComparer.Ordinal);

        /// <summary>
        /// Incremented on every snapshot. A snapshot older than the last one written is skipped,
        /// so a slow write that finishes late cannot overwrite newer data.
        /// </summary>
        long _stateVersion;
        long _writtenVersion;

        readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        /// <summary>Identifies this vault's save in <see cref="OpenVaults"/>.</summary>
        readonly object _saveKey;

        /// <summary>Completes once the vault is disposed and its last write has finished.</summary>
        readonly TaskCompletionSource<bool> _closed =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Resources already reported as undeclared, so each typo is logged once.</summary>
        readonly HashSet<string> _reportedUndeclared = new HashSet<string>(StringComparer.Ordinal);

        bool _loaded;
        Task _loading;
        volatile bool _disposed;
        bool _debouncePending;

        public string PlayerId => _playerId;

        /// <summary>The endpoint claims are posted to.</summary>
        public string ApiUrl => _apiUrl;

        /// <summary>
        /// Raised after a balance change is committed, with the resource and its new balance.
        /// </summary>
        /// <remarks>
        /// Raised on the thread that finished the work. For anything that follows a claim this is
        /// a thread-pool thread, so a handler that touches a <c>GameObject</c>, <c>Transform</c> or
        /// UI element will throw. Subscribe to the same event on <see cref="VaultBehaviour"/>
        /// instead; it re-raises on the main thread.
        /// <para>
        /// Raised after the change is saved. Exceptions from handlers are logged and ignored so
        /// a broken handler cannot stop a save.
        /// </para>
        /// </remarks>
        public event Action<string, long> BalanceChanged;

        /// <summary>Raised whenever a claim changes state.</summary>
        /// <remarks>Same threading and error handling as <see cref="BalanceChanged"/>.</remarks>
        public event Action<ClaimRecord> ClaimStateChanged;

        /// <summary>
        /// Creates a vault. Throws if the configuration is invalid. The vault is not usable until
        /// <see cref="LoadAsync"/> has run; <see cref="OpenAsync"/> does both.
        /// </summary>
        public Vault(VaultConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();

            _playerId = config.PlayerId;
            _apiUrl = config.ApiUrl;
            _allowUndeclaredResources = config.AllowUndeclaredResources;
            _onCorruptData = config.OnCorruptData;
            _flushMode = config.FlushMode;
            _debounceInterval = config.DebounceInterval;
            _retry = config.Retry.Clone();

            _transport = config.Transport ?? new UnityWebRequestTransport(_retry.Timeout);
            _storage = config.Storage ?? new JsonFileStorage();
            _clock = config.Clock ?? new SystemClock();
            _logger = config.Logger ?? new UnityLogger();
            _saveKey = OpenVaults.KeyFor(_storage, _playerId);

            foreach (var definition in config.Resources)
            {
                _declared[definition.Key] = definition.Max;
                _balances[definition.Key] = definition.Initial;
            }
        }

        /// <summary>
        /// Creates a vault, loads saved state and, unless disabled, starts retrying unfinished
        /// claims in the background. Opening does not wait for the network.
        /// </summary>
        /// <exception cref="VaultStorageException">
        /// Saved state exists but could not be read. Nothing was written, so the save is intact
        /// and opening can be retried.
        /// </exception>
        public static async Task<Vault> OpenAsync(VaultConfig config, CancellationToken cancellationToken = default)
        {
            var vault = new Vault(config);

            try
            {
                await vault.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The caller never received this vault, so dispose it here.
                vault.Dispose();
                throw;
            }

            if (config.ResumePendingOnOpen)
                vault.ResumePendingDetached();

            return vault;
        }

        // ---------------------------------------------------------------- resources

        /// <summary>Current balance. Unknown resources return zero. Never throws.</summary>
        public long GetBalance(string resource)
        {
            if (string.IsNullOrEmpty(resource)) return 0;
            lock (_sync) return GetBalanceLocked(resource);
        }

        /// <summary>The configured maximum for a resource, or null if it has none.</summary>
        public long? GetMax(string resource)
        {
            if (resource != null && _declared.TryGetValue(resource, out var max)) return max;
            return null;
        }

        /// <summary>
        /// Whether the config declares this resource. Use it to check at startup that the keys a
        /// game uses in code match the ones configured, for example in the Inspector.
        /// </summary>
        public bool IsDeclared(string resource) => resource != null && _declared.ContainsKey(resource);

        /// <summary>
        /// A copy of every balance. It is a copy because a claim completing on another thread can
        /// change the underlying dictionary while it is being enumerated.
        /// </summary>
        public IReadOnlyDictionary<string, long> Balances
        {
            get { lock (_sync) return new Dictionary<string, long>(_balances, StringComparer.Ordinal); }
        }

        public bool CanSpend(string resource, long amount)
        {
            if (amount <= 0 || !IsValidResourceName(resource)) return false;
            lock (_sync) return IsKnownLocked(resource) && GetBalanceLocked(resource) >= amount;
        }

        /// <summary>
        /// Subtracts from a balance. Returns a failed result, rather than throwing, when the
        /// balance is too low.
        /// </summary>
        /// <remarks>
        /// Changes the balance in memory and schedules the write. Use <see cref="SpendAsync"/>
        /// when the spend must be saved before the game continues, or <see cref="TransactAsync"/>
        /// for a purchase.
        /// <para>
        /// Pending claims never reserve any amount, so spending works while claims are in flight.
        /// </para>
        /// </remarks>
        public SpendResult Spend(string resource, long amount)
        {
            ThrowIfDisposed();

            SpendResult result;
            lock (_sync) result = TrySpendLocked(resource, amount);

            if (!result.Success) return result;

            // Schedule the write before raising the event, so a handler that throws cannot
            // prevent it.
            SchedulePersist();
            RaiseBalanceChanged(resource);
            return result;
        }

        /// <summary>
        /// Same as <see cref="Spend"/>, but the task completes once the change is saved.
        /// </summary>
        /// <remarks>
        /// If the write fails, the spend is undone in memory and the result is
        /// <see cref="SpendFailure.StorageUnavailable"/>. To charge and deliver an item in one
        /// write, use <see cref="TransactAsync"/>.
        /// </remarks>
        public async Task<SpendResult> SpendAsync(string resource, long amount)
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(default).ConfigureAwait(false);

            SpendResult result;
            long version;
            string payload;

            lock (_sync)
            {
                result = TrySpendLocked(resource, amount);
                if (!result.Success) return result;
                payload = SerializeLocked(out version);
            }

            try
            {
                await WriteAsync(version, payload).ConfigureAwait(false);
            }
            catch (VaultStorageException exception)
            {
                long balance;
                lock (_sync)
                {
                    ApplyClampedLocked(resource, amount);   // undo the spend
                    balance = GetBalanceLocked(resource);
                }

                _logger.Error(
                    $"[PlayerVault] Spending {amount} {resource} was rolled back: the change could not be saved.",
                    exception);

                return SpendResult.Fail(SpendFailure.StorageUnavailable, balance);
            }

            RaiseBalanceChanged(resource);
            return result;
        }

        /// <summary>
        /// Adds to a balance without contacting the backend, for income the game awards itself
        /// such as coins picked up in a level. Clamped to the resource maximum. Rewards that must
        /// be granted only once should use <see cref="ClaimAsync"/>.
        /// </summary>
        public GrantResult GrantLocal(string resource, long amount)
        {
            ThrowIfDisposed();

            GrantResult result;
            lock (_sync) result = TryGrantLocked(resource, amount);

            if (!result.Success || result.AmountApplied == 0) return result;

            SchedulePersist();
            RaiseBalanceChanged(resource);
            return result;
        }

        /// <summary>Same as <see cref="GrantLocal"/>, but completes once the change is saved.</summary>
        /// <remarks>If the write fails, the grant is undone in memory and the result is
        /// <see cref="SpendFailure.StorageUnavailable"/>.</remarks>
        public async Task<GrantResult> GrantAsync(string resource, long amount)
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(default).ConfigureAwait(false);

            GrantResult result;
            long version;
            string payload;

            lock (_sync)
            {
                result = TryGrantLocked(resource, amount);
                if (!result.Success || result.AmountApplied == 0) return result;
                payload = SerializeLocked(out version);
            }

            try
            {
                await WriteAsync(version, payload).ConfigureAwait(false);
            }
            catch (VaultStorageException exception)
            {
                long balance;
                lock (_sync)
                {
                    SubtractLocked(resource, result.AmountApplied);
                    balance = GetBalanceLocked(resource);
                }

                _logger.Error(
                    $"[PlayerVault] Granting {amount} {resource} was rolled back: the change could not be saved.",
                    exception);

                return GrantResult.Fail(SpendFailure.StorageUnavailable, balance);
            }

            RaiseBalanceChanged(resource);
            return result;
        }

        /// <summary>
        /// Applies several resource changes in a single write. Either all of them are saved or
        /// none are.
        /// </summary>
        /// <remarks>
        /// Use this for purchases. A spend followed by a grant is two writes, and if the process
        /// is killed between them the player has paid and received nothing. Here the whole
        /// transaction is checked, applied and written together, and undone in memory if the
        /// write fails.
        /// </remarks>
        /// <example>
        /// <code>
        /// var result = await vault.TransactAsync(VaultTransaction.Purchase("coins", 250, "level-3-unlocked"));
        /// if (result.Success) ShowLevel(3);
        /// </code>
        /// </example>
        public async Task<TransactionResult> TransactAsync(VaultTransaction transaction)
        {
            ThrowIfDisposed();
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (transaction.Count == 0) return TransactionResult.Ok(Array.Empty<ResourceChange>());

            // A purchase checked against starting balances, before the save is loaded, would
            // charge the wrong amount and then overwrite the save.
            await EnsureLoadedAsync(default).ConfigureAwait(false);

            List<ResourceChange> changes;
            SpendFailure failure;
            string failedResource;
            long version;
            string payload;

            lock (_sync)
            {
                if (!TryStageLocked(transaction, out changes, out failure, out failedResource))
                    return TransactionResult.Fail(failure, failedResource);

                payload = SerializeLocked(out version);
            }

            try
            {
                await WriteAsync(version, payload).ConfigureAwait(false);
            }
            catch (VaultStorageException exception)
            {
                lock (_sync) RevertLocked(changes);

                _logger.Error($"[PlayerVault] {transaction} was rolled back: it could not be saved.", exception);
                return TransactionResult.Fail(SpendFailure.StorageUnavailable, null);
            }

            foreach (var change in changes) RaiseBalanceChanged(change.Resource);
            return TransactionResult.Ok(changes);
        }

        // ------------------------------------------------------- resources, under the lock

        long GetBalanceLocked(string resource) =>
            _balances.TryGetValue(resource, out var balance) ? balance : 0;

        bool IsKnownLocked(string resource)
        {
            if (_declared.ContainsKey(resource) || _balances.ContainsKey(resource) || _allowUndeclaredResources)
                return true;

            // Refusing an undeclared resource is how typos are caught, but a refusal the game
            // does not check is silent. Say so once per key.
            if (_reportedUndeclared.Add(resource))
            {
                _logger.Warn(
                    $"[PlayerVault] '{resource}' is not a declared resource, so every operation on it " +
                    $"is refused. Declared: {string.Join(", ", _declared.Keys)}. Check the spelling, or " +
                    "declare it in VaultConfig.Resources or on the VaultBehaviour.");
            }

            return false;
        }

        /// <summary>
        /// Rejects empty resource names. Checked separately from whether the resource is
        /// declared, so <c>AllowUndeclaredResources</c> cannot let an empty name through.
        /// </summary>
        static bool IsValidResourceName(string resource) => !string.IsNullOrWhiteSpace(resource);

        SpendResult TrySpendLocked(string resource, long amount)
        {
            if (!IsValidResourceName(resource)) return SpendResult.Fail(SpendFailure.UnknownResource, 0);
            if (amount <= 0) return SpendResult.Fail(SpendFailure.InvalidAmount, GetBalanceLocked(resource));
            if (!IsKnownLocked(resource)) return SpendResult.Fail(SpendFailure.UnknownResource, 0);

            var balance = GetBalanceLocked(resource);
            if (balance < amount) return SpendResult.Fail(SpendFailure.InsufficientBalance, balance);

            var updated = balance - amount;
            _balances[resource] = updated;
            return SpendResult.Ok(updated);
        }

        GrantResult TryGrantLocked(string resource, long amount)
        {
            if (!IsValidResourceName(resource)) return GrantResult.Fail(SpendFailure.UnknownResource, 0);
            if (amount <= 0) return GrantResult.Fail(SpendFailure.InvalidAmount, GetBalanceLocked(resource));

            if (!IsKnownLocked(resource))
                return GrantResult.Fail(SpendFailure.UnknownResource, 0);

            if (!_balances.ContainsKey(resource)) _balances[resource] = 0;

            var applied = ApplyClampedLocked(resource, amount);
            return GrantResult.Ok(applied, GetBalanceLocked(resource));
        }

        /// <summary>
        /// Adds <paramref name="amount"/> to a balance, clamped to the resource maximum, and
        /// returns how much was actually added.
        /// </summary>
        long ApplyClampedLocked(string resource, long amount)
        {
            var current = GetBalanceLocked(resource);
            var max = GetMax(resource);

            long target;
            if (amount > long.MaxValue - current) target = long.MaxValue;
            else target = current + amount;

            if (max.HasValue && target > max.Value) target = max.Value;
            if (target < current) target = current;

            _balances[resource] = target;
            return target - current;
        }

        void SubtractLocked(string resource, long amount)
        {
            var target = GetBalanceLocked(resource) - amount;
            _balances[resource] = target < 0 ? 0 : target;
        }

        /// <summary>
        /// Applies every step of a transaction, or none. Steps run in order against the running
        /// balance, so each step sees the ones before it.
        /// </summary>
        bool TryStageLocked(
            VaultTransaction transaction,
            out List<ResourceChange> changes,
            out SpendFailure failure,
            out string failedResource)
        {
            var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
            var order = new List<string>();

            failure = SpendFailure.None;
            failedResource = null;
            changes = null;

            var steps = transaction.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                long applied;

                if (step.Kind == VaultTransaction.Kind.Spend)
                {
                    var spend = TrySpendLocked(step.Resource, step.Amount);
                    if (!spend.Success)
                    {
                        failure = spend.Failure;
                        failedResource = step.Resource;
                        Rollback(deltas);
                        return false;
                    }

                    applied = -step.Amount;
                }
                else
                {
                    var grant = TryGrantLocked(step.Resource, step.Amount);
                    if (!grant.Success)
                    {
                        failure = grant.Failure;
                        failedResource = step.Resource;
                        Rollback(deltas);
                        return false;
                    }

                    if (step.Kind == VaultTransaction.Kind.GrantExact && grant.AmountApplied < step.Amount)
                    {
                        // The maximum clamped part of the grant. Undo this step's partial grant,
                        // then the steps before it.
                        SubtractLocked(step.Resource, grant.AmountApplied);
                        failure = SpendFailure.AtMaximum;
                        failedResource = step.Resource;
                        Rollback(deltas);
                        return false;
                    }

                    applied = grant.AmountApplied;
                }

                if (!deltas.ContainsKey(step.Resource)) order.Add(step.Resource);
                deltas.TryGetValue(step.Resource, out var running);
                deltas[step.Resource] = running + applied;
            }

            changes = new List<ResourceChange>(order.Count);
            foreach (var resource in order)
                changes.Add(new ResourceChange(resource, deltas[resource], GetBalanceLocked(resource)));

            return true;

            void Rollback(Dictionary<string, long> applied)
            {
                foreach (var pair in applied)
                {
                    var target = GetBalanceLocked(pair.Key) - pair.Value;
                    _balances[pair.Key] = target < 0 ? 0 : target;
                }
            }
        }

        void RevertLocked(IReadOnlyList<ResourceChange> changes)
        {
            for (var i = 0; i < changes.Count; i++)
            {
                var target = GetBalanceLocked(changes[i].Resource) - changes[i].Applied;
                _balances[changes[i].Resource] = target < 0 ? 0 : target;
            }
        }

        // ------------------------------------------------------------------- claims

        /// <summary>
        /// Claims a reward: sends the request and adds the resource once the backend accepts it.
        /// If the reward id was already granted, returns immediately without sending anything.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A pending record is saved before the request is sent. On success, the balance, the
        /// record and the granted-reward set are saved in one write. With any other order, a
        /// process killed mid-claim could lose the reward or grant it twice.
        /// </para>
        /// <para>
        /// Concurrent calls for the same reward id share one request. The shared entry is
        /// registered before any work starts, so a handler that calls this method again from a
        /// state-change event joins the running claim. The granted set is checked again when
        /// the reward is applied.
        /// </para>
        /// <para>
        /// If all retries fail, the claim stays <see cref="ClaimStatus.Pending"/> and is retried
        /// on the next launch. Only a definite rejection from the backend is final.
        /// </para>
        /// </remarks>
        public async Task<ClaimResult> ClaimAsync(
            string rewardId,
            string resource,
            long amount,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(rewardId) || amount <= 0 ||
                !IsValidResourceName(resource) || !IsKnown(resource))
            {
                var invalid = NewRecord(rewardId ?? string.Empty, resource, amount,
                    ClaimStatus.Failed, ClaimFailure.Invalid);
                return ClaimResult.Invalid(invalid);
            }

            TaskCompletionSource<ClaimResult> completion = null;
            Task<ClaimResult> joined = null;

            lock (_sync)
            {
                if (_granted.Contains(rewardId))
                    return new ClaimResult(ClaimStatus.AlreadyGranted, ClaimFailure.None, _claims[rewardId]);

                if (_inFlight.TryGetValue(rewardId, out var running))
                {
                    joined = running;
                }
                else
                {
                    if (_claims.TryGetValue(rewardId, out var stored) &&
                        stored.Status == ClaimStatus.Pending &&
                        (!string.Equals(stored.Resource, resource, StringComparison.Ordinal) ||
                         stored.AmountRequested != amount))
                    {
                        // Retrying with a different resource or amount would record one reward
                        // and grant another. Leave the stored claim as it is.
                        _logger.Warn(
                            $"[PlayerVault] Claim '{rewardId}' is pending for {stored.AmountRequested} " +
                            $"{stored.Resource}; refusing to retry it as {amount} {resource}.");

                        return new ClaimResult(ClaimStatus.Failed, ClaimFailure.Conflict, stored);
                    }

                    // Register before starting the work. Otherwise a synchronous storage or
                    // transport can raise the first state-change event before registration, and
                    // a handler that claims again would start a second request.
                    completion = new TaskCompletionSource<ClaimResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlight[rewardId] = completion.Task;
                }
            }

            if (joined != null) return await joined.ConfigureAwait(false);

            try
            {
                var result = await ClaimCoreAsync(rewardId, resource, amount, cancellationToken).ConfigureAwait(false);
                completion.TrySetResult(result);
                return result;
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }
            finally
            {
                lock (_sync) _inFlight.Remove(rewardId);
            }
        }

        async Task<ClaimResult> ClaimCoreAsync(
            string rewardId, string resource, long amount, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var token = linked.Token;

            ClaimRecord record;
            ClaimRecord previous;

            lock (_sync)
            {
                _claims.TryGetValue(rewardId, out previous);
                record = previous != null && previous.Status == ClaimStatus.Pending
                    ? previous.With(updatedAt: _clock.UtcNow)
                    : NewRecord(rewardId, resource, amount, ClaimStatus.Pending, ClaimFailure.None);
            }

            // Save the pending record first. If the process dies after this, the claim can be
            // retried. If this write fails, there is no record of the claim, so the request
            // must not be sent.
            try
            {
                await CommitAsync(record, previous).ConfigureAwait(false);
            }
            catch (VaultStorageException exception)
            {
                _logger.Error(
                    $"[PlayerVault] Claim '{rewardId}' was not started: its pending record could not be saved.",
                    exception);

                return new ClaimResult(ClaimStatus.Pending, ClaimFailure.Storage,
                    record.With(failure: ClaimFailure.Storage));
            }

            var body = JsonUtility.ToJson(new ClaimRequestBody
            {
                player_id = _playerId,
                reward_id = rewardId,
                resource = resource,
                amount = amount
            });

            for (var attempt = 1; attempt <= _retry.MaxAttempts; attempt++)
            {
                if (token.IsCancellationRequested)
                    return await SettleAsync(record.With(failure: ClaimFailure.Cancelled, updatedAt: _clock.UtcNow))
                        .ConfigureAwait(false);

                TransportResponse response;
                try
                {
                    response = await _transport.PostAsync(_apiUrl, body, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return await SettleAsync(record.With(failure: ClaimFailure.Cancelled, updatedAt: _clock.UtcNow))
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.Error($"[PlayerVault] Transport threw while claiming '{rewardId}'.", exception);
                    response = TransportResponse.Indeterminate(exception.Message);
                }

                record = record.With(
                    attempts: record.Attempts + 1,
                    updatedAt: _clock.UtcNow,
                    lastStatusCode: response.StatusCode,
                    lastError: response.Error);

                switch (response.Outcome)
                {
                    case TransportOutcome.Success:
                        // A 2xx does not guarantee a usable body. Unity does not parse the
                        // response, so a proxy's HTML error page can arrive as Success.
                        if (!VaultSerializer.LooksLikeJsonObject(response.Body))
                        {
                            _logger.Warn($"[PlayerVault] Claim '{rewardId}' got {response.StatusCode} with a non-JSON body.");
                            return await SettleAsync(record.With(
                                status: ClaimStatus.Failed,
                                failure: ClaimFailure.Parse,
                                lastError: "response body was not a JSON object")).ConfigureAwait(false);
                        }

                        return await CommitGrantAsync(rewardId, resource, amount, record).ConfigureAwait(false);

                    case TransportOutcome.Rejected:
                        _logger.Warn($"[PlayerVault] Claim '{rewardId}' rejected with {response.StatusCode}: {response.Error}");
                        return await SettleAsync(record.With(
                            status: ClaimStatus.Failed, failure: ClaimFailure.Rejected)).ConfigureAwait(false);

                    default:
                        record = record.With(failure: ClaimFailure.Network);
                        await TryCommitAsync(record).ConfigureAwait(false);

                        if (attempt < _retry.MaxAttempts)
                        {
                            try
                            {
                                await _clock.DelayAsync(_retry.DelayForAttempt(attempt, _random), token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                return await SettleAsync(
                                    record.With(failure: ClaimFailure.Cancelled, updatedAt: _clock.UtcNow))
                                    .ConfigureAwait(false);
                            }
                        }

                        break;
                }
            }

            // Out of attempts. The claim stays Pending and is retried on the next launch.
            return ClaimResult.From(record);

            // Saves an outcome that does not grant anything. If the write fails, the claim is
            // still pending on disk and will be retried on the next launch, so memory and disk
            // agree.
            async Task<ClaimResult> SettleAsync(ClaimRecord settled)
            {
                try
                {
                    await CommitAsync(settled, record).ConfigureAwait(false);
                    return ClaimResult.From(settled);
                }
                catch (VaultStorageException exception)
                {
                    _logger.Error($"[PlayerVault] Claim '{rewardId}' could not record its outcome.", exception);
                    return new ClaimResult(ClaimStatus.Pending, ClaimFailure.Storage, record);
                }
            }
        }

        /// <summary>
        /// Grants the reward: the balance, the record and the granted set are updated under one
        /// lock and saved in one write.
        /// </summary>
        async Task<ClaimResult> CommitGrantAsync(string rewardId, string resource, long amount, ClaimRecord record)
        {
            ClaimRecord granted = null;
            ClaimResult? alreadyGranted = null;
            long applied = 0;
            long version = 0;
            string payload = null;

            lock (_sync)
            {
                // Second check behind the in-flight table: if this reward was granted while the
                // request was out, do not apply it again.
                if (_granted.Contains(rewardId))
                {
                    alreadyGranted = new ClaimResult(ClaimStatus.AlreadyGranted, ClaimFailure.None, _claims[rewardId]);
                }
                else
                {
                    applied = ApplyClampedLocked(resource, amount);
                    granted = record.With(
                        status: ClaimStatus.Granted,
                        failure: ClaimFailure.None,
                        amountApplied: applied,
                        updatedAt: _clock.UtcNow);

                    _granted.Add(rewardId);
                    _claims[rewardId] = granted;
                    payload = SerializeLocked(out version);
                }
            }

            if (alreadyGranted.HasValue) return alreadyGranted.Value;

            try
            {
                await WriteAsync(version, payload).ConfigureAwait(false);
            }
            catch (VaultStorageException exception)
            {
                // Undo the grant instead of reporting one that would be lost on restart. The
                // claim goes back to pending, which matches what is on disk, and is retried later.
                ClaimRecord rolledBack;
                lock (_sync)
                {
                    SubtractLocked(resource, applied);
                    _granted.Remove(rewardId);
                    rolledBack = record.With(failure: ClaimFailure.Storage, updatedAt: _clock.UtcNow);
                    _claims[rewardId] = rolledBack;
                }

                _logger.Error(
                    $"[PlayerVault] Claim '{rewardId}' was accepted but could not be saved; it stays pending.",
                    exception);

                RaiseClaimStateChanged(rolledBack);
                return new ClaimResult(ClaimStatus.Pending, ClaimFailure.Storage, rolledBack);
            }

            RaiseClaimStateChanged(granted);
            RaiseBalanceChanged(resource);
            return ClaimResult.From(granted);
        }

        /// <summary>The saved record for a reward id, or null if it was never claimed.</summary>
        public ClaimRecord GetClaim(string rewardId)
        {
            if (string.IsNullOrEmpty(rewardId)) return null;
            lock (_sync) return _claims.TryGetValue(rewardId, out var record) ? record : null;
        }

        /// <summary>Every claim that is still pending.</summary>
        public IReadOnlyList<ClaimRecord> PendingClaims
        {
            get
            {
                var pending = new List<ClaimRecord>();
                lock (_sync)
                {
                    foreach (var record in _claims.Values)
                        if (record.Status == ClaimStatus.Pending) pending.Add(record);
                }

                return pending;
            }
        }

        /// <summary>
        /// Retries every pending claim, one at a time. Safe to call repeatedly: a reward that is
        /// already granted is skipped.
        /// </summary>
        public async Task ResumePendingAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            foreach (var record in PendingClaims)
            {
                if (cancellationToken.IsCancellationRequested) return;
                await ClaimAsync(record.RewardId, record.Resource, record.AmountRequested, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        void ResumePendingDetached()
        {
            _ = ResumeQuietlyAsync();

            async Task ResumeQuietlyAsync()
            {
                try { await ResumePendingAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (Exception exception) { _logger.Error("[PlayerVault] Resuming pending claims failed.", exception); }
            }
        }

        bool IsKnown(string resource)
        {
            if (!IsValidResourceName(resource)) return false;
            lock (_sync) return IsKnownLocked(resource);
        }

        ClaimRecord NewRecord(string rewardId, string resource, long amount, ClaimStatus status, ClaimFailure failure)
        {
            var now = _clock.UtcNow;
            return new ClaimRecord(rewardId, resource, amount, 0, status, failure, 0, now, now, 0, null);
        }

        /// <summary>
        /// Stores a record and writes the whole document. If the write fails, the in-memory
        /// record is reset to <paramref name="fallback"/> (null removes it), so memory never
        /// shows a change that was not saved.
        /// </summary>
        async Task CommitAsync(ClaimRecord record, ClaimRecord fallback)
        {
            long version;
            string payload;

            lock (_sync)
            {
                _claims[record.RewardId] = record;
                payload = SerializeLocked(out version);
            }

            try
            {
                await WriteAsync(version, payload).ConfigureAwait(false);
            }
            catch
            {
                lock (_sync)
                {
                    if (fallback == null) _claims.Remove(record.RewardId);
                    else _claims[record.RewardId] = fallback;
                }

                throw;
            }

            RaiseClaimStateChanged(record);
        }

        /// <summary>
        /// Saves progress that is not a state change, such as the attempt count or last error.
        /// Losing one of these only loses diagnostics, so a failure is logged and ignored.
        /// </summary>
        async Task TryCommitAsync(ClaimRecord record)
        {
            try
            {
                await CommitAsync(record, record).ConfigureAwait(false);
            }
            catch (VaultStorageException exception)
            {
                _logger.Error($"[PlayerVault] Could not record progress for claim '{record.RewardId}'.", exception);
            }
        }

        // ---------------------------------------------------------------- persistence

        /// <summary>
        /// Loads saved state. Called by <see cref="OpenAsync"/>.
        /// </summary>
        /// <exception cref="VaultStorageException">
        /// The save exists but could not be read. This is kept separate from "no save", because
        /// treating an unreadable file as a new player would overwrite it with starting balances
        /// on the first write.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The save is corrupt and the policy is <see cref="CorruptDataPolicy.Throw"/>, the save
        /// was written by a newer SDK version, or it belongs to a different player.
        /// </exception>
        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            // Waits for a vault that is still closing on this save, such as the previous
            // scene's, and throws if one is open.
            await OpenVaults.AcquireAsync(_saveKey, this,
                $"A vault for player '{_playerId}' is already open on this storage. Two vaults on one " +
                "save overwrite each other's writes: use the open one, or close it before opening another.")
                .ConfigureAwait(false);

            if (_disposed)
            {
                OpenVaults.Release(_saveKey, this);
                throw new ObjectDisposedException(nameof(Vault));
            }

            string payload;
            try
            {
                payload = await _storage.ReadAsync(_playerId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new VaultStorageException(
                    _playerId,
                    $"PlayerVault could not read the stored state for '{_playerId}'. " +
                    "Opening failed; the existing save has not been touched.",
                    exception);
            }

            if (!string.IsNullOrEmpty(payload))
            {
                if (VaultSerializer.TryDeserialize(payload, out var document, out var error))
                {
                    Apply(document);
                }
                else if (_onCorruptData == CorruptDataPolicy.Throw)
                {
                    throw new InvalidOperationException($"PlayerVault state for '{_playerId}' is unreadable: {error}");
                }
                else
                {
                    _logger.Error($"[PlayerVault] State for '{_playerId}' is unreadable ({error}). Quarantining and starting fresh.");
                    try { await _storage.QuarantineAsync(_playerId, cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) { _logger.Error("[PlayerVault] Quarantine failed.", exception); }
                }
            }

            lock (_sync) _loaded = true;
        }

        void Apply(VaultDocument document)
        {
            if (document.schemaVersion > VaultDocument.CurrentSchemaVersion)
            {
                // An older build reading a newer save would drop the fields it does not know
                // and lose them on the next write, so refuse to load it.
                throw new InvalidOperationException(
                    $"PlayerVault state for '{_playerId}' was written by a newer SDK " +
                    $"(schema {document.schemaVersion} > {VaultDocument.CurrentSchemaVersion}).");
            }

            if (!string.Equals(document.playerId, _playerId, StringComparison.Ordinal))
            {
                // Backup check in case two player ids ever map to the same file name.
                throw new InvalidOperationException(
                    $"PlayerVault state at the location for '{_playerId}' belongs to " +
                    $"'{document.playerId}'. Refusing to load it.");
            }

            lock (_sync)
            {
                foreach (var entry in document.balances)
                {
                    if (string.IsNullOrEmpty(entry.key)) continue;
                    if (!_declared.ContainsKey(entry.key) && !_allowUndeclaredResources) continue;

                    var value = entry.value;
                    var max = GetMax(entry.key);
                    if (max.HasValue && value > max.Value) value = max.Value;   // a lowered maximum applies on load
                    if (value < 0) value = 0;

                    _balances[entry.key] = value;
                }

                foreach (var entry in document.claims)
                {
                    if (string.IsNullOrEmpty(entry.rewardId)) continue;

                    var record = VaultSerializer.ToRecord(entry);
                    _claims[record.RewardId] = record;
                    if (record.Status == ClaimStatus.Granted) _granted.Add(record.RewardId);
                }
            }
        }

        Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_loaded) return Task.CompletedTask;
                return _loading ??= LoadOnceAsync(cancellationToken);
            }
        }

        async Task LoadOnceAsync(CancellationToken cancellationToken)
        {
            try { await LoadAsync(cancellationToken).ConfigureAwait(false); }
            finally { lock (_sync) _loading = null; }
        }

        /// <summary>Writes the current state and completes when it is saved.</summary>
        /// <exception cref="VaultStorageException">The write failed. Nothing is rolled back;
        /// a flush saves whatever is in memory.</exception>
        public Task FlushAsync()
        {
            ThrowIfDisposed();
            return PersistAsync();
        }

        /// <summary>
        /// Flushes, disposes, and completes once the last write has finished. Use this for a
        /// clean shutdown; <see cref="Dispose"/> alone cancels in-flight work and drops debounced
        /// changes that were not written yet.
        /// </summary>
        /// <remarks>
        /// Once this returns, another vault can open the same save and will see everything this
        /// one wrote. A vault opened on the save earlier waits for this to finish anyway.
        /// </remarks>
        public async Task CloseAsync()
        {
            if (!_disposed)
            {
                // Mark the save as closing first, so an open that starts during the final flush
                // waits for it instead of failing because the save is still in use.
                OpenVaults.BeginRelease(_saveKey, this, _closed.Task);

                try { await PersistAsync().ConfigureAwait(false); }
                catch (VaultStorageException exception) { _logger.Error("[PlayerVault] Final flush failed.", exception); }
                finally { Dispose(); }
            }

            await _closed.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes a player's save, including any files quarantined as unreadable. The next open
        /// for that player starts from the configured starting balances with no claims.
        /// </summary>
        /// <remarks>
        /// Use it for a "reset progress" option or to reset a test player. Refuses while a vault
        /// is open on the save, because the open vault would write its state straight back:
        /// close it, delete, then open a new one. Waits for a vault that is still closing.
        /// </remarks>
        /// <param name="storage">The storage the vault uses. Defaults to <see cref="JsonFileStorage"/>.</param>
        /// <exception cref="InvalidOperationException">A vault is open on this save.</exception>
        /// <exception cref="NotSupportedException">The storage does not implement <see cref="IVaultStorage.DeleteAsync"/>.</exception>
        /// <exception cref="VaultStorageException">The storage failed to delete the save.</exception>
        public static async Task DeleteSaveAsync(
            string playerId, IVaultStorage storage = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("A player id is required.", nameof(playerId));

            storage ??= new JsonFileStorage();

            // Hold the save while deleting, so an open that starts meanwhile waits and then
            // loads nothing, instead of reading a half-deleted save.
            var key = OpenVaults.KeyFor(storage, playerId);
            var owner = new object();
            var deleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await OpenVaults.AcquireAsync(key, owner,
                $"The save for player '{playerId}' cannot be deleted while its vault is open. " +
                "Close the vault first; it would otherwise write its state straight back.")
                .ConfigureAwait(false);

            OpenVaults.BeginRelease(key, owner, deleted.Task);

            try
            {
                await storage.DeleteAsync(playerId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!(exception is NotSupportedException) && !(exception is OperationCanceledException))
            {
                throw new VaultStorageException(playerId, $"PlayerVault could not delete the save for '{playerId}'.", exception);
            }
            finally
            {
                OpenVaults.Release(key, owner);
                deleted.TrySetResult(true);
            }
        }

        async Task PersistAsync()
        {
            long version;
            string payload;

            lock (_sync) payload = SerializeLocked(out version);

            await WriteAsync(version, payload).ConfigureAwait(false);
        }

        /// <summary>Takes a numbered snapshot. Must be called while holding <see cref="_sync"/>.</summary>
        string SerializeLocked(out long version)
        {
            version = ++_stateVersion;
            return VaultSerializer.Serialize(SnapshotLocked());
        }

        async Task WriteAsync(long version, string payload)
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // A newer snapshot is already on disk and includes this one's changes, so
                // writing this one would move the file backwards.
                if (version <= _writtenVersion) return;

                // Once disposed, the save may already belong to a new vault, which would lose
                // whatever this write replaced.
                if (_disposed)
                    throw new ObjectDisposedException(nameof(Vault),
                        "The vault was disposed before this change was written. Use CloseAsync to save before closing.");

                await _storage.WriteAsync(_playerId, payload).ConfigureAwait(false);
                _writtenVersion = version;
            }
            catch (Exception exception)
            {
                throw new VaultStorageException(
                    _playerId, $"PlayerVault could not write the state for '{_playerId}'.", exception);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        void SchedulePersist()
        {
            if (_flushMode == FlushMode.Immediate)
            {
                _ = PersistQuietlyAsync();
                return;
            }

            lock (_sync)
            {
                if (_debouncePending) return;
                _debouncePending = true;
            }

            _ = DebouncedPersistAsync();
        }

        async Task PersistQuietlyAsync()
        {
            try { await PersistAsync().ConfigureAwait(false); }
            catch (Exception) when (_disposed)
            {
                _logger.Warn("[PlayerVault] A change made just before the vault was disposed was not saved. " +
                             "Use CloseAsync instead of Dispose to save everything first.");
            }
            catch (Exception exception) { _logger.Error("[PlayerVault] Background persist failed.", exception); }
        }

        async Task DebouncedPersistAsync()
        {
            try
            {
                await _clock.DelayAsync(_debounceInterval, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                lock (_sync) _debouncePending = false;
            }

            await PersistQuietlyAsync().ConfigureAwait(false);
        }

        VaultDocument SnapshotLocked()
        {
            var document = new VaultDocument
            {
                schemaVersion = VaultDocument.CurrentSchemaVersion,
                playerId = _playerId
            };

            foreach (var pair in _balances)
                document.balances.Add(new BalanceEntry { key = pair.Key, value = pair.Value });

            foreach (var record in _claims.Values)
                document.claims.Add(VaultSerializer.ToEntry(record));

            return document;
        }

        // ------------------------------------------------------------------- events

        void RaiseBalanceChanged(string resource)
        {
            var handler = BalanceChanged;
            if (handler == null) return;

            try
            {
                handler(resource, GetBalance(resource));
            }
            catch (Exception exception)
            {
                // The change is already saved. A handler that throws should not break the vault.
                _logger.Error($"[PlayerVault] A BalanceChanged subscriber threw for '{resource}'.", exception);
            }
        }

        void RaiseClaimStateChanged(ClaimRecord record)
        {
            var handler = ClaimStateChanged;
            if (handler == null) return;

            try
            {
                handler(record);
            }
            catch (Exception exception)
            {
                _logger.Error($"[PlayerVault] A ClaimStateChanged subscriber threw for '{record.RewardId}'.", exception);
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Vault));
        }

        /// <summary>
        /// Waits for a write that is still running, then frees the save for another vault. Every
        /// write after this sees <see cref="_disposed"/> and does not touch the save.
        /// </summary>
        async Task ReleaseSaveAsync()
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                _writeGate.Release();
            }
            finally
            {
                OpenVaults.Release(_saveKey, this);
                _closed.TrySetResult(true);
            }
        }

        /// <summary>
        /// Cancels in-flight work and stops writing. Does not flush; call <see cref="FlushAsync"/>
        /// first or use <see cref="CloseAsync"/> if there may be unsaved changes, which can happen
        /// with <see cref="FlushMode.Debounced"/>.
        /// </summary>
        /// <remarks>
        /// Returns without waiting. A write already running finishes, and a new vault opened on
        /// the same save waits for it before loading, so it never reads a stale file. Writes that
        /// had not started are dropped.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _lifetime.Cancel(); } catch (Exception) { /* ignore errors during teardown */ }

            OpenVaults.BeginRelease(_saveKey, this, _closed.Task);
            _ = ReleaseSaveAsync();

            // The semaphore and the cancellation source are not disposed. A write or claim may
            // still be finishing on another thread and would throw ObjectDisposedException when
            // it releases them. Neither holds an unmanaged handle, so the GC can collect them.
        }
    }
}
