using System;
using System.Threading.Tasks;
using NUnit.Framework;
using static PlayerVault.Tests.VaultTestHarness;

namespace PlayerVault.Tests
{
    /// <summary>
    /// Edge cases: reentrancy, overlapping work, failing storage and shutdown.
    /// </summary>
    [TestFixture]
    internal sealed class ReliabilityTests
    {
        [Test]
        public void A_subscriber_that_reclaims_from_the_pending_event_does_not_get_a_second_grant()
        {
            // With synchronous storage and transport, the pending event fires while the claim is
            // still being set up. A handler that claimed the same reward from there used to
            // start a second claim and grant the reward twice.
            var transport = FakeTransport.Ok();
            var storage = new InMemoryStorage();
            using var vault = Open(Config(transport, storage));

            Task<ClaimResult> reentrant = null;
            vault.ClaimStateChanged += record =>
            {
                if (reentrant == null && record.Status == ClaimStatus.Pending)
                    reentrant = vault.ClaimAsync("level-10", "coins", 250);
            };

            var first = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.IsNotNull(reentrant, "the test needs the reentrant call to have happened");
            var second = Run(reentrant);

            Assert.AreEqual(ClaimStatus.Granted, first.Status);
            Assert.AreEqual(350, vault.GetBalance("coins"), "250 granted once, not twice");
            Assert.AreEqual(1, transport.CallCount, "the second call must join the first, not race it");
            Assert.AreEqual(ClaimStatus.Granted, second.Status);
        }

        [Test]
        public void A_pending_reward_cannot_be_retried_with_a_different_payload()
        {
            // Retrying a pending coin reward as a lives reward used to grant lives while the
            // record still said coins.
            var storage = new InMemoryStorage();

            using (var offline = Open(Config(
                FakeTransport.Always(TransportResponse.Indeterminate("offline")), storage, maxAttempts: 1)))
                Run(offline.ClaimAsync("level-10", "coins", 250));

            var transport = FakeTransport.Ok();
            using var vault = Open(Config(transport, storage));

            var result = Run(vault.ClaimAsync("level-10", "lives", 2));

            Assert.AreEqual(ClaimFailure.Conflict, result.Failure);
            Assert.AreEqual(0, transport.CallCount, "a conflicting retry must not reach the network");
            Assert.AreEqual(3, vault.GetBalance("lives"), "and must not move the balance it asked for");
            Assert.AreEqual("coins", vault.GetClaim("level-10").Resource, "the stored claim stays as it was");

            // The original payload still settles normally.
            Assert.AreEqual(ClaimStatus.Granted, Run(vault.ClaimAsync("level-10", "coins", 250)).Status);
        }

        [Test]
        public void A_claim_that_is_accepted_but_unsaveable_stays_pending_and_settles_on_the_next_launch()
        {
            // Simulates a failure at the grant write: the request succeeded but the write did
            // not. The reward must still be granted, and only once.
            var storage = new InMemoryStorage();
            storage.OnWrite = payload =>
            {
                if (payload.Contains("Granted")) throw new InvalidOperationException("the disk went away");
            };

            using (var crashing = Open(Config(FakeTransport.Ok(), storage)))
            {
                var result = Run(crashing.ClaimAsync("level-10", "coins", 250));

                Assert.AreEqual(ClaimStatus.Pending, result.Status);
                Assert.AreEqual(ClaimFailure.Storage, result.Failure);
                Assert.AreEqual(100, crashing.GetBalance("coins"), "the grant must be rolled back with its write");
            }

            storage.OnWrite = null;
            var transport = FakeTransport.Ok();
            using var relaunched = Open(Config(transport, storage));

            Assert.AreEqual(1, relaunched.PendingClaims.Count);
            Run(relaunched.ResumePendingAsync());

            Assert.AreEqual(350, relaunched.GetBalance("coins"));
            Assert.AreEqual(ClaimStatus.Granted, relaunched.GetClaim("level-10").Status);
        }

        [Test]
        public void A_throwing_subscriber_cannot_prevent_the_save()
        {
            var storage = new InMemoryStorage();
            var logger = new NullLogger();

            using (var vault = Open(Config(storage: storage, logger: logger)))
            {
                vault.BalanceChanged += (resource, balance) => throw new InvalidOperationException("a broken HUD");

                Assert.DoesNotThrow(() => vault.Spend("coins", 30));
                Run(vault.FlushAsync());
            }

            using var reopened = Open(Config(storage: storage));

            Assert.AreEqual(70, reopened.GetBalance("coins"), "the deduction must reach disk despite the handler");
            Assert.IsNotEmpty(logger.Errors, "and the broken handler must be reported rather than hidden");
        }

