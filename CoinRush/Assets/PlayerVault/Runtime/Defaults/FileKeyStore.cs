using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault
{
    /// <summary>
    /// Keeps each player's signing key and save counter in a plain file under
    /// <c>Application.persistentDataPath/playervault/keys/</c>. The default in the Editor and in
    /// Standalone builds, which have no key store the SDK uses.
    /// </summary>
    /// <remarks>
    /// This protects nothing: anyone who can edit the save can read the key next to it and sign
    /// their edit. It exists so the Editor runs exactly the checks a device runs. Editing a save
    /// by hand in the Editor is detected, because a text editor does not re-sign it.
    /// <para>
    /// Logs a warning once in a built player, where it gives the impression of protection
    /// without providing any.
    /// </para>
    /// </remarks>
    public sealed class FileKeyStore : IVaultKeyStore
    {
        const int KeyLength = 32;

#if !UNITY_EDITOR
        static int _warned;
#endif

        readonly string _root;
        readonly object _sync = new object();

        [Serializable]
        class KeyFile
        {
            public string key;
            public long counter;
        }

        /// <param name="rootDirectory">
        /// Defaults to <c>Application.persistentDataPath/playervault/keys</c>. Construct on Unity's
        /// main thread, because persistentDataPath cannot be read from other threads.
        /// </param>
        public FileKeyStore(string rootDirectory = null)
        {
            _root = rootDirectory ?? Path.Combine(Application.persistentDataPath, "playervault", "keys");

#if !UNITY_EDITOR
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                Debug.LogWarning(
                    "[PlayerVault] Save signing keys are kept in a plain file on this platform, so edited " +
                    "saves are only detected until someone reads the key. Only iOS and Android keep it secret.");
            }
#endif
        }

        public Task<byte[]> GetOrCreateKeyAsync(string playerId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var file = Read(playerId);
                if (file.key.Length > 0) return Task.FromResult(Convert.FromBase64String(file.key));

                var key = new byte[KeyLength];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);

                file.key = Convert.ToBase64String(key);
                Write(playerId, file);
                return Task.FromResult(key);
            }
        }

        public Task<long> ReadCounterAsync(string playerId, CancellationToken cancellationToken = default)
        {
            lock (_sync) return Task.FromResult(Read(playerId).counter);
        }

        public Task WriteCounterAsync(string playerId, long counter, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var file = Read(playerId);
                if (counter <= file.counter) return Task.CompletedTask;

                file.counter = counter;
                Write(playerId, file);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string playerId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var path = PathFor(playerId);
                if (File.Exists(path)) File.Delete(path);
            }

            return Task.CompletedTask;
        }

        string PathFor(string playerId) => Path.Combine(_root, JsonFileStorage.FileNameFor(playerId) + ".key");

        KeyFile Read(string playerId)
        {
            var path = PathFor(playerId);
            var file = File.Exists(path) ? JsonUtility.FromJson<KeyFile>(File.ReadAllText(path, Encoding.UTF8)) : null;

            file ??= new KeyFile();
            file.key ??= string.Empty;
            return file;
        }

        /// <summary>Written to a temporary file and renamed, like the save, so a crash cannot lose the key.</summary>
        void Write(string playerId, KeyFile file)
        {
            Directory.CreateDirectory(_root);

            var path = PathFor(playerId);
            var temporary = path + ".tmp";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(file));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
