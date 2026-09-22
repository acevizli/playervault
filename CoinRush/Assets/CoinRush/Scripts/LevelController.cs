using System;
using System.Collections.Generic;
using PlayerVault;
using UnityEngine;

namespace CoinRush
{
    /// <summary>The phase the level is in. Drives what the HUD shows and whether input matters.</summary>
    public enum LevelPhase
    {
        /// <summary>The vault has not finished opening. Nothing is playable yet.</summary>
        Booting,
        Playing,
        Completed,
        GameOver
    }

    /// <summary>
    /// Owns the run: laying out the level, reacting to pickups and deaths, deciding when the run is
    /// over, and selling the next level for coins.
    ///
    /// It does not own the <i>numbers</i>. Coins, lives and even which levels are unlocked are
    /// PlayerVault resources, so this class reads balances rather than tracking them, and every
    /// change arrives back through <see cref="VaultBehaviour.BalanceChanged"/> rather than being
    /// pushed out from here. That inversion is what lets a pending claim replayed at launch — from a
    /// session that was offline when the level was cleared — move the HUD without a single line of
    /// game code being involved. It is also why progression survives a reinstall of the scene: the
    /// vault is the save file.
    /// </summary>
    public sealed class LevelController : MonoBehaviour
    {
        /// <summary>Resource keys. Declared on the VaultBehaviour in the scene; these must match.</summary>
        public const string CoinsResource = "coins";
        public const string LivesResource = "lives";

        /// <summary>
        /// How many levels the player has bought, as a resource rather than a PlayerPrefs int.
        /// Making it a resource means unlocking is a <see cref="Vault.Spend"/> and a
        /// <see cref="Vault.GrantLocal"/> against the same durable document as everything else —
        /// one save file, one write, no second persistence mechanism to keep in step.
        /// </summary>
        public const string LevelsResource = "levels";

        /// <summary>
        /// Reward ids are derived from the level's position, not authored per level. Hand-typed ids
        /// are a duplicate away from two levels sharing a once-only reward, and the vault would
        /// honour that duplicate exactly as asked.
        /// </summary>
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

        /// <summary>Coins still on the board this run.</summary>
        public int CoinsRemaining { get; private set; }

        public LevelPhase Phase { get; private set; } = LevelPhase.Booting;

        /// <summary>The claim for the level being played, or null if it has never been cleared.</summary>
        public ClaimRecord Claim { get; private set; }

        /// <summary>How many claims the vault is still trying to settle. Zero most of the time.</summary>
        public int PendingClaimCount { get; private set; }

        /// <summary>The open vault, for read-only inspection. Null until it opens.</summary>
        public Vault Vault => _vault;

        public long Coins => _vault?.GetBalance(CoinsResource) ?? 0;
        public long Lives => _vault?.GetBalance(LivesResource) ?? 0;

        /// <summary>The lives ceiling, so the HUD can render "2/3" rather than a bare "2".</summary>
        public long? LivesMax => _vault?.GetMax(LivesResource);

        /// <summary>How many levels are unlocked. Always at least one — the first is free.</summary>
        public int UnlockedLevels => Mathf.Max(1, (int)(_vault?.GetBalance(LevelsResource) ?? 1));

        public int LevelCount => levels.Count;

        /// <summary>Index of the level being played: always the highest one the player has bought.</summary>
        public int CurrentIndex => Mathf.Clamp(UnlockedLevels - 1, 0, Mathf.Max(0, levels.Count - 1));

        public LevelDefinition CurrentLevel =>
            levels.Count == 0 ? null : levels[CurrentIndex];

        public bool HasNextLevel => CurrentIndex + 1 < levels.Count;

        public long NextUnlockCost => HasNextLevel ? levels[CurrentIndex + 1].unlockCost : 0;

        /// <summary>
        /// Whether the next level is affordable right now. The vault answers this, not the game —
        /// and it answers it without regard to claims in flight, which is the point: a reward still
        /// settling with the backend never reserves or freezes the balance the player already has.
        /// </summary>
        public bool CanAffordNextLevel =>
            _vault != null && HasNextLevel && _vault.CanSpend(CoinsResource, NextUnlockCost);

        public event Action<long> CoinsChanged;
        public event Action<long> LivesChanged;
        public event Action<LevelPhase> PhaseChanged;
        public event Action<ClaimRecord> ClaimChanged;

        /// <summary>Raised when the level being played changes, or is rebuilt.</summary>
        public event Action<LevelDefinition> LevelChanged;