        [Test]
        public void Disposing_while_a_write_is_in_flight_does_not_throw()
        {
            var storage = new InMemoryStorage();
            var vault = Open(Config(storage: storage));

            storage.OnWrite = _ => vault.Dispose();

            Assert.DoesNotThrow(() => Run(vault.FlushAsync()),
                "shutdown must not turn an in-flight write into an ObjectDisposedException");
        }

        [Test]
        public void Editing_the_config_after_opening_does_not_change_the_vault()
        {
            var storage = new InMemoryStorage();
            var config = Config(storage: storage);

            using var vault = Open(config);

            config.PlayerId = "somebody-else";
            config.ApiUrl = "https://elsewhere.test/claim";

            vault.Spend("coins", 10);
            Run(vault.FlushAsync());

            Assert.AreEqual(Player, vault.PlayerId);
            Assert.IsTrue(storage.Has(Player), "the ledger must stay under the identity it was opened with");
            Assert.IsFalse(storage.Has("somebody-else"));
        }

        [Test]
        public void Spending_is_unaffected_by_a_claim_completing_underneath_it()
        {
            // Both doubles resume on the thread pool, so the claim completes on a different
            // thread from the spend.
            var gate = new TaskCompletionSource<bool>();
            var transport = FakeTransport.Ok();
            transport.Gate = gate.Task;
            transport.CompleteAsynchronously = true;

            var storage = new InMemoryStorage { CompleteAsynchronously = true };
            using var vault = Open(Config(transport, storage));

            var claim = vault.ClaimAsync("level-10", "coins", 250);

            var spend = vault.Spend("coins", 60);
            Assert.IsTrue(spend.Success, "a claim in flight must never hold the existing balance hostage");

            gate.SetResult(true);
            Assert.AreEqual(ClaimStatus.Granted, Run(claim).Status);

            Assert.AreEqual(290, vault.GetBalance("coins"), "100 - 60 + 250; neither change may be lost");

            Run(vault.FlushAsync());
            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(290, reopened.GetBalance("coins"));
        }

        [Test]
        public void An_invalid_resource_name_is_refused_rather_than_throwing()
        {
            var config = Config();
            config.AllowUndeclaredResources = true;

            using var vault = Open(config);

            Assert.AreEqual(SpendFailure.UnknownResource, vault.GrantLocal(null, 1).Failure);
            Assert.AreEqual(SpendFailure.UnknownResource, vault.GrantLocal("   ", 1).Failure);
            Assert.AreEqual(SpendFailure.UnknownResource, vault.Spend(null, 1).Failure);
            Assert.IsFalse(vault.CanSpend(null, 1));
        }

        [Test]
        public void Claims_and_reads_survive_asynchronously_completing_dependencies()
        {
            // Every other test uses doubles that complete inline, which never happens in a real
            // game.
            var storage = new InMemoryStorage { CompleteAsynchronously = true };
            var transport = FakeTransport.Ok();
            transport.CompleteAsynchronously = true;

            using var vault = Open(Config(transport, storage));

            var claims = new[]
            {
                vault.ClaimAsync("level-1", "coins", 10),
                vault.ClaimAsync("level-2", "coins", 20),
                vault.ClaimAsync("level-3", "coins", 30)
            };

            Run(Task.WhenAll(claims));

            Assert.AreEqual(160, vault.GetBalance("coins"));
            Assert.AreEqual(3, transport.CallCount);

            Run(vault.FlushAsync());
            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(160, reopened.GetBalance("coins"), "every concurrent commit must be on disk");
        }

        [Test]
        public void A_claim_record_explains_what_went_wrong()
        {
            var transport = FakeTransport.Always(TransportResponse.Retryable(429, "{}", "Too Many Requests"));
            using var vault = Open(Config(transport, maxAttempts: 2));

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(429, result.Record.LastStatusCode);
            Assert.AreEqual("Too Many Requests", result.Record.LastError);
            Assert.IsTrue(result.Record.IsAwaitingRetry);
            StringAssert.Contains("429", result.Record.Describe());
        }

        [Test]
        public void Closing_flushes_before_it_disposes()
        {
            var storage = new InMemoryStorage();
            var config = Config(storage: storage);
            config.FlushMode = FlushMode.Debounced;

            var vault = Open(config);
            vault.Spend("coins", 25);

            Run(vault.CloseAsync());

            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(75, reopened.GetBalance("coins"), "a debounced change must survive an orderly shutdown");
        }
    }

