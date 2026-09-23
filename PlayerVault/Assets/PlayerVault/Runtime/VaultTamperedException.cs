using System;

namespace PlayerVault
{
    /// <summary>Why a save failed its tamper check.</summary>
    public enum TamperReason
    {
        /// <summary>The save has no signature, for example because it was written by hand.</summary>
        Unsigned,

        /// <summary>The save was changed after it was written.</summary>
        SignatureMismatch,

        /// <summary>The save is correctly signed but older than the last one written: a copy was put back.</summary>
        RolledBack
    }

    /// <summary>
    /// Thrown by <see cref="Vault.OpenAsync"/> when the save failed its tamper check and
    /// <see cref="VaultConfig.OnTampered"/> is <see cref="TamperedDataPolicy.Block"/>.
    /// </summary>
    /// <remarks>
    /// The save is left in place, so every later open fails the same way. Unlike a
    /// <see cref="VaultStorageException"/>, retrying does not help. The save can only be cleared
    /// with <see cref="Vault.DeleteSaveAsync"/>.
    /// </remarks>
    public sealed class VaultTamperedException : Exception
    {
        public string PlayerId { get; }
        public TamperReason Reason { get; }

        public VaultTamperedException(string playerId, TamperReason reason)
            : base(Describe(playerId, reason))
        {
            PlayerId = playerId;
            Reason = reason;
        }

        static string Describe(string playerId, TamperReason reason)
        {
            switch (reason)
            {
                case TamperReason.Unsigned:
                    return $"The save for '{playerId}' has no signature, so it was not written by PlayerVault on this device.";
                case TamperReason.RolledBack:
                    return $"The save for '{playerId}' is an older copy put back in place of the current one.";
                default:
                    return $"The save for '{playerId}' was changed after PlayerVault wrote it.";
            }
        }
    }
}
