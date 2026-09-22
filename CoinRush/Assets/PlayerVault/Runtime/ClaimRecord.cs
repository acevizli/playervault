using System;

namespace PlayerVault
{
    /// <summary>
    /// The durable record of a reward claim. Immutable — the vault replaces records
    /// rather than mutating them, so a record handed to game code can never change
    /// underneath it.
    /// </summary>
    public sealed class ClaimRecord
    {
        public string RewardId { get; }
        public string Resource { get; }

        /// <summary>What the game asked for.</summary>
        public long AmountRequested { get; }

        /// <summary>
        /// What actually landed in the balance. Lower than <see cref="AmountRequested"/>
        /// when a maximum balance clamped it, and zero until the claim is granted.
        /// </summary>
        public long AmountApplied { get; }

        public ClaimStatus Status { get; }
        public ClaimFailure Failure { get; }

        /// <summary>Attempts made across all sessions.</summary>
        public int Attempts { get; }

        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; }

        /// <summary>True when the claim is clamped and the player received less than the reward stated.</summary>
        public bool WasClamped => Status == ClaimStatus.Granted && AmountApplied < AmountRequested;

        internal ClaimRecord(
            string rewardId,
            string resource,
            long amountRequested,
            long amountApplied,
            ClaimStatus status,
            ClaimFailure failure,
            int attempts,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt)
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
        }

        internal ClaimRecord With(
            ClaimStatus? status = null,
            ClaimFailure? failure = null,
            long? amountApplied = null,
            int? attempts = null,
            DateTimeOffset? updatedAt = null)
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
                updatedAt ?? UpdatedAt);
        }

        public override string ToString() =>
            $"Claim({RewardId}, {Resource} {AmountApplied}/{AmountRequested}, {Status}, {Failure}, attempts={Attempts})";
    }
}
