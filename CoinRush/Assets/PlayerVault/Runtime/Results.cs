using System.Collections.Generic;

namespace PlayerVault
{
    /// <summary>Why a spend, grant or transaction was refused.</summary>
    public enum SpendFailure
    {
        None,
        UnknownResource,
        InvalidAmount,
        InsufficientBalance,

        /// <summary>
        /// A grant that had to be applied in full would go over the resource maximum. Only
        /// returned for <see cref="VaultTransaction.GrantExact"/>; a normal grant is clamped
        /// and still succeeds.
        /// </summary>
        AtMaximum,

        /// <summary>
        /// The change could not be saved, so it was also undone in memory. Only returned by
        /// the <c>Async</c> methods. The synchronous methods write in the background and log
        /// write failures instead.
        /// </summary>
        StorageUnavailable
    }

    /// <summary>
    /// Result of a spend. Spending never throws; a refused spend is reported here.
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

    /// <summary>Result of a local grant (one that does not go through a reward claim).</summary>
    public readonly struct GrantResult
    {
        public bool Success { get; }
        public SpendFailure Failure { get; }

        /// <summary>
        /// How much was actually added. Less than requested if the maximum clamped it.
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

    /// <summary>How much one resource changed in a transaction.</summary>
    public readonly struct ResourceChange
    {
        public string Resource { get; }

        /// <summary>
        /// Negative for a spend, positive for a grant. A clamped grant is smaller than
        /// requested.
        /// </summary>
        public long Applied { get; }

        public long Balance { get; }

        internal ResourceChange(string resource, long applied, long balance)
        {
            Resource = resource;
            Applied = applied;
            Balance = balance;
        }

        public override string ToString() => $"{Resource} {Applied:+#;-#;0} -> {Balance}";
    }

    /// <summary>
    /// Result of a <see cref="VaultTransaction"/>. Either every step was applied and saved,
    /// or none were.
    /// </summary>
    public readonly struct TransactionResult
    {
        public bool Success { get; }
        public SpendFailure Failure { get; }

        /// <summary>The resource whose step failed, or null on success.</summary>
        public string FailedResource { get; }

        /// <summary>The change to each resource. Empty if the transaction failed.</summary>
        public IReadOnlyList<ResourceChange> Changes { get; }

        internal TransactionResult(
            bool success, SpendFailure failure, string failedResource, IReadOnlyList<ResourceChange> changes)
        {
            Success = success;
            Failure = failure;
            FailedResource = failedResource;
            Changes = changes ?? System.Array.Empty<ResourceChange>();
        }

        /// <summary>The change to a resource, or zero if the transaction did not touch it.</summary>
        public long AppliedTo(string resource)
        {
            for (var i = 0; i < Changes.Count; i++)
                if (Changes[i].Resource == resource) return Changes[i].Applied;

            return 0;
        }

        internal static TransactionResult Ok(IReadOnlyList<ResourceChange> changes) =>
            new TransactionResult(true, SpendFailure.None, null, changes);

        internal static TransactionResult Fail(SpendFailure failure, string resource) =>
            new TransactionResult(false, failure, resource, null);
    }

    /// <summary>The current state of a claim.</summary>
    public enum ClaimStatus
    {
        /// <summary>
        /// Started but not finished. This includes claims whose request timed out, since
        /// their outcome is unknown. Pending claims are retried on the next launch.
        /// </summary>
        Pending,

        /// <summary>Accepted by the backend and applied to the balance.</summary>
        Granted,

        /// <summary>This reward id was already granted. No request was sent.</summary>
        AlreadyGranted,

        /// <summary>Refused for good. Only failures that would repeat on retry end up here.</summary>
        Failed
    }

    /// <summary>Why a claim failed, or why it is still pending.</summary>
    public enum ClaimFailure
    {
        None,

        /// <summary>The backend refused it with a 4xx status. Retrying would fail the same way.</summary>
        Rejected,

        /// <summary>The network could not be reached, or attempts were exhausted.</summary>
        Network,

        /// <summary>A response arrived but was not the expected JSON.</summary>
        Parse,

        /// <summary>The caller cancelled. The claim stays pending and will be replayed.</summary>
        Cancelled,

        /// <summary>Invalid request: empty reward id, amount not positive, or unknown resource.</summary>
        Invalid,

        /// <summary>
        /// The claim could not be saved, so nothing was granted. The claim stays pending, or
        /// was never started if its first record could not be saved.
        /// </summary>
        Storage,

        /// <summary>
        /// This reward id is already pending with a different resource or amount. The stored
        /// claim is unchanged; retry it with the original values.
        /// </summary>
        Conflict
    }

    /// <summary>Result of a claim attempt.</summary>
    public readonly struct ClaimResult
    {
        /// <summary>
        /// The result of this call. It can differ from <c>Record.Status</c>: claiming a
        /// granted reward again returns <see cref="ClaimStatus.AlreadyGranted"/> with the
        /// original record, whose status is still Granted.
        /// </summary>
        public ClaimStatus Status { get; }

        public ClaimFailure Failure { get; }

        /// <summary>The saved record. Survives restarts and can be read with <see cref="Vault.GetClaim"/>.</summary>
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
