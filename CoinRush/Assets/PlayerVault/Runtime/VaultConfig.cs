using System;
using System.Collections.Generic;

namespace PlayerVault
{
    /// <summary>What to do when the stored document cannot be parsed.</summary>
    public enum CorruptDataPolicy
    {
        /// <summary>
        /// Move the unreadable file aside and start fresh. The default: a player who
        /// cannot launch the game is worse off than one who lost progress, and the
        /// quarantined file keeps support recovery possible.
        /// </summary>
        Quarantine,

        /// <summary>Fail loudly at open time and let the game decide.</summary>
        Throw
    }

    /// <summary>When balance changes reach disk.</summary>
    public enum FlushMode
    {
        /// <summary>Write on every mutation. Correct by default.</summary>
        Immediate,

        /// <summary>Coalesce writes within <see cref="VaultConfig.DebounceInterval"/>.</summary>
        Debounced
    }

    /// <summary>
    /// Everything the vault needs. Every seam has a working default, so the minimum
    /// viable config is a player id and a list of resources.
    /// </summary>
    public sealed class VaultConfig
    {
        /// <summary>Required. Also keys the storage file, so switching player switches ledger.</summary>
        public string PlayerId { get; set; }

        public string ApiUrl { get; set; } = "https://httpbin.org/anything";

        public IList<ResourceDefinition> Resources { get; set; } = new List<ResourceDefinition>();

        /// <summary>
        /// When false (the default) a resource not declared in <see cref="Resources"/> is
        /// refused, which catches typos. When true, unknown resources spring into existence
        /// on first use with no maximum.
        /// </summary>
        public bool AllowUndeclaredResources { get; set; }

        /// <summary>Replay unfinished claims when the vault opens. Runs detached; opening never blocks on the network.</summary>
        public bool ResumePendingOnOpen { get; set; } = true;

        public CorruptDataPolicy OnCorruptData { get; set; } = CorruptDataPolicy.Quarantine;

        public FlushMode FlushMode { get; set; } = FlushMode.Immediate;

        public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Defaults to <see cref="UnityWebRequestTransport"/>.</summary>
        public IVaultTransport Transport { get; set; }

        /// <summary>Defaults to <see cref="JsonFileStorage"/> under Application.persistentDataPath.</summary>
        public IVaultStorage Storage { get; set; }

        /// <summary>Defaults to <see cref="SystemClock"/>.</summary>
        public IVaultClock Clock { get; set; }

        /// <summary>Defaults to <see cref="UnityLogger"/>.</summary>
        public IVaultLogger Logger { get; set; }

        public RetryPolicy Retry { get; set; } = new RetryPolicy();

        /// <summary>
        /// Validates the configuration. Called by the Vault constructor.
        /// </summary>
        /// <remarks>
        /// The SDK throws at configuration time and returns results at runtime. A bad
        /// config is a programming error and should fail loudly on the first frame;
        /// an insufficient balance is a normal event and should never throw.
        /// </remarks>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(PlayerId))
                throw new ArgumentException("VaultConfig.PlayerId is required.", nameof(PlayerId));

            if (string.IsNullOrWhiteSpace(ApiUrl))
                throw new ArgumentException("VaultConfig.ApiUrl is required.", nameof(ApiUrl));

            if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out _))
                throw new ArgumentException($"VaultConfig.ApiUrl is not a valid absolute URL: '{ApiUrl}'.", nameof(ApiUrl));

            if (Resources == null)
                throw new ArgumentException("VaultConfig.Resources must not be null.", nameof(Resources));

            if (Resources.Count == 0 && !AllowUndeclaredResources)
                throw new ArgumentException(
                    "VaultConfig declares no resources and AllowUndeclaredResources is false, so the vault could never do anything.",
                    nameof(Resources));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in Resources)
            {
                if (resource == null)
                    throw new ArgumentException("VaultConfig.Resources contains a null entry.", nameof(Resources));

                if (string.IsNullOrWhiteSpace(resource.Key))
                    throw new ArgumentException("A ResourceDefinition has an empty Key.", nameof(Resources));

                if (!seen.Add(resource.Key))
                    throw new ArgumentException($"Duplicate resource key '{resource.Key}'.", nameof(Resources));

                if (resource.Initial < 0)
                    throw new ArgumentException($"Resource '{resource.Key}' has a negative Initial.", nameof(Resources));

                if (resource.Max.HasValue && resource.Max.Value < 0)
                    throw new ArgumentException($"Resource '{resource.Key}' has a negative Max.", nameof(Resources));

                if (resource.Max.HasValue && resource.Initial > resource.Max.Value)
                    throw new ArgumentException(
                        $"Resource '{resource.Key}' has Initial {resource.Initial} above Max {resource.Max.Value}.",
                        nameof(Resources));
            }

            if (Retry == null)
                throw new ArgumentException("VaultConfig.Retry must not be null.", nameof(Retry));

            if (Retry.MaxAttempts < 1)
                throw new ArgumentException("RetryPolicy.MaxAttempts must be at least 1.", nameof(Retry));

            if (Retry.Timeout <= TimeSpan.Zero)
                throw new ArgumentException("RetryPolicy.Timeout must be positive.", nameof(Retry));

            if (Retry.Jitter < 0d || Retry.Jitter > 1d)
                throw new ArgumentException("RetryPolicy.Jitter must be between 0 and 1.", nameof(Retry));
        }
    }
}
