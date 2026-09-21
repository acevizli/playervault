using System.Threading.Tasks;
using NUnit.Framework;
using static PlayerVault.Tests.VaultTestHarness;

namespace PlayerVault.Tests
{
    [TestFixture]
    internal sealed class ClaimTests
    {
        [Test]
        public void A_successful_claim_grants_the_reward()
        {
            var transport = FakeTransport.Ok();
            using var vault = Open(Config(transport));

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.IsTrue(result.Success);
            Assert.AreEqual(ClaimStatus.Granted, result.Status);
            Assert.AreEqual(250, result.Record.AmountApplied);
            Assert.AreEqual(350, vault.GetBalance("coins"));
        }

        [Test]
        public void The_request_body_matches_the_documented_shape()
        {
            var transport = FakeTransport.Ok();
            using var vault = Open(Config(transport));

            Run(vault.ClaimAsync("level-10-first-completion", "coins", 100));

            var body = transport.Bodies[0];
            StringAssert.Contains("\"player_id\":\"test-player\"", body.Replace(" ", ""));
            StringAssert.Contains("\"reward_id\":\"level-10-first-completion\"", body.Replace(" ", ""));
            StringAssert.Contains("\"resource\":\"coins\"", body.Replace(" ", ""));
            StringAssert.Contains("\"amount\":100", body.Replace(" ", ""));
        }

        [Test]
        public void Claiming_the_same_reward_twice_grants_once_and_sends_one_request()
        {
            var transport = FakeTransport.Ok();
            using var vault = Open(Config(transport));

            var first = Run(vault.ClaimAsync("level-10", "coins", 250));
            var second = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.Granted, first.Status);
            Assert.AreEqual(ClaimStatus.AlreadyGranted, second.Status);
            Assert.AreEqual(350, vault.GetBalance("coins"), "the reward must not be applied twice");
            Assert.AreEqual(1, transport.CallCount, "a known-granted reward must not reach the network at all");
        }

        [Test]
        public void Two_concurrent_claims_for_one_reward_share_a_single_request()
        {
            var gate = new TaskCompletionSource<bool>();
            var transport = FakeTransport.Ok();
            transport.Gate = gate.Task;

            using var vault = Open(Config(transport));

            var first = vault.ClaimAsync("level-10", "coins", 250);
            var second = vault.ClaimAsync("level-10", "coins", 250);

            gate.SetResult(true);
            Run(Task.WhenAll(first, second));

            Assert.AreEqual(1, transport.CallCount, "the second caller should join the first, not race it");
            Assert.AreEqual(350, vault.GetBalance("coins"));
        }

        [Test]
        public void A_claim_is_clamped_by_the_maximum_and_says_so()
        {
            using var vault = Open(Config());

            var result = Run(vault.ClaimAsync("bonus-lives", "lives", 10));   // 3 already, max 5

            Assert.AreEqual(ClaimStatus.Granted, result.Status);
            Assert.AreEqual(10, result.Record.AmountRequested);
            Assert.AreEqual(2, result.Record.AmountApplied);
            Assert.IsTrue(result.Record.WasClamped);
            Assert.AreEqual(5, vault.GetBalance("lives"));
        }

        [Test]
        public void A_clamped_claim_still_counts_as_granted_so_it_cannot_be_reclaimed()
        {
            var transport = FakeTransport.Ok();
            using var vault = Open(Config(transport));

            Run(vault.ClaimAsync("bonus-lives", "lives", 10));
            var again = Run(vault.ClaimAsync("bonus-lives", "lives", 10));

            Assert.AreEqual(ClaimStatus.AlreadyGranted, again.Status);
            Assert.AreEqual(1, transport.CallCount);
        }

