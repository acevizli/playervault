using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerVault.Tests
{
    /// <summary>
    /// A transport that never touches the network. Records every body it was handed so a
    /// test can assert on the wire format, and counts calls so a test can prove a request
    /// was NOT sent — which is how idempotency is actually verified.
    /// </summary>
    internal sealed class FakeTransport : IVaultTransport
    {
        readonly Func<int, TransportResponse> _responder;

        public readonly List<string> Bodies = new List<string>();
        public int CallCount => Bodies.Count;

        /// <summary>Optional gate, so a test can hold requests open and exercise concurrency.</summary>
        public Task Gate;

        public FakeTransport(Func<int, TransportResponse> responder) => _responder = responder;

        public static FakeTransport Always(TransportResponse response) =>
            new FakeTransport(_ => response);

        public static FakeTransport Ok(string body = "{\"json\":{}}") =>
            Always(TransportResponse.Success(200, body));

        /// <summary>Fails for the first <paramref name="failures"/> attempts, then succeeds.</summary>
        public static FakeTransport FailsThenSucceeds(int failures, TransportResponse failure) =>
            new FakeTransport(attempt => attempt <= failures ? failure : TransportResponse.Success(200, "{\"json\":{}}"));

        public async Task<TransportResponse> PostAsync(string url, string jsonBody, CancellationToken cancellationToken)
        {
            Bodies.Add(jsonBody);
            if (Gate != null) await Gate.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return _responder(Bodies.Count);
        }
    }

    /// <summary>
    /// Storage in a dictionary. Surviving a restart is modelled by constructing a second
    /// Vault over the same instance — which is exactly what the real thing does with a file.
    /// </summary>
    internal sealed class InMemoryStorage : IVaultStorage
    {
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>();
        public readonly List<string> Quarantined = new List<string>();

        public int Writes;
        public bool FailWrites;

        public Task<string> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files.TryGetValue(key, out var payload) ? payload : null);

        public Task WriteAsync(string key, string payload, CancellationToken cancellationToken = default)
        {
            if (FailWrites) throw new IOException_Simulated();

            Writes++;
            Files[key] = payload;
            return Task.CompletedTask;
        }

        public Task QuarantineAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Files.TryGetValue(key, out var payload))
            {
                Quarantined.Add(payload);
                Files.Remove(key);
            }

            return Task.CompletedTask;
        }

        internal sealed class IOException_Simulated : Exception { }
    }

    /// <summary>
    /// Time that never actually passes, so retry backoff is exercised without waiting for it.
    /// </summary>
    internal sealed class FakeClock : IVaultClock
    {
        public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public readonly List<TimeSpan> Delays = new List<TimeSpan>();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Delays.Add(delay);
            UtcNow = UtcNow.Add(delay);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    internal sealed class NullLogger : IVaultLogger
    {
        public readonly List<string> Errors = new List<string>();

        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception exception = null) => Errors.Add(message);
    }
}
