using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault
{
    /// <summary>
    /// The default storage: one JSON file per player under
    /// <c>Application.persistentDataPath/playervault/</c>, written atomically.
    /// </summary>
    /// <remarks>
    /// Each write goes to a temporary file, is flushed to disk with fsync, and is then renamed
    /// over the real file. Without the fsync, the rename can reach disk before the data does,
    /// and a power loss would leave an empty file.
    /// </remarks>
    public sealed class JsonFileStorage : IVaultStorage
    {
        /// <summary>Maximum number of key characters kept in the readable part of the file name.</summary>
        const int ReadablePrefixLength = 48;

        readonly string _root;

        /// <param name="rootDirectory">
        /// Defaults to <c>Application.persistentDataPath/playervault</c>. Construct on Unity's
        /// main thread, because persistentDataPath cannot be read from other threads. The path
        /// is cached so file I/O can run on the thread pool.
        /// </param>
        public JsonFileStorage(string rootDirectory = null)
        {
            _root = rootDirectory ?? Path.Combine(Application.persistentDataPath, "playervault");
        }

        /// <summary>The file path for a key. Public so a game can find, back up or delete a save.</summary>
        public string PathFor(string key) => Path.Combine(_root, FileNameFor(key) + ".json");

        public async Task<string> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        public async Task WriteAsync(string key, string payload, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(_root);

            var path = PathFor(key);
            var temporary = path + ".tmp";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(payload).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public Task QuarantineAsync(string key, CancellationToken cancellationToken = default)
        {
            var path = PathFor(key);
            if (File.Exists(path))
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(path, $"{path}.corrupt-{stamp}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Turns a key into a file name that is readable and unique.
        /// </summary>
        /// <remarks>
        /// Replacing unsafe characters with underscores alone would map <c>a/b</c> and
        /// <c>a_b</c> to the same file, so two players would share a save. A hash of the full
        /// key is appended to keep names unique; the readable prefix is only there to make the
        /// files easy to tell apart. <see cref="Vault"/> also checks the player id stored in
        /// the file when loading.
        /// </remarks>
        public static string FileNameFor(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "player";

            var builder = new StringBuilder(ReadablePrefixLength);
            foreach (var character in key)
            {
                if (builder.Length == ReadablePrefixLength) break;
                builder.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');
            }

            return builder.Append('-').Append(Digest(key)).ToString();
        }

        static string Digest(string key)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));

            var builder = new StringBuilder(16);
            for (var i = 0; i < 8; i++) builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
