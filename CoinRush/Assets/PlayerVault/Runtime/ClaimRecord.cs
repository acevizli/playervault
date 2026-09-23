using System;

namespace PlayerVault
{
    /// <summary>
    /// The saved record of a reward claim. Immutable: the vault replaces records instead of
    /// changing them, so a record held by game code never changes.
    /// </summary>
    public sealed class ClaimRecord
    {
        public string RewardId { get; }
        public string Resource { get; }

        /// <summary>What the game asked for.</summary>
        public long AmountRequested { get; }

        /// <summary>
        /// How much was added to the balance. Lower than <see cref="AmountRequested"/> if the
        /// maximum clamped it, and zero until the claim is granted.
        /// </summary>
        public long AmountApplied { get; }

        public ClaimStatus Status { get; }
        public ClaimFailure Failure { get; }

        /// <summary>Attempts made across all sessions.</summary>
        public int Attempts { get; }

        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; }

        /// <summary>
        /// HTTP status of the most recent attempt, or -1 when no response was processed.
        /// Zero means nothing has been sent yet.
        /// </summary>
        /// <remarks>
        /// <see cref="ClaimFailure.Network"/> alone does not say whether the request was
        /// throttled, blocked by a proxy or never sent. This field does.
        /// </remarks>
        public int LastStatusCode { get; }

        /// <summary>Transport-level detail from the most recent attempt. For display and logs only.</summary>
        public string LastError { get; }

        /// <summary>True when the maximum clamped the reward and the player received less than requested.</summary>
        public bool WasClamped => Status == ClaimStatus.Granted && AmountApplied < AmountRequested;

        /// <summary>True when the claim has been sent at least once and is still pending.</summary>
        public bool IsAwaitingRetry => Status == ClaimStatus.Pending && Attempts > 0;

        internal ClaimRecord(
            string rewardId,
            string resource,
            long amountRequested,
            long amountApplied,
            ClaimStatus status,
            ClaimFailure failure,
            int attempts,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            int lastStatusCode = 0,
            string lastError = null)
        {
            RewardId = rewardId;
            Resource = resource;
            AmountRequested = amountRequested;
            AmountApplied = amountApplied;
            Status = status;
            Failure = failure;
            Attempts = attempts;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            LastStatusCode = lastStatusCode;
            LastError = lastError;
        }

        internal ClaimRecord With(
            ClaimStatus? status = null,
            ClaimFailure? failure = null,
            long? amountApplied = null,
            int? attempts = null,
            DateTimeOffset? updatedAt = null,
            int? lastStatusCode = null,
            string lastError = null)
        {
            return new ClaimRecord(
                RewardId,
                Resource,
                AmountRequested,
                amountApplied ?? AmountApplied,
                status ?? Status,
                failure ?? Failure,
                attempts ?? Attempts,
                CreatedAt,
                updatedAt ?? UpdatedAt,
                lastStatusCode ?? LastStatusCode,
                lastError ?? LastError);
        }

        /// <summary>A one-line description for a HUD or log.</summary>
        public string Describe()
        {
            switch (Status)
            {
                case ClaimStatus.Granted:
                    return WasClamped
                        ? $"granted {AmountApplied} of {AmountRequested} {Resource} (the rest overflowed the cap)"
                        : $"granted {AmountApplied} {Resource}";

                case ClaimStatus.AlreadyGranted:
                    return $"already granted {AmountApplied} {Resource}";

                case ClaimStatus.Failed:
                    return $"failed ({Failure}){DetailSuffix()}";

                default:
                    return Attempts == 0
                        ? $"not sent yet ({Failure})"
                        : $"pending after {Attempts} attempt(s) ({Failure}){DetailSuffix()}";
            }
        }

        string DetailSuffix()
        {
            if (LastStatusCode > 0 && !string.IsNullOrEmpty(LastError)) return $" — HTTP {LastStatusCode}: {LastError}";
            if (LastStatusCode > 0) return $" — HTTP {LastStatusCode}";
            if (!string.IsNullOrEmpty(LastError)) return $" — {LastError}";
            return string.Empty;
        }

        public override string ToString() =>
            $"Claim({RewardId}, {Resource} {AmountApplied}/{AmountRequested}, {Status}, {Failure}, attempts={Attempts})";
    }
}
