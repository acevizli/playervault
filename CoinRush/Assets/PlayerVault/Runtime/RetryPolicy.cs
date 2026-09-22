using System;

namespace PlayerVault
{
    /// <summary>
    /// How hard the SDK tries before leaving a claim for the next launch.
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
        /// Random spread applied to each delay, as a fraction. 0.25 means ±25%.
        /// Stops a crowd of clients retrying in lockstep after an outage.
        /// </summary>
        public double Jitter { get; set; } = 0.25;

        /// <summary>
        /// Per-attempt request timeout.
        /// </summary>
        /// <remarks>
        /// Deliberately generous. Measured latency against the sample endpoint ranged from
        /// 0.6s to 6.0s, and every timeout produces an <i>indeterminate</i> outcome rather
        /// than a clean failure — so an aggressive timeout manufactures exactly the ambiguity
        /// the SDK works hardest to avoid.
        /// </remarks>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

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
