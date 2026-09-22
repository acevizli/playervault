using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault
{
    public sealed class SystemClock : IVaultClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            Task.Delay(delay, cancellationToken);
    }

    public sealed class UnityLogger : IVaultLogger
    {
        public void Info(string message) => Debug.Log(message);
        public void Warn(string message) => Debug.LogWarning(message);

        public void Error(string message, Exception exception = null)
        {
            Debug.LogError(message);
            if (exception != null) Debug.LogException(exception);
        }
    }
}
