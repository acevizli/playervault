using System;
using System.Collections.Generic;

namespace PlayerVault.Internal
{
    /// <summary>
    /// The on-disk shape. One document holds balances, claims and — implicitly — the
    /// granted-reward guard, so a single atomic write commits all three together.
    /// </summary>
    /// <remarks>
    /// Public fields rather than properties, and a list of key/value pairs rather than a
    /// dictionary, because <c>UnityEngine.JsonUtility</c> serializes neither properties
    /// nor dictionaries. The alternative was taking a dependency on Newtonsoft, which
    /// every consuming game would then inherit — a poor trade for a package whose whole
    /// selling point is dropping into any project.
    /// </remarks>
    [Serializable]
    public class VaultDocument
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;
        public string playerId;
        public List<BalanceEntry> balances = new List<BalanceEntry>();
        public List<ClaimEntry> claims = new List<ClaimEntry>();
    }

    [Serializable]
    public class BalanceEntry
    {
        public string key;
        public long value;
    }

    /// <summary>
    /// Enums are stored as strings, not ints, so a reordered enum cannot silently
    /// reinterpret a player's saved claims as something else.
    /// </summary>
    [Serializable]
    public class ClaimEntry
    {
        public string rewardId;
        public string resource;
        public long amountRequested;
        public long amountApplied;
        public string status;
        public string failure;
        public int attempts;
        public string createdAt;
        public string updatedAt;
    }

    /// <summary>
    /// The request body, matching the shape the case specifies. Field names are literally
    /// snake_case because JsonUtility cannot rename fields on the way out.
    /// </summary>
    [Serializable]
    public class ClaimRequestBody
    {
        public string player_id;
        public string reward_id;
        public string resource;
        public long amount;
    }
}