        /// <summary>Raised when the number of unsettled claims changes.</summary>
        public event Action<int> PendingClaimsChanged;

        /// <summary>Short-lived messages for the HUD: clamped grants, refused purchases.</summary>
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

            // IsOpen is checked before subscribing because VaultBehaviour sets Vault and raises Opened
            // back to back; a listener attached afterwards would wait for an event that already fired.
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
                vaultBehaviour.BalanceChanged -= OnBalanceChanged;
                vaultBehaviour.ClaimStateChanged -= OnClaimStateChanged;
            }

            UnsubscribeFromLevel();
        }

        /// <summary>
        /// Replays the current level. Wired to the HUD's full-screen tap catcher rather than read
        /// from the pointer here: a raw pointer read cannot tell whether the tap landed on the unlock
        /// button, and the frame ordering between this component and the EventSystem is not defined,
        /// so the same tap could both buy a level and restart the old one. Letting uGUI's own raycast
        /// sorting decide removes the race rather than papering over it.
        /// </summary>
        public void RequestReplay()
        {
            if (Phase == LevelPhase.Playing || Phase == LevelPhase.Booting) return;
            StartRun();
        }

        void OnVaultOpened(Vault vault)
        {
            _vault = vault;

            // Subscribed on the wrapper, not on the vault itself. The vault raises its events on
            // whichever thread finished the work, and these handlers end up writing HUD text — which
            // is a main-thread-only operation. VaultBehaviour re-raises them on the main thread.
            vaultBehaviour.BalanceChanged += OnBalanceChanged;
            vaultBehaviour.ClaimStateChanged += OnClaimStateChanged;

            WarnIfLevelCapMismatched();

            // A claim left unfinished by an earlier session is already being replayed by now — the
            // vault resumes on open. Surface whatever it knows so the HUD is honest from frame one.
            Claim = _vault.GetClaim(RewardIdFor(CurrentIndex));
            if (Claim != null)
            {
                ClaimChanged?.Invoke(Claim);
            }

            RefreshPendingClaims();
            StartRun();
        }

        /// <summary>
        /// The <c>levels</c> resource carries the level count as its maximum, so the vault refuses to
        /// unlock past the end on its own. That only holds while the two agree, and they live in
        /// different files — the scene and this list — so the disagreement is worth saying out loud.
        /// </summary>
        void WarnIfLevelCapMismatched()
        {
            var max = _vault.GetMax(LevelsResource);
            if (max.HasValue && max.Value == levels.Count) return;

            Debug.LogWarning(
                $"[CoinRush] The '{LevelsResource}' resource is capped at " +
                $"{(max.HasValue ? max.Value.ToString() : "nothing")} but there are {levels.Count} levels. " +
                "Fix the resource's Max on the VaultBehaviour so unlocking cannot run off the end.");
        }

        /// <summary>Tops lives back up and lays out the current level.</summary>
        public void StartRun()
        {
            if (_vault == null || levels.Count == 0)
            {
                return;
            }

            // Grant the full allowance and let the resource maximum absorb the excess rather than
            // computing the difference here. The SDK already knows the ceiling; duplicating that
            // arithmetic in the game is how the two drift apart. What it grants back is worth
            // reading: zero applied means the player never lost a life, which is worth saying.
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

            Claim = _vault.GetClaim(RewardIdFor(CurrentIndex));
            ClaimChanged?.Invoke(Claim);

            CoinsChanged?.Invoke(Coins);
            LivesChanged?.Invoke(Lives);
            LevelChanged?.Invoke(CurrentLevel);
            RefreshPendingClaims();
        }

        /// <summary>
        /// Buys the next level with coins. Refusals come back as a <see cref="Notice"/> rather than
        /// an exception, because running out of money is an ordinary thing for a player to do.
        /// </summary>
        public void TryUnlockNextLevel()
        {
            if (_vault == null || !HasNextLevel)
            {
                return;
            }

            // Asked before spending so the refusal can be specific. Spend would refuse it anyway —
            // this is a nicer message, not a second source of truth.
            if (!_vault.CanSpend(CoinsResource, NextUnlockCost))
            {
                Notice?.Invoke($"NEED {NextUnlockCost - Coins} MORE COINS");
                return;
            }

            // SpendRoutine, not Spend: this is a purchase, and the deduction should be on disk
            // before the player is handed the thing they bought. A crash in between would otherwise
            // give the level away for free.
            StartCoroutine(vaultBehaviour.SpendRoutine(CoinsResource, NextUnlockCost, OnUnlockPaid));
        }

        void OnUnlockPaid(SpendResult result)
        {
            if (!result.Success)
            {
                Notice?.Invoke(DescribeSpendFailure(result.Failure));
                return;
            }

            var unlocked = _vault.GrantLocal(LevelsResource, 1);
            if (unlocked.AmountApplied == 0)
            {
                // Paid for something the vault would not hand over — the level cap and the level list
                // disagree. Give the coins back rather than leaving the player short.
                _vault.GrantLocal(CoinsResource, NextUnlockCost);
                Notice?.Invoke("UNLOCK FAILED — COINS REFUNDED");
                Debug.LogError($"[CoinRush] '{LevelsResource}' refused the unlock; check its Max.");
                return;
            }

            Notice?.Invoke($"UNLOCKED {CurrentLevel.name}");
            StartRun();
        }

        /// <summary>
        /// Asks the vault to retry everything still in flight, instead of waiting for the next launch.
        /// The HUD offers this so an offline claim can be seen settling rather than described.
        /// </summary>
        public void RetryPendingClaims()
        {
            if (_vault == null || PendingClaimCount == 0) return;

            Notice?.Invoke("RETRYING PENDING REWARDS");
            StartCoroutine(vaultBehaviour.ResumePendingRoutine(RefreshPendingClaims));
        }

        static string DescribeSpendFailure(SpendFailure failure)
        {
            switch (failure)
            {
                case SpendFailure.InsufficientBalance: return "NOT ENOUGH COINS";
                case SpendFailure.UnknownResource: return "UNKNOWN RESOURCE";
                case SpendFailure.InvalidAmount: return "INVALID AMOUNT";
                default: return "PURCHASE REFUSED";
            }
        }

        /// <summary>The once-only reward id for a level, derived from its position in the list.</summary>
        public static string RewardIdFor(int index) => string.Format(RewardIdFormat, index + 1);

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

            // A pickup is a local grant, not a claim. Nothing server-side authorises picking up a coin
            // that the game itself just spawned, and routing it through the network would make the
            // whole economy hostage to connectivity.
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

            // A refused spend is the authoritative "no lives left" answer — the vault, not the game,
            // decides whether the balance covers it. Reading which refusal it was keeps a genuine bug
            // (a mistyped resource key) from being silently displayed as an ordinary game over.
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

            // The coroutine bridge rather than `await vault.ClaimAsync(...)`. Awaiting directly is
            // fine for the claim itself, but the continuation resumes on the thread pool, and the
            // first thing this wants to do with the result is put it on screen. A coroutine is driven
            // by Unity's own loop, so the callback is on the main thread by construction.
            StartCoroutine(vaultBehaviour.ClaimRoutine(
                RewardIdFor(CurrentIndex), CoinsResource, CurrentLevel.firstClearReward, OnClaimCompleted));
        }

        void OnClaimCompleted(ClaimResult result)
        {
            // AlreadyGranted returns without sending a request, and so without moving the claim's
            // state — ClaimStateChanged never fires for it. Reading the returned result is what makes
            // the second clear of the level say so rather than silently showing nothing.
            Claim = result.Record;
            ClaimChanged?.Invoke(Claim);
            RefreshPendingClaims();
        }

        void OnBalanceChanged(string resource, long balance)
        {
            if (resource == CoinsResource) CoinsChanged?.Invoke(balance);
            else if (resource == LivesResource) LivesChanged?.Invoke(balance);
        }

        void OnClaimStateChanged(ClaimRecord record)
        {
            RefreshPendingClaims();

            if (record.RewardId != RewardIdFor(CurrentIndex))
            {
                return;
            }

            Claim = record;
            ClaimChanged?.Invoke(record);
        }

        void RefreshPendingClaims()
        {
            var count = _vault?.PendingClaims.Count ?? 0;
            if (count == PendingClaimCount) return;

            PendingClaimCount = count;
            PendingClaimsChanged?.Invoke(count);
        }

        void Freeze()
        {
            if (ball == null)
            {
                return;
            }

            // Disabling the controller stops it reading input and applying force, but leaves the
            // Rigidbody simulated so the ball settles naturally instead of stopping dead in mid-air.
            ball.enabled = false;
        }

        void SetPhase(LevelPhase phase)
        {
            Phase = phase;
            PhaseChanged?.Invoke(phase);
        }
    }
}
