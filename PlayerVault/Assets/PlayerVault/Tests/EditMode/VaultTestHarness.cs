using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PlayerVault.Tests
{
    /// <summary>
    /// Shared setup for the suite.
    /// </summary>
    internal static class VaultTestHarness
    {
        public const string Player = "test-player";

        /// <summary>
        /// Runs a task to completion synchronously.
        /// </summary>
        /// <remarks>
        /// Safe here, and deliberate: every test double completes synchronously
        /// (Task.FromResult / Task.CompletedTask) and the SDK uses ConfigureAwait(false)
        /// throughout, so nothing is waiting on a captured context. Blocking keeps the tests
        /// plain [Test] methods with ordinary asserts, rather than coroutine-shaped
        /// [UnityTest] methods where a failed assert is harder to read.
        /// </remarks>
        public static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        public static void Run(Task task) => task.GetAwaiter().GetResult();

        public static VaultConfig Config(
            IVaultTransport transport = null,
            IVaultStorage storage = null,
            IVaultClock clock = null,
            IVaultLogger logger = null,
            IEnumerable<ResourceDefinition> resources = null,
            int maxAttempts = 3)
        {
            var config = new VaultConfig
            {
                PlayerId = Player,
                ApiUrl = "https://example.test/claim",
                Transport = transport ?? FakeTransport.Ok(),
                Storage = storage ?? new InMemoryStorage(),
                Clock = clock ?? new FakeClock(),
                Logger = logger ?? new NullLogger(),
                ResumePendingOnOpen = false,
                Retry = new RetryPolicy { MaxAttempts = maxAttempts, BaseDelay = TimeSpan.FromMilliseconds(1) }
            };

            foreach (var resource in resources ?? Default()) config.Resources.Add(resource);
            return config;
        }

        public static IEnumerable<ResourceDefinition> Default() => new[]
        {
            new ResourceDefinition("coins", initial: 100),
            new ResourceDefinition("lives", initial: 3, max: 5)
        };

        public static Vault Open(VaultConfig config) => Run(Vault.OpenAsync(config));
    }
}
