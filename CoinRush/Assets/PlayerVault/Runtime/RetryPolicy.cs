using System;

namespace PlayerVault
{
    /// <summary>
    /// How many times, and how often, a claim is retried before it is left for the next launch.
    /// </summary>
    public sealed class RetryPolicy
    {
        /// <summary>Total attempts per session, including the first. Minimum 1.</summary>
        public int MaxAttempts { get; set; } = 3;

        /// <summary>Delay before the second attempt; doubles thereafter.</summary>
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Upper bound on any single backoff wait.</summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Random variation applied to each delay, as a fraction. 0.25 means +/-25%. This
        /// stops many clients from retrying at the same moment after an outage.
        /// </summary>
        public double Jitter { get; set; } = 0.25;

        /// <summary>
        /// Per-attempt request timeout.
        /// </summary>
        /// <remarks>
        /// Set high on purpose. Measured latency against the sample endpoint was 0.6s to 6.0s.
        /// A timed-out request has an unknown outcome, so a short timeout creates more claims
        /// whose result is unknown.
        /// </remarks>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// A copy. The vault takes one at construction, so later edits to the game's policy
        /// do not affect requests already in flight.
        /// </summary>
        internal RetryPolicy Clone() => new RetryPolicy
        {
            MaxAttempts = MaxAttempts,
            BaseDelay = BaseDelay,
            MaxDelay = MaxDelay,
            Jitter = Jitter,
            Timeout = Timeout
        };

        internal TimeSpan DelayForAttempt(int attempt, Random random)
        {
            var exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
            exponential = Math.Min(exponential, MaxDelay.TotalMilliseconds);

            var spread = exponential * Jitter;
            var offset = (random.NextDouble() * 2d - 1d) * spread;

            return TimeSpan.FromMilliseconds(Math.Max(0d, exponential + offset));
        }
    }
}
