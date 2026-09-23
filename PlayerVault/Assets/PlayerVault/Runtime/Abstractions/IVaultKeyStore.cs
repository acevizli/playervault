using System.Threading;
using System.Threading.Tasks;

namespace PlayerVault
{
    /// <summary>
    /// Holds what the vault needs to tell whether a save was edited or put back: a secret
    /// signing key and the number of the last save written, per player.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The save file itself is where a player can reach it, so neither of these may be kept in
    /// it. The defaults use the iOS Keychain and the Android Keystore, which a player cannot read
    /// or change on a device that is not rooted or jailbroken. The Editor and Standalone builds
    /// use <see cref="FileKeyStore"/>, which does not protect anything but runs the same checks.
    /// </para>
    /// <para>
    /// The signing and the checks are not part of this interface and cannot be replaced. An
    /// implementation only stores the two values.
    /// </para>
    /// </remarks>
    public interface IVaultKeyStore
    {
        /// <summary>
        /// The player's signing key, at least 32 random bytes. Created and stored on first use;
        /// every later call must return the same bytes.
        /// </summary>
        Task<byte[]> GetOrCreateKeyAsync(string playerId, CancellationToken cancellationToken = default);

        /// <summary>The highest save number recorded for the player, or 0 if none has been.</summary>
        Task<long> ReadCounterAsync(string playerId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Records the number of the save just written. <b>Must never lower the stored value</b>:
        /// ignore a number below it. A lower value would let an older copy of the save be put back.
        /// </summary>
        Task WriteCounterAsync(string playerId, long counter, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes the player's key and counter. Used by <see cref="Vault.DeleteSaveAsync"/>.
        /// Deleting a player that has nothing stored is not an error.
        /// </summary>
        Task DeleteAsync(string playerId, CancellationToken cancellationToken = default);
    }
}