        [Test]
        public void A_rejection_is_terminal_and_is_not_retried()
        {
            var transport = FakeTransport.Always(TransportResponse.Rejected(400, "{}"));
            using var vault = Open(Config(transport));

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.Failed, result.Status);
            Assert.AreEqual(ClaimFailure.Rejected, result.Failure);
            Assert.AreEqual(1, transport.CallCount, "a 4xx is deterministic; retrying it is wasted");
            Assert.AreEqual(100, vault.GetBalance("coins"));
        }

        [Test]
        public void A_server_error_is_retried_and_leaves_the_claim_pending()
        {
            var transport = FakeTransport.Always(TransportResponse.Retryable(503, "{}"));
            using var vault = Open(Config(transport, maxAttempts: 3));

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.Pending, result.Status,
                "an exhausted budget must stay pending so the next launch can replay it");
            Assert.AreEqual(3, transport.CallCount);
            Assert.AreEqual(100, vault.GetBalance("coins"));
        }

        [Test]
        public void Being_offline_leaves_the_claim_pending_rather_than_failed()
        {
            var transport = FakeTransport.Always(TransportResponse.Indeterminate("no connection"));
            using var vault = Open(Config(transport, maxAttempts: 2));

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.Pending, result.Status);
            Assert.AreEqual(ClaimFailure.Network, result.Failure);
            Assert.AreEqual(2, transport.CallCount);
        }

        [Test]
        public void A_transient_failure_followed_by_success_still_grants()
        {
            var transport = FakeTransport.FailsThenSucceeds(2, TransportResponse.Retryable(500, "{}"));
            using var vault = Open(Config(transport, maxAttempts: 3));

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.Granted, result.Status);
            Assert.AreEqual(3, transport.CallCount);
            Assert.AreEqual(350, vault.GetBalance("coins"));
        }

        [Test]
        public void A_success_carrying_a_non_JSON_body_does_not_grant()
        {
            // The trap from the research: Unity's download handler does no parsing, so a
            // proxy's HTML error page arrives as Result.Success. Granting on that would be
            // a reward handed out on the strength of a 502.
            var transport = FakeTransport.Always(
                TransportResponse.Success(200, "<html><body>502 Bad Gateway</body></html>"));
            using var vault = Open(Config(transport));

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.Failed, result.Status);
            Assert.AreEqual(ClaimFailure.Parse, result.Failure);
            Assert.AreEqual(100, vault.GetBalance("coins"));
        }

        [TestCase("", "coins", 100)]
        [TestCase(null, "coins", 100)]
        [TestCase("level-10", "coins", 0)]
        [TestCase("level-10", "coins", -1)]
        [TestCase("level-10", "gems", 100)]
        public void Malformed_claims_are_refused_without_reaching_the_network(
            string rewardId, string resource, long amount)
        {
            var transport = FakeTransport.Ok();
            using var vault = Open(Config(transport));

            var result = Run(vault.ClaimAsync(rewardId, resource, amount));

            Assert.AreEqual(ClaimStatus.Failed, result.Status);
            Assert.AreEqual(ClaimFailure.Invalid, result.Failure);
            Assert.AreEqual(0, transport.CallCount);
        }

        [Test]
        public void A_claim_record_is_queryable_after_the_fact()
        {
            using var vault = Open(Config());
            Run(vault.ClaimAsync("level-10", "coins", 250));

            var record = vault.GetClaim("level-10");

            Assert.IsNotNull(record);
            Assert.AreEqual(ClaimStatus.Granted, record.Status);
            Assert.AreEqual(250, record.AmountApplied);
            Assert.IsNull(vault.GetClaim("never-attempted"));
        }

        [Test]
        public void A_pending_claim_does_not_block_spending()
        {
            var transport = FakeTransport.Always(TransportResponse.Indeterminate("offline"));
            using var vault = Open(Config(transport, maxAttempts: 1));

            Run(vault.ClaimAsync("level-10", "coins", 250));
            Assert.AreEqual(1, vault.PendingClaims.Count);

            var spend = vault.Spend("coins", 60);

            Assert.IsTrue(spend.Success, "an unfinished claim must never hold the existing balance hostage");
            Assert.AreEqual(40, vault.GetBalance("coins"));
        }
    }
}
