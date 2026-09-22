using System;
using PlayerVault;
using UnityEngine;
using UnityEngine.InputSystem;

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
    /// Owns the run: laying out the level, reacting to pickups and deaths, and deciding when the run
    /// is over.
    ///
    /// It no longer owns the *numbers*. Coins and lives are PlayerVault resources now, so this class
    /// reads balances rather than tracking them, and every change to a balance arrives back through
    /// <see cref="Vault.BalanceChanged"/> rather than being pushed out from here. That inversion is
    /// what lets a pending claim replayed at launch — from a session that was offline when the level
    /// was cleared — move the HUD without a single line of game code being involved.
    /// </summary>
    public sealed class LevelController : MonoBehaviour
    {
        /// <summary>Resource keys. Declared on the VaultBehaviour in the scene; these must match.</summary>
        public const string CoinsResource = "coins";
        public const string LivesResource = "lives";

        /// <summary>
        /// The reward id. Stable and specific: it identifies *this* milestone for *this* level, so it
        /// stays meaningful when a second level shows up. The vault grants it exactly once per player,
        /// forever, across reinstalls of the scene and restarts of the app.
        /// </summary>
        public const string FirstClearReward = "level-complete-first-time";

        [Header("Scene")]
        [SerializeField] BallController ball;
        [SerializeField] ArenaBuilder arena;
        [SerializeField] VaultBehaviour vaultBehaviour;

        [Header("Economy")]
        [SerializeField] int livesPerRun = 3;
        [SerializeField] long coinsPerPickup = 10;
        [SerializeField] long firstClearReward = 100;

        Vault _vault;

        /// <summary>Coins still on the board this run.</summary>
        public int CoinsRemaining { get; private set; }

        public LevelPhase Phase { get; private set; } = LevelPhase.Booting;

        /// <summary>The most recent claim, or null if the level has never been cleared this session.</summary>
        public ClaimRecord Claim { get; private set; }

        public long Coins => _vault?.GetBalance(CoinsResource) ?? 0;
        public long Lives => _vault?.GetBalance(LivesResource) ?? 0;

        public event Action<long> CoinsChanged;
        public event Action<long> LivesChanged;
        public event Action<LevelPhase> PhaseChanged;
        public event Action<ClaimRecord> ClaimChanged;

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

        void Update()
        {
            if (Phase == LevelPhase.Playing || Phase == LevelPhase.Booting)
            {
                return;
            }

            // Once the run has ended the same tap that steered the ball restarts the level. Reading the
            // pointer directly keeps the restart working on a phone with no on-screen button to hit.
            var pointer = Pointer.current;
            if (pointer != null && pointer.press.wasPressedThisFrame)
            {
                StartRun();
            }
        }

        void OnVaultOpened(Vault vault)
        {
            _vault = vault;

            // Subscribed on the wrapper, not on the vault itself. The vault raises its events on
            // whichever thread finished the work, and these handlers end up writing HUD text — which
            // is a main-thread-only operation. VaultBehaviour re-raises them on the main thread.
            vaultBehaviour.BalanceChanged += OnBalanceChanged;
            vaultBehaviour.ClaimStateChanged += OnClaimStateChanged;

            // A claim left unfinished by an earlier session is already being replayed by now — the
            // vault resumes on open. Surface whatever it knows so the HUD is honest from frame one.
            Claim = _vault.GetClaim(FirstClearReward);
            if (Claim != null)
            {
                ClaimChanged?.Invoke(Claim);
            }

            StartRun();
        }

        /// <summary>Tops lives back up and lays out a fresh level.</summary>
        public void StartRun()
        {
            if (_vault == null)
            {
                return;
            }

            // Grant the full allowance and let the resource maximum absorb the excess rather than
            // computing the difference here. The SDK already knows the ceiling; duplicating that
            // arithmetic in the game is how the two drift apart.
            _vault.GrantLocal(LivesResource, livesPerRun);

            BuildLevel();
            SetPhase(LevelPhase.Playing);

            if (ball != null)
            {
                ball.enabled = true;
                ball.ResetToSpawn();
            }

            CoinsChanged?.Invoke(Coins);
            LivesChanged?.Invoke(Lives);
        }

        void BuildLevel()
        {
            UnsubscribeFromLevel();
            arena.Build();

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
            _vault.GrantLocal(CoinsResource, coinsPerPickup);
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
            // decides whether the balance covers it.
            if (!result.Success || result.Balance <= 0)
            {
                Freeze();
                SetPhase(LevelPhase.GameOver);
                return;
            }

            if (ball != null)
            {
                ball.ResetToSpawn();
            }
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
                FirstClearReward, CoinsResource, firstClearReward, OnClaimCompleted));
        }

        void OnClaimCompleted(ClaimResult result)
        {
            // AlreadyGranted returns without sending a request, and so without moving the claim's
            // state — ClaimStateChanged never fires for it. Reading the returned result is what makes
            // the second clear of the level say so rather than silently showing nothing.
            Claim = result.Record;
            ClaimChanged?.Invoke(Claim);
        }

        void OnBalanceChanged(string resource, long balance)
        {
            if (resource == CoinsResource) CoinsChanged?.Invoke(balance);
            else if (resource == LivesResource) LivesChanged?.Invoke(balance);
        }

        void OnClaimStateChanged(ClaimRecord record)
        {
            if (record.RewardId != FirstClearReward)
            {
                return;
            }

            Claim = record;
            ClaimChanged?.Invoke(record);
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
