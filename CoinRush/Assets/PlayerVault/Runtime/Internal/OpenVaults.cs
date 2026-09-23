using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault.Internal
{
    /// <summary>
    /// Tracks which saves have an open vault in this process, so a save is only ever written by
    /// one vault at a time.
    /// </summary>
    /// <remarks>
    /// Two vaults on one save each write their own snapshot over the other's, so whichever
    /// writes last silently undoes the other's changes. A second open of a save that is in use
    /// therefore throws. A save whose vault is closing is different: the open waits until the
    /// closing vault's last write has finished, then goes ahead. That is the scene-change case,
    /// where the old scene's vault is still writing while the new scene opens its own.
    /// </remarks>
    internal static class OpenVaults
    {
        sealed class Entry
        {
            public object Owner;

            /// <summary>Null while the save is in use. Completes once the owner lets go.</summary>
            public Task Released;
        }

        static readonly object Sync = new object();
        static readonly Dictionary<object, Entry> Entries = new Dictionary<object, Entry>();

        /// <summary>
        /// Identifies a save. Two <see cref="JsonFileStorage"/> instances are separate objects
        /// but write the same file, so they are compared by path. Any other storage is compared
        /// by instance.
        /// </summary>
        public static object KeyFor(IVaultStorage storage, string playerId)
        {
            if (storage is JsonFileStorage files)
                return "file:" + Path.GetFullPath(files.PathFor(playerId));

            return (storage, playerId);
        }

        /// <summary>
        /// Takes the save for <paramref name="owner"/>. Waits while another owner is releasing
        /// it, and throws with <paramref name="inUse"/> if another owner holds it.
        /// </summary>
        public static async Task AcquireAsync(object key, object owner, string inUse)
        {
            while (true)
            {
                Task releasing;

                lock (Sync)
                {
                    if (!Entries.TryGetValue(key, out var entry))
                    {
                        Entries[key] = new Entry { Owner = owner };
                        return;
                    }

                    if (ReferenceEquals(entry.Owner, owner)) return;
                    if (entry.Released == null) throw new InvalidOperationException(inUse);

                    releasing = entry.Released;
                }

                // Whatever the release ended with, the entry is gone once it completes.
                try { await releasing.ConfigureAwait(false); }
                catch (Exception) { /* only the timing matters here */ }
            }
        }

        /// <summary>
        /// Marks the save as being released. Later acquires wait for <paramref name="released"/>
        /// instead of throwing.
        /// </summary>
        public static void BeginRelease(object key, object owner, Task released)
        {
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Owner, owner) && entry.Released == null)
                    entry.Released = released;
            }
        }

        /// <summary>Frees the save. Call before completing the task passed to <see cref="BeginRelease"/>.</summary>
        public static void Release(object key, object owner)
        {
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Owner, owner))
                    Entries.Remove(key);
            }
        }

        /// <summary>
        /// Clears the table when play mode starts. With domain reload turned off, static state
        /// survives between play sessions, and a vault the last session never closed would block
        /// the next one from opening.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnPlay()
        {
            lock (Sync) Entries.Clear();
        }
    }
}
