using System;
using System.Collections.Generic;
using PlayerVault;
using UnityEngine;

namespace CoinRush
{
    /// <summary>The current phase. Controls what the HUD shows and whether input is used.</summary>
    public enum LevelPhase
    {
        /// <summary>The vault has not finished opening. Nothing is playable yet.</summary>
        Booting,

        /// <summary>The level select is showing. The arena is cleared.</summary>
        Menu,

        Playing,
        Completed,
        GameOver
    }

    /// <summary>
    /// Runs a level: builds it, handles pickups and lost lives, decides when the run ends, and
    /// sells locked levels for coins.
    ///
    /// Coins, lives and unlocked levels are PlayerVault resources. This class reads balances
    /// instead of storing them, and changes come back through
    /// <see cref="VaultBehaviour.BalanceChanged"/>. That way a pending claim retried at launch
    /// updates the HUD without any extra game code, and the vault is the save file.
    /// </summary>
    public sealed class LevelController : MonoBehaviour
    {
        /// <summary>Resource keys. Declared on the VaultBehaviour in the scene; these must match.</summary>
        public const string CoinsResource = "coins";
        public const string LivesResource = "lives";

        /// <summary>
        /// Each level after the first has an unlock resource, such as <c>level-3-unlocked</c>,
        /// with a maximum of 1.
        /// </summary>
        /// <remarks>
        /// Levels can be bought in any order, so each one needs its own flag instead of a single
        /// level counter. Storing them as vault resources means a purchase is one
        /// <see cref="Vault.TransactAsync"/> call in the same save as everything else. A resource
        /// with a maximum of 1 works as a boolean: the vault refuses a second grant, so a double
        /// tap cannot charge twice.
        /// </remarks>
        const string UnlockKeyFormat = "level-{0}-unlocked";

        /// <summary>The fallback reward id for a level with none authored.</summary>
        const string RewardIdFormat = "level-{0}-first-clear";

        [Header("Scene")]
        [SerializeField] BallController ball;
        [SerializeField] ArenaBuilder arena;
        [SerializeField] VaultBehaviour vaultBehaviour;

        [Header("Levels")]
        [SerializeField] List<LevelDefinition> levels = new List<LevelDefinition>();

        [Header("Economy")]
        [SerializeField] int livesPerRun = 3;
        [SerializeField] long coinsPerPickup = 10;

        Vault _vault;

        /// <summary>
        /// The level the player picked. Stored separately so any unlocked level can be replayed.
        /// </summary>
        int _selected;

        /// <summary>Coins still on the board this run.</summary>
        public int CoinsRemaining { get; private set; }

        public LevelPhase Phase { get; private set; } = LevelPhase.Booting;

        /// <summary>The claim for the level being played, or null if it has never been cleared.</summary>
        public ClaimRecord Claim { get; private set; }

        /// <summary>How many claims are still pending. Usually zero.</summary>
        public int PendingClaimCount { get; private set; }

        /// <summary>Whether the retry button should be shown.</summary>
        /// <remarks>
        /// The vault retries pending rewards on the next launch anyway, so a retry that changed
        /// nothing is not offered again right away. The button hides when tapped and comes back
        /// when the set of pending claims changes or the app returns to the foreground. The HUD
        /// keeps showing that a reward is pending either way.
        /// </remarks>
        public bool RetryOffered { get; private set; }

        /// <summary>The open vault, for read-only inspection. Null until it opens.</summary>
        public Vault Vault => _vault;

        public long Coins => _vault?.GetBalance(CoinsResource) ?? 0;
        public long Lives => _vault?.GetBalance(LivesResource) ?? 0;

        /// <summary>The lives maximum, so the HUD can show "2/3" instead of "2".</summary>
        public long? LivesMax => _vault?.GetMax(LivesResource);

        public int LevelCount => levels.Count;

        /// <summary>Index of the level the player last chose from the menu.</summary>
        public int CurrentIndex => Mathf.Clamp(_selected, 0, Mathf.Max(0, levels.Count - 1));

        public LevelDefinition CurrentLevel =>
            levels.Count == 0 ? null : levels[CurrentIndex];

        public LevelDefinition LevelAt(int index) => levels[index];

        /// <summary>
        /// The unlock resource key for a level: the one set on the level, or one based on its
        /// position.
        /// </summary>
        public string UnlockKeyFor(int index) =>
            Authored(index, levels.Count > index && index >= 0 ? levels[index].unlockKey : null, UnlockKeyFormat);

        /// <summary>The one-time reward id for a level: the one set on it, or one based on its position.</summary>
        public string RewardIdFor(int index) =>
            Authored(index, levels.Count > index && index >= 0 ? levels[index].rewardId : null, RewardIdFormat);

