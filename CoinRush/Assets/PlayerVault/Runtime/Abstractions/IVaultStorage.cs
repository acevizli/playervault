using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerVault
{
    /// <summary>
    /// Durable key/value storage for the vault document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WriteAsync must be atomic.</b> The SDK saves the balance, the claim record and the
    /// granted-reward set together in one write. If an implementation can leave a partly
    /// written file after a crash, rewards can be granted twice.
    /// <see cref="JsonFileStorage"/> writes to a temporary file, fsyncs it, then renames it
    /// over the real file.
    /// </para>
    /// <para>
    /// This is also why the SDK does not use PlayerPrefs, which saves keys one at a time.
    /// </para>
    /// </remarks>
    public interface IVaultStorage
    {
        Task<string> ReadAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>Replaces the stored payload. Must either fully succeed or change nothing.</summary>
        Task WriteAsync(string key, string payload, CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves an unreadable payload aside so it is not read again but can still be
        /// recovered. Called when parsing fails and the policy is Quarantine.
        /// </summary>
        Task QuarantineAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes the stored payload and anything quarantined for the key. Deleting a key that
        /// has nothing stored is not an error. Used by <see cref="Vault.DeleteSaveAsync"/>.
        /// </summary>
        /// <remarks>
        /// Optional: the default throws <see cref="NotSupportedException"/>, so a storage written
        /// before this method existed still compiles and only resets are unavailable.
        /// </remarks>
        Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException(
                $"{GetType().Name} does not support deleting a save. Implement IVaultStorage.DeleteAsync."));
    }
}
