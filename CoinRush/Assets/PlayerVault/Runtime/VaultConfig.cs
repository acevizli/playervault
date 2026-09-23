using System;
using System.Collections.Generic;

namespace PlayerVault
{
    /// <summary>What to do when the stored document cannot be parsed.</summary>
    public enum CorruptDataPolicy
    {
        /// <summary>
        /// Move the unreadable file aside and start with a new save. This is the default:
        /// losing progress is better than a game that cannot start, and the moved file can
        /// still be recovered.
        /// </summary>
        Quarantine,

        /// <summary>Throw when opening and let the game decide.</summary>
        Throw
    }

    /// <summary>What to do when the save fails its tamper check.</summary>
    public enum TamperedDataPolicy
    {
        /// <summary>
        /// Refuse to open, leaving the save in place, so the game can block the player. Opening
        /// throws <see cref="VaultTamperedException"/> every time until the save is deleted. This
        /// is the default.
        /// </summary>
        Block,

        /// <summary>
        /// Move the save aside, log an error and start with a new save, as for an unreadable one.
        /// The player loses their progress, including whatever the edit gave them.
        /// </summary>
        Quarantine
    }

    /// <summary>When balance changes reach disk.</summary>
    public enum FlushMode
    {
        /// <summary>Write on every change. This is the default.</summary>
        Immediate,

        /// <summary>Coalesce writes within <see cref="VaultConfig.DebounceInterval"/>.</summary>
        Debounced
    }

    /// <summary>
    /// Vault settings. Every dependency has a default, so the minimum config is a player id
    /// and a list of resources.
    /// </summary>
    public sealed class VaultConfig
    {
        /// <summary>Required. Also used as the save file key, so each player has a separate save.</summary>
        public string PlayerId { get; set; }

        public string ApiUrl { get; set; } = "https://httpbin.org/anything";

        public IList<ResourceDefinition> Resources { get; set; } = new List<ResourceDefinition>();

        /// <summary>
        /// When false (the default), resources not listed in <see cref="Resources"/> are
        /// refused, which catches typos. When true, unknown resources are created on first use
        /// with no maximum.
        /// </summary>
        public bool AllowUndeclaredResources { get; set; }

        /// <summary>Retry unfinished claims when the vault opens. Runs in the background; opening does not wait for the network.</summary>
        public bool ResumePendingOnOpen { get; set; } = true;

        public CorruptDataPolicy OnCorruptData { get; set; } = CorruptDataPolicy.Quarantine;

        /// <summary>
        /// Sign every save and check the signature on open, so a save edited outside the game, or
        /// an older copy put back, is detected. On by default. See <see cref="IVaultKeyStore"/>.
        /// </summary>
        /// <remarks>
        /// This detects edits made through a file manager, adb or a backup tool, and saves copied
        /// from another device. It does not stop a player on a rooted or jailbroken device, who can
        /// make the game itself sign anything. Only a server that owns the balances can.
        /// </remarks>
        public bool DetectTampering { get; set; } = true;

        public TamperedDataPolicy OnTampered { get; set; } = TamperedDataPolicy.Block;

        public FlushMode FlushMode { get; set; } = FlushMode.Immediate;

        public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Defaults to <see cref="UnityWebRequestTransport"/>.</summary>
        public IVaultTransport Transport { get; set; }

        /// <summary>Defaults to <see cref="JsonFileStorage"/> under Application.persistentDataPath.</summary>
        public IVaultStorage Storage { get; set; }

        /// <summary>
        /// Where the save signing key and counter are kept. Defaults to the iOS Keychain, the
        /// Android Keystore, and <see cref="FileKeyStore"/> everywhere else. Only used when
        /// <see cref="DetectTampering"/> is on.
        /// </summary>
        public IVaultKeyStore KeyStore { get; set; }

        /// <summary>Defaults to <see cref="SystemClock"/>.</summary>
        public IVaultClock Clock { get; set; }

        /// <summary>Defaults to <see cref="UnityLogger"/>.</summary>
        public IVaultLogger Logger { get; set; }

        public RetryPolicy Retry { get; set; } = new RetryPolicy();

        /// <summary>
        /// Validates the configuration. Called by the Vault constructor.
        /// </summary>
        /// <remarks>
        /// Configuration errors throw. Runtime failures such as an insufficient balance are
        /// returned as results instead.
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
