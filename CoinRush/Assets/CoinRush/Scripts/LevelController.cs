using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CoinRush
{
    /// <summary>The phase the level is in. Drives what the HUD shows and whether input matters.</summary>
    public enum LevelPhase
    {
        Playing,
        Completed,
        GameOver
    }

    /// <summary>
    /// Owns the game's state: how many coins have been banked, how many lives are left, and whether
    /// the level is still running.
    ///
    /// Note that the counters below are plain <c>int</c> fields belonging to CoinRush. PlayerVault is
    /// not referenced anywhere in this class, on purpose — the game is written first as a game would
    /// be written, and the SDK takes ownership of these two numbers later. Doing it in that order is
    /// the only way to find out whether the SDK actually integrates, rather than having been quietly
    /// designed into the game from birth.
    /// </summary>
    public sealed class LevelController : MonoBehaviour
    {
        [SerializeField] BallController ball;
        [SerializeField] ArenaBuilder arena;
        [SerializeField] int startingLives = 3;
        [SerializeField] int coinsPerPickup = 10;

        /// <summary>Coins banked this run.</summary>
        public int Coins { get; private set; }

        /// <summary>Lives remaining.</summary>
        public int Lives { get; private set; }

        /// <summary>Coins still on the board.</summary>
        public int CoinsRemaining { get; private set; }

        public LevelPhase Phase { get; private set; } = LevelPhase.Playing;

        public event Action<int> CoinsChanged;
        public event Action<int> LivesChanged;
        public event Action<LevelPhase> PhaseChanged;

        void Start()
        {
            if (ball != null)
            {
                ball.Fell += OnBallFell;
            }

            StartRun();
        }

        void OnDestroy()
        {
            if (ball != null)
            {
                ball.Fell -= OnBallFell;
            }

            UnsubscribeFromLevel();
        }

        void Update()
        {
            if (Phase == LevelPhase.Playing)
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

        /// <summary>Resets every counter and lays out a fresh level.</summary>
        public void StartRun()
        {
            Coins = 0;
            Lives = startingLives;

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

            Coins += coinsPerPickup;
            CoinsRemaining--;
            CoinsChanged?.Invoke(Coins);

            if (CoinsRemaining <= 0)
            {
                // The hook. When PlayerVault lands, this is where the reward claim fires — the arena
                // being cleared is the event a server would be told about.
                Freeze();
                SetPhase(LevelPhase.Completed);
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

            Lives--;
            LivesChanged?.Invoke(Lives);

            if (Lives <= 0)
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
