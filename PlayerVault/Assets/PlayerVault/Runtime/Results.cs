namespace PlayerVault
{
    /// <summary>Why a spend was refused.</summary>
    public enum SpendFailure
    {
        None,
        UnknownResource,
        InvalidAmount,
        InsufficientBalance
    }

    /// <summary>
    /// Outcome of a spend. Spending never throws — a refused spend is an ordinary
    /// result the game inspects, not an exception it has to catch in the game loop.
    /// </summary>
    public readonly struct SpendResult
    {
        public bool Success { get; }
        public SpendFailure Failure { get; }

        /// <summary>Balance after the operation, or the unchanged balance if refused.</summary>
        public long Balance { get; }

        SpendResult(bool success, SpendFailure failure, long balance)
        {
            Success = success;
            Failure = failure;
            Balance = balance;
        }

        internal static SpendResult Ok(long balance) => new SpendResult(true, SpendFailure.None, balance);
        internal static SpendResult Fail(SpendFailure failure, long balance) => new SpendResult(false, failure, balance);
    }

    /// <summary>Outcome of a local grant that does not involve a reward claim.</summary>
    public readonly struct GrantResult
    {
        public bool Success { get; }
        public SpendFailure Failure { get; }

        /// <summary>
        /// How much actually landed. Less than requested when a maximum balance clamped it.
        /// </summary>
        public long AmountApplied { get; }

        public long Balance { get; }

        GrantResult(bool success, SpendFailure failure, long amountApplied, long balance)
        {
            Success = success;
            Failure = failure;
            AmountApplied = amountApplied;
            Balance = balance;
        }

        internal static GrantResult Ok(long applied, long balance) =>
            new GrantResult(true, SpendFailure.None, applied, balance);

        internal static GrantResult Fail(SpendFailure failure, long balance) =>
            new GrantResult(false, failure, 0, balance);
    }

    /// <summary>Where a claim currently stands.</summary>
    public enum ClaimStatus
    {
        /// <summary>
        /// Started and not finished. Includes claims whose outcome is genuinely unknown
        /// because a request timed out — those are indistinguishable from unfinished and
        /// are replayed on the next launch.
        /// </summary>
        Pending,

        /// <summary>Accepted by the backend and applied to the balance.</summary>
        Granted,

        /// <summary>This reward id was already granted. No request was sent.</summary>
        AlreadyGranted,

        /// <summary>Terminally refused. Only deterministic failures land here.</summary>
        Failed
    }

    /// <summary>Why a claim failed, or why it is still pending.</summary>
    public enum ClaimFailure
    {
        None,

        /// <summary>The backend refused it — 4xx. Retrying would fail identically.</summary>
        Rejected,

        /// <summary>The network could not be reached, or attempts were exhausted.</summary>
        Network,

        /// <summary>A response arrived but was not the expected JSON.</summary>
        Parse,

        /// <summary>The caller cancelled. The claim stays pending and will be replayed.</summary>
        Cancelled,

        /// <summary>The request was malformed — empty reward id, non-positive amount, unknown resource.</summary>
        Invalid
    }

    /// <summary>Outcome of a claim attempt.</summary>
    public readonly struct ClaimResult
    {
        public ClaimStatus Status { get; }
        public ClaimFailure Failure { get; }

        /// <summary>The durable record. Survives restarts and is queryable via <see cref="Vault.GetClaim"/>.</summary>
        public ClaimRecord Record { get; }

        public bool Success => Status == ClaimStatus.Granted;

        internal ClaimResult(ClaimStatus status, ClaimFailure failure, ClaimRecord record)
        {
            Status = status;
            Failure = failure;
            Record = record;
        }

        internal static ClaimResult From(ClaimRecord record) =>
            new ClaimResult(record.Status, record.Failure, record);

        internal static ClaimResult Invalid(ClaimRecord record) =>
            new ClaimResult(ClaimStatus.Failed, ClaimFailure.Invalid, record);
    }
}