        /// <summary>
        /// Uses the id set on the level; if it is empty, falls back to one based on position.
        /// </summary>
        /// <remarks>
        /// Position-based ids break when levels are inserted or reordered: every level after the
        /// change gets a different id, so saved rewards move to the wrong level. Ids set by hand do
        /// not change, and <see cref="WarnIfIdsCollide"/> catches duplicates.
        /// </remarks>
        static string Authored(int index, string authored, string format) =>
            string.IsNullOrWhiteSpace(authored) ? string.Format(format, index + 1) : authored.Trim();

        /// <summary>The first level is free; the others are unlocked through vault resources.</summary>
        public bool IsUnlocked(int index) =>
            index == 0 || (_vault != null && _vault.GetBalance(UnlockKeyFor(index)) > 0);

        public long UnlockCostOf(int index) =>
            index >= 0 && index < levels.Count ? levels[index].unlockCost : 0;

        /// <summary>
        /// Whether the player can afford a locked level right now. The vault decides, and pending
        /// claims do not reserve any of the balance.
        /// </summary>
        public bool CanAfford(int index) =>
            _vault != null && _vault.CanSpend(CoinsResource, UnlockCostOf(index));

        public event Action<long> CoinsChanged;
        public event Action<long> LivesChanged;
        public event Action<LevelPhase> PhaseChanged;
        /// <summary>
        /// Raised with the outcome of a claim and the record behind it.
        /// </summary>
        /// <remarks>
        /// The status is passed separately from the record. Claiming a reward again returns
        /// <see cref="ClaimStatus.AlreadyGranted"/> with the original record, whose status is still
        /// <see cref="ClaimStatus.Granted"/>. A listener that only read the record would show a new
        /// reward every time the level is replayed.
        /// </remarks>
        public event Action<ClaimStatus, ClaimRecord> ClaimChanged;

        /// <summary>Raised when the level being played changes, or is rebuilt.</summary>
        public event Action<LevelDefinition> LevelChanged;

        /// <summary>Raised when the number of pending claims changes.</summary>
        public event Action<int> PendingClaimsChanged;

        /// <summary>Raised when <see cref="RetryOffered"/> changes, so the HUD can show or hide the button.</summary>
        public event Action<bool> RetryOfferChanged;

        /// <summary>Raised when the set of unlocked levels changes, so the menu can refresh.</summary>
        public event Action UnlocksChanged;

        /// <summary>Short messages for the HUD, such as clamped grants or refused purchases.</summary>
        public event Action<string> Notice;

        void Start()
        {
            if (ball != null)
            {
                ball.Fell += OnBallFell;
            }

            if (vaultBehaviour == null)
            {
                Debug.LogError("[CoinRush] No VaultBehaviour assigned — the game cannot track resources.");
                return;
            }

            if (levels.Count == 0)
            {
                Debug.LogError("[CoinRush] No levels defined.");
                return;
            }

            // Check IsOpen first: VaultBehaviour sets Vault and raises Opened together, so a
            // listener added afterwards would miss the event. A failed open is also handled, so the
            // player is told their progress could not be loaded instead of seeing every level locked.
            vaultBehaviour.OpenFailed += OnVaultOpenFailed;

            if (vaultBehaviour.IsOpen)
            {
                OnVaultOpened(vaultBehaviour.Vault);
            }
            else
            {
                vaultBehaviour.Opened += OnVaultOpened;
            }
        }

        void OnDestroy()
        {
            if (ball != null)
            {
                ball.Fell -= OnBallFell;
            }

            if (vaultBehaviour != null)
            {
                vaultBehaviour.Opened -= OnVaultOpened;
                vaultBehaviour.OpenFailed -= OnVaultOpenFailed;
                vaultBehaviour.BalanceChanged -= OnBalanceChanged;
                vaultBehaviour.ClaimStateChanged -= OnClaimStateChanged;
            }

            UnsubscribeFromLevel();
        }

        /// <summary>
        /// Replays the current level. Called by the HUD's full-screen tap button instead of reading
        /// the pointer here. A pointer read cannot tell whether the tap hit the unlock button, so one
        /// tap could both buy a level and restart the old one. uGUI's raycasting avoids that.
        /// </summary>
        public void RequestReplay()
        {
            if (Phase != LevelPhase.Completed && Phase != LevelPhase.GameOver) return;
            StartRun();
        }

