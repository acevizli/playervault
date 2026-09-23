using System;
using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// One level's layout and price, set in the Inspector.
    ///
    /// Levels are data instead of scenes. Scenes that differ only in coin count would mean fixing
    /// the same bug in several places, and loading a scene causes a hitch that rebuilding the ring
    /// does not. As with <see cref="ArenaBuilder"/>, nothing is placed by hand.
    /// </summary>
    [Serializable]
    public sealed class LevelDefinition
    {
        [Tooltip("Shown on the HUD. Display only; progress is saved under the ids below.")]
        public string name = "LEVEL";

        [Header("Identity")]
        [Tooltip("Fixed id for this level's one-time first-clear reward. Set by hand so that reordering " +
                 "or inserting levels cannot move a reward to a different level. If empty, the level's " +
                 "position is used, which breaks if the list order changes.")]
        public string rewardId;

        [Tooltip("Fixed resource key that records owning this level. Must also be declared on the " +
                 "VaultBehaviour with Initial 0 and Max 1. If empty, the level's position is used.")]
        public string unlockKey;

        [Header("Layout")]
        public int coinCount = 8;
        public float coinRingRadius = 9f;
        public int hazardCount = 3;
        public float hazardRingRadius = 5.5f;

        [Tooltip("How fast hazards wander, in units per second. Zero leaves them parked.")]
        public float hazardSpeed;

        [Tooltip("Average seconds between a wandering hazard's course changes. Lower is more erratic.")]
        public float hazardTurnSeconds = 2f;

        [Header("Economy")]
        [Tooltip("Coins spent to unlock this level. Ignored for the first level, which is always open.")]
        public long unlockCost;

        [Tooltip("Coins granted by the backend the first time this level is cleared, and never again.")]
        public long firstClearReward = 100;
    }
}
