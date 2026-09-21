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
    /// A plain C# object the game owns — not a singleton, not a MonoBehaviour — so a game
    /// can hold one per player, and tests can create one per test with no scene.
    /// <see cref="VaultBehaviour"/> wraps it for teams who want a component instead.
    /// </para>
    /// <para>
    /// Sealed on purpose. Every seam worth replacing is an interface on
    /// <see cref="VaultConfig"/>; extension happens by composition. What is not extensible
    /// is the claim state machine, because its ordering is the only thing standing between
    /// the SDK and a reward granted twice.
    /// </para>
    /// <para>
    /// Not thread safe. All calls are expected on Unity's main thread.
    /// </para>
    /// </remarks>
    public sealed class Vault : IDisposable
    {
        readonly VaultConfig _config;
        readonly IVaultTransport _transport;
        readonly IVaultStorage _storage;
        readonly IVaultClock _clock;
        readonly IVaultLogger _logger;
        readonly RetryPolicy _retry;
        readonly System.Random _random = new System.Random();

        readonly Dictionary<string, ResourceDefinition> _definitions =
            new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal);

        readonly Dictionary<string, long> _balances = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly Dictionary<string, ClaimRecord> _claims = new Dictionary<string, ClaimRecord>(StringComparer.Ordinal);

        /// <summary>
        /// The at-most-once guard. Rebuilt from claims at load rather than stored separately,
        /// so it cannot drift out of step with the records it is derived from.
        /// </summary>
        readonly HashSet<string> _granted = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Joins concurrent claims for the same reward instead of racing them.</summary>
        readonly Dictionary<string, Task<ClaimResult>> _inFlight =
            new Dictionary<string, Task<ClaimResult>>(StringComparer.Ordinal);

        readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        bool _loaded;
        bool _disposed;
        bool _debouncePending;

        public string PlayerId => _config.PlayerId;

        /// <summary>Raised after a balance change has been committed. (resource, new balance)</summary>
        public event Action<string, long> BalanceChanged;

        /// <summary>Raised whenever a claim moves. Useful for driving UI without polling.</summary>
        public event Action<ClaimRecord> ClaimStateChanged;

        /// <summary>
        /// Constructs a vault. Throws on an invalid configuration — a bad config is a
        /// programming error and should fail on the first frame, not silently later.
        /// The vault is not usable until <see cref="LoadAsync"/> has run; prefer
        /// <see cref="OpenAsync"/>, which does both.
        /// </summary>
        public Vault(VaultConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Validate();

            _retry = _config.Retry;
            _transport = _config.Transport ?? new UnityWebRequestTransport(_config.Retry.Timeout);
            _storage = _config.Storage ?? new JsonFileStorage();
            _clock = _config.Clock ?? new SystemClock();
            _logger = _config.Logger ?? new UnityLogger();

            foreach (var definition in _config.Resources)
            {
                _definitions[definition.Key] = definition;
                _balances[definition.Key] = definition.Initial;
            }
        }

        /// <summary>
        /// Constructs, loads persisted state, and — unless disabled — starts replaying
        /// unfinished claims. The replay is detached, so opening never blocks on the network.
        /// </summary>
        public static async Task<Vault> OpenAsync(VaultConfig config, CancellationToken cancellationToken = default)
        {
            var vault = new Vault(config);
            await vault.LoadAsync(cancellationToken).ConfigureAwait(false);

            if (config.ResumePendingOnOpen)
                vault.ResumePendingDetached();

            return vault;
        }

        // ---------------------------------------------------------------- resources

        /// <summary>Current balance. Unknown resources read as zero; this never throws.</summary>
        public long GetBalance(string resource)
        {
            if (string.IsNullOrEmpty(resource)) return 0;
            return _balances.TryGetValue(resource, out var balance) ? balance : 0;
        }

        /// <summary>The configured ceiling for a resource, or null when unbounded.</summary>
        public long? GetMax(string resource)
        {
            if (resource != null && _definitions.TryGetValue(resource, out var definition)) return definition.Max;
            return null;
        }

        public IReadOnlyDictionary<string, long> Balances => _balances;

        public bool CanSpend(string resource, long amount) =>
            amount > 0 && IsKnown(resource) && GetBalance(resource) >= amount;

        /// <summary>
        /// Deducts from a balance. Refused when the balance is short, and a refusal is an
        /// ordinary result rather than an exception.
        /// </summary>
        /// <remarks>
        /// Mutates in memory and schedules the write. Use <see cref="SpendAsync"/> when the
        /// spend must be on disk before the game acts on it — for instance before granting
        /// an item the player paid for.
        /// <para>
        /// A pending claim never reserves or holds any amount, so spending is always
        /// available regardless of what is in flight.
        /// </para>
        /// </remarks>
        public SpendResult Spend(string resource, long amount)
        {
            var result = SpendInMemory(resource, amount);
            if (result.Success) SchedulePersist();
            return result;
        }

        /// <summary>As <see cref="Spend"/>, but the returned task completes once the change is durable.</summary>
        public async Task<SpendResult> SpendAsync(string resource, long amount)
        {
            var result = SpendInMemory(resource, amount);
            if (result.Success) await PersistAsync().ConfigureAwait(false);
            return result;
        }

        SpendResult SpendInMemory(string resource, long amount)
        {
            ThrowIfDisposed();

            if (amount <= 0) return SpendResult.Fail(SpendFailure.InvalidAmount, GetBalance(resource));
            if (!IsKnown(resource)) return SpendResult.Fail(SpendFailure.UnknownResource, 0);

            var balance = GetBalance(resource);
            if (balance < amount) return SpendResult.Fail(SpendFailure.InsufficientBalance, balance);

            var updated = balance - amount;
            _balances[resource] = updated;
            BalanceChanged?.Invoke(resource, updated);
            return SpendResult.Ok(updated);
        }

        /// <summary>
        /// Adds to a balance without contacting the backend — for income the game awards
        /// locally, such as coins picked up in a level. Clamped by any configured maximum.
        /// Rewards that must be granted once and only once go through
        /// <see cref="ClaimAsync"/> instead.
        /// </summary>
        public GrantResult GrantLocal(string resource, long amount)
        {
            ThrowIfDisposed();

            if (amount <= 0) return GrantResult.Fail(SpendFailure.InvalidAmount, GetBalance(resource));

            if (!IsKnown(resource))
            {
                if (!_config.AllowUndeclaredResources)
                    return GrantResult.Fail(SpendFailure.UnknownResource, 0);

                _balances[resource] = 0;
            }

            var applied = ApplyClamped(resource, amount);
            var balance = GetBalance(resource);

            if (applied > 0)
            {
                BalanceChanged?.Invoke(resource, balance);
                SchedulePersist();
            }

            return GrantResult.Ok(applied, balance);
        }

        bool IsKnown(string resource) =>
            !string.IsNullOrEmpty(resource) &&
            (_definitions.ContainsKey(resource) || _balances.ContainsKey(resource) || _config.AllowUndeclaredResources);

        /// <summary>
        /// Adds <paramref name="amount"/> to a balance, clamped by the resource maximum,
        /// and returns how much actually landed.
        /// </summary>
        long ApplyClamped(string resource, long amount)
        {
            var current = GetBalance(resource);
            var max = GetMax(resource);

            long target;
            if (amount > long.MaxValue - current) target = long.MaxValue;
            else target = current + amount;

            if (max.HasValue && target > max.Value) target = max.Value;
            if (target < current) target = current;

            _balances[resource] = target;
            return target - current;
        }

        // ------------------------------------------------------------------- claims

        /// <summary>
        /// Claims a reward: sends the request, and applies the resource once the backend
        /// accepts. A reward id already granted returns immediately without a request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ordering here is the load-bearing part of the SDK. A pending record is made
        /// durable <i>before</i> the request goes out, and the balance, the record and the
        /// granted-reward guard are then committed in a single write. Any other ordering
        /// either loses a reward or grants it twice when the process dies mid-claim.
        /// </para>
        /// <para>
        /// Exhausting the retry budget leaves the claim <see cref="ClaimStatus.Pending"/>,
        /// not failed, so an entirely offline session replays on the next launch. Only a
        /// deterministic refusal is terminal.
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

            if (string.IsNullOrWhiteSpace(rewardId) || amount <= 0 || !IsKnown(resource))
            {
                var invalid = NewRecord(rewardId ?? string.Empty, resource, amount,
                    ClaimStatus.Failed, ClaimFailure.Invalid);
                return ClaimResult.Invalid(invalid);
            }

            if (_granted.Contains(rewardId))
                return new ClaimResult(ClaimStatus.AlreadyGranted, ClaimFailure.None, _claims[rewardId]);

            if (_inFlight.TryGetValue(rewardId, out var running))
                return await running.ConfigureAwait(false);

            var task = ClaimCoreAsync(rewardId, resource, amount, cancellationToken);
            _inFlight[rewardId] = task;

            try { return await task.ConfigureAwait(false); }
            finally { _inFlight.Remove(rewardId); }
        }

        async Task<ClaimResult> ClaimCoreAsync(
            string rewardId, string resource, long amount, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var token = linked.Token;

            var record = _claims.TryGetValue(rewardId, out var existing) && existing.Status == ClaimStatus.Pending
                ? existing.With(updatedAt: _clock.UtcNow)
                : NewRecord(rewardId, resource, amount, ClaimStatus.Pending, ClaimFailure.None);

            // Write-ahead. If the process dies after this point the claim is replayable;
            // before it, there is no evidence it ever happened.
            record = await CommitAsync(record).ConfigureAwait(false);

            var body = JsonUtility.ToJson(new ClaimRequestBody
            {
                player_id = PlayerId,
                reward_id = rewardId,
                resource = resource,
                amount = amount
            });

            for (var attempt = 1; attempt <= _retry.MaxAttempts; attempt++)
            {
                if (token.IsCancellationRequested)
                    return ClaimResult.From(await CommitAsync(
                        record.With(failure: ClaimFailure.Cancelled, updatedAt: _clock.UtcNow)).ConfigureAwait(false));

                TransportResponse response;
                try
                {
                    response = await _transport.PostAsync(_config.ApiUrl, body, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return ClaimResult.From(await CommitAsync(
                        record.With(failure: ClaimFailure.Cancelled, updatedAt: _clock.UtcNow)).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    _logger.Error($"[PlayerVault] Transport threw while claiming '{rewardId}'.", exception);
                    response = TransportResponse.Indeterminate(exception.Message);
                }

                record = record.With(attempts: record.Attempts + 1, updatedAt: _clock.UtcNow);

                switch (response.Outcome)
                {
                    case TransportOutcome.Success:
                        // A 2xx is not proof of a usable body: Unity's download handler does
                        // no parsing, so a proxy's HTML error page arrives as Success.
                        if (!VaultSerializer.LooksLikeJsonObject(response.Body))
                        {
                            _logger.Warn($"[PlayerVault] Claim '{rewardId}' got {response.StatusCode} with a non-JSON body.");
                            record = record.With(status: ClaimStatus.Failed, failure: ClaimFailure.Parse);
                            return ClaimResult.From(await CommitAsync(record).ConfigureAwait(false));
                        }

                        // The atomic commit: balance, record and guard reach disk together.
                        var applied = ApplyClamped(resource, amount);
                        record = record.With(
                            status: ClaimStatus.Granted,
                            failure: ClaimFailure.None,
                            amountApplied: applied);
                        _granted.Add(rewardId);

                        record = await CommitAsync(record).ConfigureAwait(false);

                        BalanceChanged?.Invoke(resource, GetBalance(resource));
                        return ClaimResult.From(record);

                    case TransportOutcome.Rejected:
                        _logger.Warn($"[PlayerVault] Claim '{rewardId}' rejected with {response.StatusCode}.");
                        record = record.With(status: ClaimStatus.Failed, failure: ClaimFailure.Rejected);
                        return ClaimResult.From(await CommitAsync(record).ConfigureAwait(false));

                    default:
                        record = record.With(failure: ClaimFailure.Network);
                        record = await CommitAsync(record).ConfigureAwait(false);

                        if (attempt < _retry.MaxAttempts)
                        {
                            try
                            {
                                await _clock.DelayAsync(_retry.DelayForAttempt(attempt, _random), token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                return ClaimResult.From(await CommitAsync(
                                    record.With(failure: ClaimFailure.Cancelled, updatedAt: _clock.UtcNow))
                                    .ConfigureAwait(false));
                            }
                        }
                        break;
                }
            }

            // Budget exhausted. Deliberately still Pending: the next launch replays it.
            return ClaimResult.From(record);
        }

        /// <summary>The durable record for a reward id, or null if never attempted.</summary>
        public ClaimRecord GetClaim(string rewardId)
        {
            if (string.IsNullOrEmpty(rewardId)) return null;
            return _claims.TryGetValue(rewardId, out var record) ? record : null;
        }

        /// <summary>Every claim that has not reached a terminal state.</summary>
        public IReadOnlyList<ClaimRecord> PendingClaims
        {
            get
            {
                var pending = new List<ClaimRecord>();
                foreach (var record in _claims.Values)
                    if (record.Status == ClaimStatus.Pending) pending.Add(record);
                return pending;
            }
        }

        /// <summary>
        /// Retries every unfinished claim, one at a time. Safe to call repeatedly — the
        /// granted guard makes a replay of an already-granted reward a no-op.
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
                catch (Exception exception) { _logger.Error("[PlayerVault] Resuming pending claims failed.", exception); }
            }
        }

        ClaimRecord NewRecord(string rewardId, string resource, long amount, ClaimStatus status, ClaimFailure failure)
        {
            var now = _clock.UtcNow;
            return new ClaimRecord(rewardId, resource, amount, 0, status, failure, 0, now, now);
        }

        /// <summary>Stores a record in memory and writes the whole document in one go.</summary>
        async Task<ClaimRecord> CommitAsync(ClaimRecord record)
        {
            _claims[record.RewardId] = record;
            await PersistAsync().ConfigureAwait(false);
            ClaimStateChanged?.Invoke(record);
            return record;
        }

        // ---------------------------------------------------------------- persistence

        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            string payload = null;
            try
            {
                payload = await _storage.ReadAsync(PlayerId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error("[PlayerVault] Could not read stored state; starting fresh.", exception);
            }

            if (!string.IsNullOrEmpty(payload))
            {
                if (VaultSerializer.TryDeserialize(payload, out var document, out var error))
                {
                    Apply(document);
                }
                else if (_config.OnCorruptData == CorruptDataPolicy.Throw)
                {
                    throw new InvalidOperationException($"PlayerVault state for '{PlayerId}' is unreadable: {error}");
                }
                else
                {
                    _logger.Error($"[PlayerVault] State for '{PlayerId}' is unreadable ({error}). Quarantining and starting fresh.");
                    try { await _storage.QuarantineAsync(PlayerId, cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) { _logger.Error("[PlayerVault] Quarantine failed.", exception); }
                }
            }

            _loaded = true;
        }

        void Apply(VaultDocument document)
        {
            if (document.schemaVersion > VaultDocument.CurrentSchemaVersion)
            {
                // Refuse rather than truncate. A newer save read by an older build is almost
                // always a downgraded client, and silently dropping the fields it does not
                // understand would destroy the player's progress on the next write.
                throw new InvalidOperationException(
                    $"PlayerVault state for '{PlayerId}' was written by a newer SDK " +
                    $"(schema {document.schemaVersion} > {VaultDocument.CurrentSchemaVersion}).");
            }

            foreach (var entry in document.balances)
            {
                if (string.IsNullOrEmpty(entry.key)) continue;
                if (!_definitions.ContainsKey(entry.key) && !_config.AllowUndeclaredResources) continue;

                var value = entry.value;
                var max = GetMax(entry.key);
                if (max.HasValue && value > max.Value) value = max.Value;   // a lowered ceiling applies on load
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

        async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (!_loaded) await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Writes pending changes and completes when they are durable.</summary>
        public Task FlushAsync()
        {
            ThrowIfDisposed();
            return PersistAsync();
        }

        async Task PersistAsync()
        {
            var document = Snapshot();
            var payload = VaultSerializer.Serialize(document);

            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _storage.WriteAsync(PlayerId, payload).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // In-memory state is ahead of disk. The next launch reads disk, which is the
                // safe direction: a reward may be re-requested, never double-granted.
                _logger.Error("[PlayerVault] Persisting state failed; in-memory state is ahead of disk.", exception);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        void SchedulePersist()
        {
            if (_config.FlushMode == FlushMode.Immediate)
            {
                _ = PersistQuietlyAsync();
                return;
            }

            if (_debouncePending) return;
            _debouncePending = true;
            _ = DebouncedPersistAsync();
        }

        async Task PersistQuietlyAsync()
        {
            try { await PersistAsync().ConfigureAwait(false); }
            catch (Exception exception) { _logger.Error("[PlayerVault] Background persist failed.", exception); }
        }

        async Task DebouncedPersistAsync()
        {
            try
            {
                await _clock.DelayAsync(_config.DebounceInterval, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _debouncePending = false;
            }

            await PersistQuietlyAsync().ConfigureAwait(false);
        }

        VaultDocument Snapshot()
        {
            var document = new VaultDocument
            {
                schemaVersion = VaultDocument.CurrentSchemaVersion,
                playerId = PlayerId
            };

            foreach (var pair in _balances)
                document.balances.Add(new BalanceEntry { key = pair.Key, value = pair.Value });

            foreach (var record in _claims.Values)
                document.claims.Add(VaultSerializer.ToEntry(record));

            return document;
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Vault));
        }

        /// <summary>
        /// Cancels in-flight work. Does not flush — call <see cref="FlushAsync"/> first if
        /// there may be unwritten changes, which there can be under
        /// <see cref="FlushMode.Debounced"/>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _lifetime.Cancel(); } catch (Exception) { /* nothing useful to do while tearing down */ }

            _lifetime.Dispose();
            _writeGate.Dispose();
        }
    }
}