        void OnVaultOpened(Vault vault)
        {
            _vault = vault;

            // Subscribe on VaultBehaviour instead of the vault. The vault raises events on background
            // threads, and these handlers update HUD text, which must happen on the main thread.
            vaultBehaviour.BalanceChanged += OnBalanceChanged;
            vaultBehaviour.ClaimStateChanged += OnClaimStateChanged;

            WarnIfUnlockResourcesMissing();
            WarnIfIdsCollide();

            // The vault is already retrying claims left from an earlier session. Show their current
            // state in the HUD from the start.
            Claim = _vault.GetClaim(RewardIdFor(CurrentIndex));
            if (Claim != null)
            {
                ClaimChanged?.Invoke(Claim.Status, Claim);
            }

            RefreshPendingClaims();
            ShowMenu();
        }

        void OnVaultOpenFailed(Exception exception)
        {
            Debug.LogError($"[CoinRush] The vault could not be opened: {exception.Message}");
            Notice?.Invoke("COULD NOT LOAD YOUR PROGRESS");
        }

        /// <summary>
        /// The unlock resources are declared on the VaultBehaviour and the levels are listed here, so
        /// the two can get out of sync. A missing resource does not crash (the purchase is refused),
        /// but it is a bug, so it is logged at startup.
        /// </summary>
        void WarnIfUnlockResourcesMissing()
        {
            var balances = _vault.Balances;

            for (var i = 1; i < levels.Count; i++)
            {
                var key = UnlockKeyFor(i);
                if (balances.ContainsKey(key)) continue;

                Debug.LogWarning(
                    $"[CoinRush] Level {i + 1} ('{levels[i].name}') has no '{key}' resource declared " +
                    "on the VaultBehaviour, so it can never be unlocked. Add it with Initial 0, Max 1.");
            }
        }

        /// <summary>
        /// Two levels with the same reward id or unlock key would share progress: clearing one would
        /// mark both cleared, and buying one would unlock both. Checked once at startup.
        /// </summary>
        void WarnIfIdsCollide()
        {
            var rewards = new Dictionary<string, int>(StringComparer.Ordinal);
            var unlocks = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < levels.Count; i++)
            {
                Check(rewards, RewardIdFor(i), i, "reward id");
                if (i > 0) Check(unlocks, UnlockKeyFor(i), i, "unlock key");
            }

            void Check(Dictionary<string, int> seen, string id, int index, string what)
            {
                if (seen.TryGetValue(id, out var first))
                {
                    Debug.LogError(
                        $"[CoinRush] Levels {first + 1} and {index + 1} share the {what} '{id}'. " +
                        "Their progress is the same progress; give one of them a distinct id.");
                    return;
                }

                seen[id] = index;
            }
        }

        /// <summary>
        /// Clears the arena and shows the level select. After a run the player returns here and
        /// chooses what to play next; every unlocked level can be replayed.
        /// </summary>
        public void ShowMenu()
        {
            if (_vault == null || levels.Count == 0) return;

            Freeze();
            UnsubscribeFromLevel();   // before Clear: clearing empties the lists we unsubscribe from
            arena.Clear();

            if (ball != null) ball.ResetToSpawn();

            SetPhase(LevelPhase.Menu);
        }

        /// <summary>
        /// Plays a level chosen from the menu. Locked levels are refused instead of bought, so a tap
        /// meant to start a level never spends coins.
        /// </summary>
        public void SelectLevel(int index)
        {
            if (_vault == null || index < 0 || index >= levels.Count) return;

            if (!IsUnlocked(index))
            {
                Notice?.Invoke($"{levels[index].name.ToUpperInvariant()} IS LOCKED");
                return;
            }

            _selected = index;
            StartRun();
        }

        /// <summary>Refills lives and builds the current level.</summary>
        public void StartRun()
        {
            if (_vault == null || levels.Count == 0)
            {
                return;
            }

            // Grant the full amount and let the resource maximum clamp it, instead of computing the
            // difference here. Zero applied means lives were already full.
            var granted = _vault.GrantLocal(LivesResource, livesPerRun);
            if (!granted.Success)
            {
                Debug.LogError($"[CoinRush] Could not grant lives: {granted.Failure}.");
            }
            else if (granted.AmountApplied == 0)
            {
                Notice?.Invoke("LIVES ALREADY FULL");
            }
            else if (granted.AmountApplied < livesPerRun)
            {
                Notice?.Invoke($"LIVES +{granted.AmountApplied} (CAPPED AT {LivesMax})");
            }

            BuildLevel();
            SetPhase(LevelPhase.Playing);

            if (ball != null)
            {
                ball.enabled = true;
                ball.ResetToSpawn();
            }

            // Read but not announced. Raising it here left the last run's "+100 COINS" banner on
            // screen for the whole next run.
            Claim = _vault.GetClaim(RewardIdFor(CurrentIndex));

            CoinsChanged?.Invoke(Coins);
            LivesChanged?.Invoke(Lives);
            LevelChanged?.Invoke(CurrentLevel);
            RefreshPendingClaims();
        }

