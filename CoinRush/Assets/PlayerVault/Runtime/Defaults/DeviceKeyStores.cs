using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault
{
    /// <summary>Picks the key store for the platform the game is running on.</summary>
    internal static class DefaultKeyStore
    {
        /// <summary>Construct on Unity's main thread; the Android and fallback stores capture main-thread state.</summary>
        public static IVaultKeyStore Create()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new KeychainKeyStore();
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidKeystoreKeyStore();
#else
            return new FileKeyStore();
#endif
        }
    }

#if UNITY_IOS && !UNITY_EDITOR
    /// <summary>
    /// Keeps the key and counter as Keychain items that never leave the device
    /// (<c>kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly</c>). The native half is
    /// <c>Plugins/iOS/PlayerVaultKeychain.mm</c>.
    /// </summary>
    /// <remarks>The Keychain is safe to call from any thread, so nothing is marshalled.</remarks>
    internal sealed class KeychainKeyStore : IVaultKeyStore
    {
        const int KeyLength = 32;

        [DllImport("__Internal")] static extern int PlayerVault_KeychainRead(string account, byte[] buffer, int capacity);
        [DllImport("__Internal")] static extern int PlayerVault_KeychainWrite(string account, byte[] data, int length);
        [DllImport("__Internal")] static extern int PlayerVault_KeychainDelete(string account);

        readonly object _sync = new object();

        public Task<byte[]> GetOrCreateKeyAsync(string playerId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var account = "key:" + playerId;
                var key = new byte[KeyLength];

                var read = PlayerVault_KeychainRead(account, key, key.Length);
                if (read == KeyLength) return Task.FromResult(key);
                if (read < 0) throw new InvalidOperationException($"Reading the signing key from the Keychain failed (OSStatus {read}).");

                using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
                Check(PlayerVault_KeychainWrite(account, key, key.Length), "storing the signing key");
                return Task.FromResult(key);
            }
        }

        public Task<long> ReadCounterAsync(string playerId, CancellationToken cancellationToken = default)
        {
            lock (_sync) return Task.FromResult(ReadCounter(playerId));
        }

        public Task WriteCounterAsync(string playerId, long counter, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (counter <= ReadCounter(playerId)) return Task.CompletedTask;

                var bytes = BitConverter.GetBytes(counter);
                Check(PlayerVault_KeychainWrite("counter:" + playerId, bytes, bytes.Length), "storing the save counter");
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string playerId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Check(PlayerVault_KeychainDelete("key:" + playerId), "deleting the signing key");
                Check(PlayerVault_KeychainDelete("counter:" + playerId), "deleting the save counter");
            }

            return Task.CompletedTask;
        }

        static long ReadCounter(string playerId)
        {
            var bytes = new byte[sizeof(long)];
            var read = PlayerVault_KeychainRead("counter:" + playerId, bytes, bytes.Length);

            if (read == 0) return 0;
            if (read != bytes.Length) throw new InvalidOperationException($"Reading the save counter from the Keychain failed ({read}).");
            return BitConverter.ToInt64(bytes, 0);
        }

        static void Check(int status, string what)
        {
            if (status != 0) throw new InvalidOperationException($"The Keychain failed {what} (OSStatus {status}).");
        }
    }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>
    /// Keeps the key and counter in private SharedPreferences, with the key encrypted by an AES
    /// key that lives in the Android Keystore and cannot be exported. The Java half is
    /// <c>Plugins/Android/PlayerVaultKeyStore.java</c>.
    /// </summary>
    /// <remarks>
    /// Calls into Java run on Unity's main thread, captured at construction. A thread-pool thread
    /// would have to be attached to the Java VM, and one that exits while attached crashes the app.
    /// The vault only calls in when opening and once after each write, so the extra frame of
    /// latency is not noticeable.
    /// </remarks>
    internal sealed class AndroidKeystoreKeyStore : IVaultKeyStore
    {
        const string PluginClass = "com.playervault.PlayerVaultKeyStore";

        readonly SynchronizationContext _unityContext = SynchronizationContext.Current;

        public Task<byte[]> GetOrCreateKeyAsync(string playerId, CancellationToken cancellationToken = default) =>
            OnUnityThread(() => Convert.FromBase64String(Call<string>("getOrCreateKey", playerId)));

        public Task<long> ReadCounterAsync(string playerId, CancellationToken cancellationToken = default) =>
            OnUnityThread(() => Call<long>("readCounter", playerId));

        public Task WriteCounterAsync(string playerId, long counter, CancellationToken cancellationToken = default) =>
            OnUnityThread(() => Call<bool>("writeCounter", playerId, counter));

        public Task DeleteAsync(string playerId, CancellationToken cancellationToken = default) =>
            OnUnityThread(() => Call<bool>("delete", playerId));

        static T Call<T>(string method, params object[] arguments)
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var plugin = new AndroidJavaClass(PluginClass);

            var withContext = new object[arguments.Length + 1];
            withContext[0] = activity;
            Array.Copy(arguments, 0, withContext, 1, arguments.Length);

            return plugin.CallStatic<T>(method, withContext);
        }

        Task<T> OnUnityThread<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Run()
            {
                try { completion.TrySetResult(work()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }

            if (_unityContext == null || SynchronizationContext.Current == _unityContext) Run();
            else _unityContext.Post(_ => Run(), null);

            return completion.Task;
        }
    }
#endif
}
