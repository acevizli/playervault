using System;
using System.Globalization;
using System.IO;
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
    /// Atomicity comes from write-to-temp, fsync, then rename. The fsync is not optional:
    /// without it the rename can reach disk while the bytes it points at have not, and a
    /// power loss leaves a valid-looking file full of nothing.
    /// </remarks>
    public sealed class JsonFileStorage : IVaultStorage
    {
        readonly string _root;

        /// <param name="rootDirectory">
        /// Defaults to <c>Application.persistentDataPath/playervault</c>. Must be constructed
        /// on Unity's main thread, since persistentDataPath is unavailable off it — the value
        /// is cached here so the actual I/O can run on the thread pool.
        /// </param>
        public JsonFileStorage(string rootDirectory = null)
        {
            _root = rootDirectory ?? Path.Combine(Application.persistentDataPath, "playervault");
        }

        public string PathFor(string key) => Path.Combine(_root, Sanitize(key) + ".json");

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

        static string Sanitize(string key)
        {
            if (string.IsNullOrEmpty(key)) return "player";

            var builder = new StringBuilder(key.Length);
            foreach (var character in key)
                builder.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');

            return builder.ToString();
        }
    }
}