        /// <summary>
        /// Buys a locked level. Levels can be bought in any order. A refused purchase is shown as a
        /// <see cref="Notice"/>.
        /// </summary>
        public void TryUnlock(int index)
        {
            if (_vault == null || index <= 0 || index >= levels.Count) return;
            if (IsUnlocked(index)) return;

            var cost = UnlockCostOf(index);

            // Checked first only to show a clearer message. The transaction would refuse it anyway.
            if (!_vault.CanSpend(CoinsResource, cost))
            {
                Notice?.Invoke($"NEED {cost - Coins} MORE COINS");
                return;
            }

            // One transaction instead of a spend and a grant. With two calls, a crash between them
            // would charge the player without unlocking the level. TransactAsync saves both in
            // one write.
            StartCoroutine(vaultBehaviour.TransactRoutine(
                VaultTransaction.Purchase(CoinsResource, cost, UnlockKeyFor(index)),
                result => OnUnlockSettled(index, result)));
        }

        void OnUnlockSettled(int index, TransactionResult result)
        {
            if (!result.Success)
            {
                // Nothing was charged or granted, so there is nothing to refund.
                Notice?.Invoke(DescribeSpendFailure(result.Failure));

                if (result.Failure == SpendFailure.UnknownResource)
                {
                    Debug.LogError(
                        $"[CoinRush] '{result.FailedResource}' is not declared on the VaultBehaviour, " +
                        $"so level {index + 1} can never be unlocked.");
                }

                return;
            }

            Notice?.Invoke($"UNLOCKED {levels[index].name.ToUpperInvariant()}");
            SelectLevel(index);
        }

        /// <summary>
        /// Asks the vault to retry all pending claims now instead of on the next launch.
        /// </summary>
        public void RetryPendingClaims()
        {
            if (_vault == null || !RetryOffered || PendingClaimCount == 0) return;

            // Hide the offer before the retry finishes, so a second tap does not queue another retry.
            SetRetryOffered(false);

            Notice?.Invoke("RETRYING PENDING REWARDS");
            StartCoroutine(vaultBehaviour.ResumePendingRoutine(OnRetrySettled));
        }

        /// <summary>
        /// Called when a manual retry finishes. If the pending claims changed,
        /// <see cref="RefreshPendingClaims"/> has already shown the button again. If not, the button
        /// stays hidden and the player is told the reward is saved.
        /// </summary>
        void OnRetrySettled()
        {
            var before = PendingClaimCount;
            RefreshPendingClaims();

            if (PendingClaimCount > 0 && PendingClaimCount == before)
            {
                Notice?.Invoke("STILL UNREACHABLE - THE REWARD IS SAVED AND RETRIES ON ITS OWN");
            }
        }

        /// <summary>
        /// Returning to the foreground may mean the network is back, so the retry button is shown
        /// again. Both callbacks are used because platforms differ: iOS pauses a backgrounded app,
        /// while the Editor only loses focus.
        /// </summary>
        void OnApplicationFocus(bool focused)
        {
            if (focused) OfferRetryIfPending();
        }

        void OnApplicationPause(bool paused)
        {
            if (!paused) OfferRetryIfPending();
        }

        void OfferRetryIfPending()
        {
            if (PendingClaimCount > 0) SetRetryOffered(true);
        }

        void SetRetryOffered(bool offered)
        {
            if (offered == RetryOffered) return;

            RetryOffered = offered;
            RetryOfferChanged?.Invoke(offered);
        }

        static string DescribeSpendFailure(SpendFailure failure)
        {
            switch (failure)
            {
                case SpendFailure.InsufficientBalance: return "NOT ENOUGH COINS";
                case SpendFailure.UnknownResource: return "UNKNOWN RESOURCE";
                case SpendFailure.InvalidAmount: return "INVALID AMOUNT";
                case SpendFailure.StorageUnavailable: return "COULD NOT SAVE - NOTHING CHARGED";
                case SpendFailure.AtMaximum: return "ALREADY UNLOCKED";
                default: return "PURCHASE REFUSED";
            }
        }

        void BuildLevel()
        {
            UnsubscribeFromLevel();
            arena.Build(CurrentLevel);

            foreach (var coin in arena.Coins)
            {
                coin.Collected += OnCoinCollected;
            }

            foreach (var hazard in arena.Hazards)
            {
                hazard.Touched += OnHazardTouched;
            }

            CoinsRemaining = arena.Coins.Count;
        }

