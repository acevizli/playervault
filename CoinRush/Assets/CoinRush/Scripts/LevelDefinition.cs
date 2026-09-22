using System;
using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// One level's shape and price, authored in the Inspector.
    ///
    /// Levels are data rather than scenes. Three scenes that differ only in how many coins sit on a
    /// ring would be three places to fix the same bug, and loading one costs a frame hitch that a
    /// ring rebuild does not. The trade is the same one <see cref="ArenaBuilder"/> already makes:
    /// no hand-placed geometry, so no art direction per level.
    /// </summary>
    [Serializable]
    public sealed class LevelDefinition
    {
        [Tooltip("Shown on the HUD. Flavour only — the reward id is derived from the level's position.")]
        public string name = "LEVEL";

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
