using System;
using System.Collections.Generic;

namespace PlayerVault.Internal
{
    /// <summary>
    /// The saved file format. One document holds balances and claims (the granted set is
    /// derived from the claims), so a single atomic write saves all of it.
    /// </summary>
    /// <remarks>
    /// Uses public fields and a list of key/value pairs because <c>UnityEngine.JsonUtility</c>
    /// cannot serialize properties or dictionaries. Using Newtonsoft instead would add a
    /// dependency to every game that imports the package.
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
    /// Enums are stored as strings, so reordering an enum does not change the meaning of
    /// saved claims.
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

        // Diagnostics. Added after schema 1 without a version bump: JsonUtility ignores
        // unknown fields and fills missing ones with defaults, so old and new builds can
        // read each other's files.
        public int lastStatusCode;
        public string lastError;
    }

    /// <summary>
    /// The claim request body. Field names are snake_case because JsonUtility cannot rename
    /// fields when serializing.
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
