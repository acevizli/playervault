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

        /// <summary>The level select is up. The arena is torn down and nothing is rolling.</summary>
        Menu,

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
        /// Every level past the first owns an entitlement resource — <c>level-3-unlocked</c> and so
        /// on — declared with a maximum of 1.
        /// </summary>
        /// <remarks>
        /// This started life as a single <c>levels</c> counter, which could only ever describe a
        /// prefix of the list. Levels are bought in any order now, so what is being stored is a
        /// <i>set</i>, not a count. Keeping it as resources rather than a PlayerPrefs blob means
        /// unlocking is a <see cref="Vault.SpendAsync"/> and a <see cref="Vault.GrantLocal"/>
        /// against the same durable document as everything else — one save file, one write, no
        /// second persistence mechanism to keep in step. A resource capped at 1 is how this SDK
        /// spells a boolean: the vault refuses the second grant itself, so a double tap on a
        /// purchase cannot charge twice.
        /// </remarks>
        const string UnlockKeyFormat = "level-{0}-unlocked";

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

        /// <summary>
        /// Which level the player picked. Previously derived from the unlock count, which quietly
        /// meant the newest level was the only one playable — clearing level five left no way back
        /// to level one.
        /// </summary>
        int _selected;

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

        public int LevelCount => levels.Count;

        /// <summary>Index of the level the player last chose from the menu.</summary>
        public int CurrentIndex => Mathf.Clamp(_selected, 0, Mathf.Max(0, levels.Count - 1));

        public LevelDefinition CurrentLevel =>
            levels.Count == 0 ? null : levels[CurrentIndex];

        public LevelDefinition LevelAt(int index) => levels[index];

        /// <summary>The entitlement resource key for a level, derived from its position.</summary>
        public static string UnlockKeyFor(int index) => string.Format(UnlockKeyFormat, index + 1);

        /// <summary>The first level is free; every other one is an entitlement the vault holds.</summary>
        public bool IsUnlocked(int index) =>
            index == 0 || (_vault != null && _vault.GetBalance(UnlockKeyFor(index)) > 0);

        public long UnlockCostOf(int index) =>
            index >= 0 && index < levels.Count ? levels[index].unlockCost : 0;

        /// <summary>
        /// Whether a locked level is affordable right now. The vault answers this, not the game —
        /// and it answers it without regard to claims in flight, which is the point: a reward still
        /// settling with the backend never reserves or freezes the balance the player already has.
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
        /// The status is carried separately from the record on purpose. A reward claimed a second
        /// time comes back as <see cref="ClaimStatus.AlreadyGranted"/> wrapping the <i>original</i>
        /// record, which is still <see cref="ClaimStatus.Granted"/> — correctly, because that is
        /// what happened the first time. A listener that reads only the record therefore announces
        /// a fresh grant on every replay, which is exactly the double-reward the SDK exists to
        /// prevent, faked by the game on top of an SDK that refused it.
        /// </remarks>
        public event Action<ClaimStatus, ClaimRecord> ClaimChanged;

        /// <summary>Raised when the level being played changes, or is rebuilt.</summary>
        public event Action<LevelDefinition> LevelChanged;

        /// <summary>Raised when the number of unsettled claims changes.</summary>
        public event Action<int> PendingClaimsChanged;

        /// <summary>Raised when the set of unlocked levels changes, so the menu can re-read it.</summary>
        public event Action UnlocksChanged;

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
            if (Phase != LevelPhase.Completed && Phase != LevelPhase.GameOver) return;
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

            WarnIfUnlockResourcesMissing();

            // A claim left unfinished by an earlier session is already being replayed by now — the
            // vault resumes on open. Surface whatever it knows so the HUD is honest from frame one.
            Claim = _vault.GetClaim(RewardIdFor(CurrentIndex));
            if (Claim != null)
            {
                ClaimChanged?.Invoke(Claim.Status, Claim);
            }

            RefreshPendingClaims();
            ShowMenu();
        }

        /// <summary>
        /// The entitlement resources are declared on the VaultBehaviour and the levels live in this
        /// list — two different files, so they can drift. An undeclared resource is not a crash: the
        /// vault refuses the grant and the purchase refunds itself. But it is a bug, and it reads far
        /// better here than as a level that mysteriously refuses to unlock three clears later.
        /// </summary>
        void WarnIfUnlockResourcesMissing()
        {
            for (var i = 1; i < levels.Count; i++)
            {
                var key = UnlockKeyFor(i);
                if (_vault.Balances.ContainsKey(key)) continue;

                Debug.LogWarning(
                    $"[CoinRush] Level {i + 1} ('{levels[i].name}') has no '{key}' resource declared " +
                    "on the VaultBehaviour, so it can never be unlocked. Add it with Initial 0, Max 1.");
            }
        }

        /// <summary>
        /// Tears the arena down and puts the level select up. This is where a run ends up rather
        /// than being pushed straight into the next level: which level to play is the player's
        /// choice, and every level they own stays replayable.
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
        /// Plays a level chosen from the menu. Locked levels are refused here rather than being
        /// silently bought — spending a player's coins is never a side effect of a tap meant to
        /// start a game.
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

            // Read, but deliberately not announced. The claim for a level being replayed is last
            // run's news, and re-raising it here is what left a "+100 COINS" banner sitting over the
            // whole of the next run.
            Claim = _vault.GetClaim(RewardIdFor(CurrentIndex));

            CoinsChanged?.Invoke(Coins);
            LivesChanged?.Invoke(Lives);
            LevelChanged?.Invoke(CurrentLevel);
            RefreshPendingClaims();
        }

        /// <summary>
        /// Buys any locked level outright, in whatever order the player can afford them. Refusals
        /// come back as a <see cref="Notice"/> rather than an exception, because running out of
        /// money is an ordinary thing for a player to do.
        /// </summary>
        public void TryUnlock(int index)
        {
            if (_vault == null || index <= 0 || index >= levels.Count) return;
            if (IsUnlocked(index)) return;

            var cost = UnlockCostOf(index);

            // Asked before spending so the refusal can be specific. Spend would refuse it anyway —
            // this is a nicer message, not a second source of truth.
            if (!_vault.CanSpend(CoinsResource, cost))
            {
                Notice?.Invoke($"NEED {cost - Coins} MORE COINS");
                return;
            }

            // SpendRoutine, not Spend: this is a purchase, and the deduction should be on disk
            // before the player is handed the thing they bought. A crash in between would otherwise
            // give the level away for free.
            StartCoroutine(vaultBehaviour.SpendRoutine(
                CoinsResource, cost, result => OnUnlockPaid(index, cost, result)));
        }

        void OnUnlockPaid(int index, long cost, SpendResult result)
        {
            if (!result.Success)
            {
                Notice?.Invoke(DescribeSpendFailure(result.Failure));
                return;
            }

            var unlocked = _vault.GrantLocal(UnlockKeyFor(index), 1);
            if (unlocked.AmountApplied == 0)
            {
                // Paid for something the vault would not hand over — the resource is undeclared, or
                // already at its ceiling. Give the coins back rather than leaving the player short.
                _vault.GrantLocal(CoinsResource, cost);
                Notice?.Invoke("UNLOCK FAILED - COINS REFUNDED");
                Debug.LogError(
                    $"[CoinRush] '{UnlockKeyFor(index)}' refused the unlock ({unlocked.Failure}).");
                return;
            }

            Notice?.Invoke($"UNLOCKED {levels[index].name.ToUpperInvariant()}");
            SelectLevel(index);
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
            ClaimChanged?.Invoke(result.Status, Claim);
            RefreshPendingClaims();
        }

        void OnBalanceChanged(string resource, long balance)
        {
            if (resource == CoinsResource) CoinsChanged?.Invoke(balance);
            else if (resource == LivesResource) LivesChanged?.Invoke(balance);

            // Anything else this game declares is a level entitlement. Routing the notification
            // through the vault's own event rather than raising it from the purchase path means a
            // grant from anywhere — a restored save, a future cheat menu — reaches the level select.
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
