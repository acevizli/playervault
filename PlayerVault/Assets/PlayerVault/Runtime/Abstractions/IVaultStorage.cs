using System.Threading;
using System.Threading.Tasks;

namespace PlayerVault
{
    /// <summary>
    /// Durable key/value storage for the vault document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WriteAsync must be atomic.</b> This is not a stylistic preference — it is the
    /// single guarantee that keeps a reward from being granted twice.
    /// </para>
    /// <para>
    /// The SDK commits a balance change, a claim record and the granted-reward guard
    /// together, in one document, in one write. If a substituted implementation can
    /// leave a partially written document behind after a crash or a kill, that guarantee
    /// is gone and the SDK will double-grant. <see cref="JsonFileStorage"/> achieves it
    /// by writing to a temporary file, fsyncing, then renaming over the target.
    /// </para>
    /// <para>
    /// This is why the SDK does not use PlayerPrefs: it writes key by key and offers no
    /// way to commit several values together.
    /// </para>
    /// </remarks>
    public interface IVaultStorage
    {
        Task<string> ReadAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>Replaces the stored payload. Must be all-or-nothing.</summary>
        Task WriteAsync(string key, string payload, CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves an unreadable payload aside so it is not read again, preserving it for
        /// support recovery. Called when parsing fails and the policy is Quarantine.
        /// </summary>
        Task QuarantineAsync(string key, CancellationToken cancellationToken = default);
    }
}
