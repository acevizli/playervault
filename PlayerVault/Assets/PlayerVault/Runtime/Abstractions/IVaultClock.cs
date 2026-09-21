using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerVault
{
    /// <summary>
    /// Time and delay, injected so retry backoff is testable without real waiting.
    /// </summary>
    public interface IVaultClock
    {
        DateTimeOffset UtcNow { get; }
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Where the SDK writes diagnostics. Replace to route into a game's own logging.
    /// </summary>
    public interface IVaultLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception exception = null);
    }
}
