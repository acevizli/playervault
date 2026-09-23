using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerVault.Tests
{
    /// <summary>
    /// A transport that never uses the network. Records every request body so tests can
    /// check the format, and counts calls so tests can check that no request was sent.
    /// </summary>
    internal sealed class FakeTransport : IVaultTransport
    {
        readonly Func<int, TransportResponse> _responder;
        readonly object _sync = new object();

        readonly List<string> _bodies = new List<string>();

        /// <summary>A copy, because a test may read this while a claim is still running.</summary>
        public List<string> Bodies
        {
            get { lock (_sync) return new List<string>(_bodies); }
        }

        public int CallCount
        {
            get { lock (_sync) return _bodies.Count; }
        }

        /// <summary>Optional gate that lets a test hold requests open to test concurrency.</summary>
        public Task Gate;

        /// <summary>
        /// Resumes on the thread pool even when nothing would otherwise suspend, to behave like
        /// real I/O.
        /// </summary>
        public bool CompleteAsynchronously;

        /// <summary>Raised on every call, before the response is created. Used by reentrancy tests.</summary>
        public Action<int> OnRequest;

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
            int attempt;
            lock (_sync)
            {
                _bodies.Add(jsonBody);
                attempt = _bodies.Count;
            }

            OnRequest?.Invoke(attempt);

            if (CompleteAsynchronously) await Task.Yield();
            if (Gate != null) await Gate.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return _responder(attempt);
        }
    }

    /// <summary>
    /// Storage in a dictionary. A restart is simulated by creating a second Vault on the same
    /// instance.
    /// </summary>
    internal sealed class InMemoryStorage : IVaultStorage
    {
        readonly object _sync = new object();
        readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.Ordinal);

        public readonly List<string> Quarantined = new List<string>();

        public int Writes;

        /// <summary>Every write throws while this is set.</summary>
        public bool FailWrites;

        /// <summary>Every read throws while this is set, like a locked or damaged file.</summary>
        public bool FailReads;

        /// <summary>
        /// Completes reads and writes on the thread pool instead of inline, to check that the
        /// SDK's ordering holds when work resumes on another thread.
        /// </summary>
        public bool CompleteAsynchronously;

        /// <summary>Raised inside a write, before it completes. Used by interleaving tests.</summary>
        public Action<string> OnWrite;

        public Dictionary<string, string> Files
        {
            get { lock (_sync) return new Dictionary<string, string>(_files, StringComparer.Ordinal); }
        }

        public bool Has(string key)
        {
            lock (_sync) return _files.ContainsKey(key);
        }

        public string Read(string key)
        {
            lock (_sync) return _files.TryGetValue(key, out var payload) ? payload : null;
        }

        public void Seed(string key, string payload)
        {
            lock (_sync) _files[key] = payload;
        }

        public async Task<string> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            if (CompleteAsynchronously) await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (FailReads) throw new SimulatedIOException("the save file could not be read");

            lock (_sync) return _files.TryGetValue(key, out var payload) ? payload : null;
        }

        public async Task WriteAsync(string key, string payload, CancellationToken cancellationToken = default)
        {
            if (CompleteAsynchronously) await Task.Yield();

            OnWrite?.Invoke(payload);

            if (FailWrites) throw new SimulatedIOException("the save file could not be written");

            lock (_sync)
            {
                Writes++;
                _files[key] = payload;
            }
        }

        public Task QuarantineAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_files.TryGetValue(key, out var payload))
                {
                    Quarantined.Add(payload);
                    _files.Remove(key);
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (_sync) _files.Remove(key);
            return Task.CompletedTask;
        }

        internal sealed class SimulatedIOException : Exception
        {
            public SimulatedIOException(string message) : base(message) { }
        }
    }

    /// <summary>A key store in memory, standing in for the Keychain or Keystore.</summary>
    internal sealed class InMemoryKeyStore : IVaultKeyStore
    {
        readonly object _sync = new object();
        readonly Dictionary<string, byte[]> _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        readonly Dictionary<string, long> _counters = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Every call throws while this is set, like a locked Keychain.</summary>
        public bool Fail;

        /// <summary>Counter writes are dropped while this is set, like the app stopping right after a save.</summary>
        public bool DropCounterWrites;

        public long Counter(string playerId)
        {
            lock (_sync) return _counters.TryGetValue(playerId, out var counter) ? counter : 0;
        }

        public bool HasKey(string playerId)
        {
            lock (_sync) return _keys.ContainsKey(playerId);
        }

        public Task<byte[]> GetOrCreateKeyAsync(string playerId, CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            lock (_sync)
            {
                if (!_keys.TryGetValue(playerId, out var key))
                {
                    key = new byte[32];
                    new System.Random().NextBytes(key);
                    _keys[playerId] = key;
                }

                return Task.FromResult((byte[])key.Clone());
            }
        }

        public Task<long> ReadCounterAsync(string playerId, CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            return Task.FromResult(Counter(playerId));
        }

        public Task WriteCounterAsync(string playerId, long counter, CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            lock (_sync)
            {
                if (!DropCounterWrites && counter > Counter(playerId)) _counters[playerId] = counter;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string playerId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _keys.Remove(playerId);
                _counters.Remove(playerId);
            }

            return Task.CompletedTask;
        }

        void ThrowIfFailing()
        {
            if (Fail) throw new InvalidOperationException("the key store is unavailable");
        }
    }

    /// <summary>
    /// A clock that does not wait, so retry delays cost no time in tests.
    /// </summary>
    internal sealed class FakeClock : IVaultClock
    {
        readonly object _sync = new object();
        DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow
        {
            get { lock (_sync) return _now; }
            set { lock (_sync) _now = value; }
        }

        public readonly List<TimeSpan> Delays = new List<TimeSpan>();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Delays.Add(delay);
                _now = _now.Add(delay);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    internal sealed class NullLogger : IVaultLogger
    {
        readonly object _sync = new object();
        readonly List<string> _errors = new List<string>();

        public List<string> Errors
        {
            get { lock (_sync) return new List<string>(_errors); }
        }

        readonly List<string> _warnings = new List<string>();

        public List<string> Warnings
        {
            get { lock (_sync) return new List<string>(_warnings); }
        }

        public void Info(string message) { }

        public void Warn(string message)
        {
            lock (_sync) _warnings.Add(message);
        }

        public void Error(string message, Exception exception = null)
        {
            lock (_sync) _errors.Add(message);
        }
    }
}