    /// <summary>
    /// Transactions, which stop a purchase from charging without delivering.
    /// </summary>
    [TestFixture]
    internal sealed class TransactionTests
    {
        [Test]
        public void A_purchase_charges_and_delivers_in_one_commit()
        {
            var storage = new InMemoryStorage();
            var config = Config(storage: storage, resources: new[]
            {
                new ResourceDefinition("coins", initial: 100),
                new ResourceDefinition("level-2-unlocked", initial: 0, max: 1)
            });

            using var vault = Open(config);

            var result = Run(vault.TransactAsync(VaultTransaction.Purchase("coins", 60, "level-2-unlocked")));

            Assert.IsTrue(result.Success);
            Assert.AreEqual(-60, result.AppliedTo("coins"));
            Assert.AreEqual(1, result.AppliedTo("level-2-unlocked"));
            Assert.AreEqual(40, vault.GetBalance("coins"));
            Assert.AreEqual(1, vault.GetBalance("level-2-unlocked"));

            using var reopened = Open(Config(storage: storage, resources: new[]
            {
                new ResourceDefinition("coins", initial: 100),
                new ResourceDefinition("level-2-unlocked", initial: 0, max: 1)
            }));

            Assert.AreEqual(40, reopened.GetBalance("coins"), "both halves of the purchase must be on disk together");
            Assert.AreEqual(1, reopened.GetBalance("level-2-unlocked"));
        }

        [Test]
        public void A_purchase_that_cannot_be_afforded_moves_nothing()
        {
            var config = Config(resources: new[]
            {
                new ResourceDefinition("coins", initial: 10),
                new ResourceDefinition("level-2-unlocked", initial: 0, max: 1)
            });

            using var vault = Open(config);

            var result = Run(vault.TransactAsync(VaultTransaction.Purchase("coins", 60, "level-2-unlocked")));

            Assert.IsFalse(result.Success);
            Assert.AreEqual(SpendFailure.InsufficientBalance, result.Failure);
            Assert.AreEqual("coins", result.FailedResource);
            Assert.AreEqual(10, vault.GetBalance("coins"));
            Assert.AreEqual(0, vault.GetBalance("level-2-unlocked"));
        }

        [Test]
        public void A_purchase_of_something_already_owned_is_refused_rather_than_charged()
        {
            var config = Config(resources: new[]
            {
                new ResourceDefinition("coins", initial: 100),
                new ResourceDefinition("level-2-unlocked", initial: 1, max: 1)
            });

            using var vault = Open(config);

            var result = Run(vault.TransactAsync(VaultTransaction.Purchase("coins", 60, "level-2-unlocked")));

            Assert.IsFalse(result.Success);
            Assert.AreEqual(SpendFailure.AtMaximum, result.Failure);
            Assert.AreEqual(100, vault.GetBalance("coins"), "an undeliverable purchase must not take the money");
        }

        [Test]
        public void A_transaction_that_cannot_be_saved_moves_nothing()
        {
            var storage = new InMemoryStorage();
            var config = Config(storage: storage, resources: new[]
            {
                new ResourceDefinition("coins", initial: 100),
                new ResourceDefinition("level-2-unlocked", initial: 0, max: 1)
            });

            using var vault = Open(config);
            storage.FailWrites = true;

            var result = Run(vault.TransactAsync(VaultTransaction.Purchase("coins", 60, "level-2-unlocked")));

            Assert.IsFalse(result.Success);
            Assert.AreEqual(SpendFailure.StorageUnavailable, result.Failure);
            Assert.AreEqual(100, vault.GetBalance("coins"), "a purchase that did not reach disk must not stand in memory");
            Assert.AreEqual(0, vault.GetBalance("level-2-unlocked"));
        }

        [Test]
        public void An_unknown_resource_refuses_the_whole_transaction()
        {
            using var vault = Open(Config());

            var result = Run(vault.TransactAsync(VaultTransaction.Purchase("coins", 10, "gems")));

            Assert.IsFalse(result.Success);
            Assert.AreEqual(SpendFailure.UnknownResource, result.Failure);
            Assert.AreEqual("gems", result.FailedResource);
            Assert.AreEqual(100, vault.GetBalance("coins"));
        }

        [Test]
        public void Steps_are_evaluated_in_order_against_the_running_balance()
        {
            using var vault = Open(Config());

            // Starts with 100 coins. Spending 140 only works after the grant of 40, which shows
            // that each step is checked against the running balance.
            var result = Run(vault.TransactAsync(
                new VaultTransaction().Grant("coins", 40).Spend("coins", 140)));

            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, vault.GetBalance("coins"));
            Assert.AreEqual(-100, result.AppliedTo("coins"), "one net change per resource");
        }

        [Test]
        public void A_grant_inside_a_transaction_is_clamped_like_any_other()
        {
            using var vault = Open(Config());   // lives: 3, max 5

            var result = Run(vault.TransactAsync(new VaultTransaction().Spend("coins", 10).Grant("lives", 10)));

            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.AppliedTo("lives"), "the clamp is reported, not treated as a refusal");
            Assert.AreEqual(5, vault.GetBalance("lives"));
            Assert.AreEqual(90, vault.GetBalance("coins"));
        }
    }
}