        void UnsubscribeFromLevel()
        {
            if (arena == null)
            {
                return;
            }

            foreach (var coin in arena.Coins)
            {
                if (coin != null) coin.Collected -= OnCoinCollected;
            }

            foreach (var hazard in arena.Hazards)
            {
                if (hazard != null) hazard.Touched -= OnHazardTouched;
            }
        }

        void OnCoinCollected(Coin coin)
        {
            if (Phase != LevelPhase.Playing)
            {
                return;
            }

            // A pickup is a local grant, not a claim. The server has no say over coins the game
            // spawned, and sending each pickup over the network would make play depend on it.
            var granted = _vault.GrantLocal(CoinsResource, coinsPerPickup);
            if (!granted.Success)
            {
                Debug.LogError($"[CoinRush] Could not grant coins: {granted.Failure}.");
            }

            CoinsRemaining--;

            if (CoinsRemaining <= 0)
            {
                CompleteLevel();
            }
        }

        void OnHazardTouched(Hazard hazard) => LoseLife();

        void OnBallFell() => LoseLife();

        void LoseLife()
        {
            if (Phase != LevelPhase.Playing)
            {
                return;
            }

            var result = _vault.Spend(LivesResource, 1);

            // The vault decides whether there are lives left. Any failure other than
            // InsufficientBalance is a bug, such as a mistyped resource key, so it is logged.
            if (!result.Success)
            {
                if (result.Failure != SpendFailure.InsufficientBalance)
                {
                    Debug.LogError($"[CoinRush] Spending a life failed unexpectedly: {result.Failure}.");
                }

                EndRun();
                return;
            }

            if (result.Balance <= 0)
            {
                EndRun();
                return;
            }

            if (ball != null)
            {
                ball.ResetToSpawn();
            }
        }

        void EndRun()
        {
            Freeze();
            SetPhase(LevelPhase.GameOver);
        }

        void CompleteLevel()
        {
            Freeze();
            SetPhase(LevelPhase.Completed);

            // Uses the coroutine instead of `await vault.ClaimAsync(...)`. An await in a
            // MonoBehaviour usually resumes on the main thread too, but a coroutine always does
            // because Unity drives it. The vault's events are different: they arrive on background
            // threads, which is why they are read through VaultBehaviour.
            StartCoroutine(vaultBehaviour.ClaimRoutine(
                RewardIdFor(CurrentIndex), CoinsResource, CurrentLevel.firstClearReward, OnClaimCompleted));
        }

        void OnClaimCompleted(ClaimResult result)
        {
            RefreshPendingClaims();

            // Only show the result if it is for the level on screen. A slow claim for one level can
            // finish after the player has started another.
            if (result.Record == null || result.Record.RewardId != RewardIdFor(CurrentIndex)) return;

            // AlreadyGranted does not change the claim, so ClaimStateChanged does not fire for it.
            // Reading the result here lets the HUD show it on a repeat clear.
            Claim = result.Record;
            ClaimChanged?.Invoke(result.Status, Claim);
        }

        void OnBalanceChanged(string resource, long balance)
        {
            if (resource == CoinsResource) CoinsChanged?.Invoke(balance);
            else if (resource == LivesResource) LivesChanged?.Invoke(balance);

            // Every other resource in this game is a level unlock. Using the vault event instead of
            // the purchase path means the level select updates however the unlock happened.
            else UnlocksChanged?.Invoke();
        }

        void OnClaimStateChanged(ClaimRecord record)
        {
            RefreshPendingClaims();

            if (record.RewardId != RewardIdFor(CurrentIndex))
            {
                return;
            }

            Claim = record;
            ClaimChanged?.Invoke(record.Status, record);
        }

        void RefreshPendingClaims()
        {
            var count = _vault?.PendingClaims.Count ?? 0;
            if (count == PendingClaimCount) return;

            PendingClaimCount = count;

            // Show the retry button again when the pending set changes: a new pending reward has not
            // been retried by hand yet, and one that just finished means the network is up. Set
            // before raising either event so both handlers see the same state.
            SetRetryOffered(count > 0);

            PendingClaimsChanged?.Invoke(count);
        }

        void Freeze()
        {
            if (ball == null)
            {
                return;
            }

            // Disabling the controller stops input and forces but keeps the Rigidbody simulated,
            // so the ball comes to rest instead of freezing in the air.
            ball.enabled = false;
        }

        void SetPhase(LevelPhase phase)
        {
            Phase = phase;
            PhaseChanged?.Invoke(phase);
        }
    }
}
