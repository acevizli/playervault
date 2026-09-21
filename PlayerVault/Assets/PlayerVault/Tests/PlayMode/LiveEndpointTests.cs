using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace PlayerVault.Tests
{
    /// <summary>
    /// The only tests that touch the network. They exist because
    /// <see cref="UnityWebRequestTransport"/> is the one component the EditMode suite
    /// cannot exercise — everything else runs against a fake.
    /// </summary>
    /// <remarks>
    /// Tagged <c>Network</c> so they can be excluded. The sample endpoint returns a
    /// transient 502 occasionally, so a failure here is worth re-running once before
    /// believing it.
    /// </remarks>
    [TestFixture]
    [Category("Network")]
    internal sealed class LiveEndpointTests
    {
        sealed class MemoryStorage : IVaultStorage
        {
            readonly Dictionary<string, string> _files = new Dictionary<string, string>();

            public Task<string> ReadAsync(string key, CancellationToken ct = default) =>
                Task.FromResult(_files.TryGetValue(key, out var v) ? v : null);

            public Task WriteAsync(string key, string payload, CancellationToken ct = default)
            {
                _files[key] = payload;
                return Task.CompletedTask;
            }

            public Task QuarantineAsync(string key, CancellationToken ct = default)
            {
                _files.Remove(key);
                return Task.CompletedTask;
            }
        }

        static VaultConfig LiveConfig() 
        {
            var config = new VaultConfig
            {
                PlayerId = "test-player",
                ApiUrl = "https://httpbin.org/anything",
                Storage = new MemoryStorage(),
                ResumePendingOnOpen = false
            };
            config.Resources.Add(new ResourceDefinition("coins"));
            return config;
        }

        static IEnumerator Await(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception;
        }

        [UnityTest]
        public IEnumerator A_real_claim_against_the_live_endpoint_grants_the_reward()
        {
            var open = Vault.OpenAsync(LiveConfig());
            yield return Await(open);

            using var vault = open.Result;

            var rewardId = "playmode-" + Guid.NewGuid().ToString("N");
            var claim = vault.ClaimAsync(rewardId, "coins", 100);
            yield return Await(claim);

            Assert.AreEqual(ClaimStatus.Granted, claim.Result.Status,
                $"live claim failed: {claim.Result.Failure}");
            Assert.AreEqual(100, vault.GetBalance("coins"));
        }

        [UnityTest]
        public IEnumerator An_unreachable_host_leaves_the_claim_pending_rather_than_failed()
        {
            var config = LiveConfig();
            config.ApiUrl = "https://this-host-does-not-exist.invalid/claim";
            config.Retry = new RetryPolicy
            {
                MaxAttempts = 2,
                BaseDelay = TimeSpan.FromMilliseconds(10),
                Timeout = TimeSpan.FromSeconds(5)
            };

            var open = Vault.OpenAsync(config);
            yield return Await(open);

            using var vault = open.Result;

            var claim = vault.ClaimAsync("unreachable-test", "coins", 100);
            yield return Await(claim);

            Assert.AreEqual(ClaimStatus.Pending, claim.Result.Status);
            Assert.AreEqual(ClaimFailure.Network, claim.Result.Failure);
            Assert.AreEqual(0, vault.GetBalance("coins"));
            Assert.AreEqual(1, vault.PendingClaims.Count);
        }
    }
}
