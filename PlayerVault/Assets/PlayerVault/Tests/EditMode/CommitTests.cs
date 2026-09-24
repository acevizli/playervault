using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using static PlayerVault.Tests.VaultTestHarness;

namespace PlayerVault.Tests
{
    /// <summary>
    /// Commits that overlap. A durable operation that fails is undone in memory and reported as
    /// failed, so no later write may put its change on disk.
    /// </summary>
    [TestFixture]
    internal sealed class CommitTests
    {
        [Test]
        public void A_failed_spend_is_not_saved_by_the_spend_queued_behind_it()
        {
            // The first spend's write is held open, the second queues behind it, then the first
            // write fails. The second used to snapshot the first spend before it was undone, so
            // the player lost 10 coins on disk for a spend they were told had failed.
            var storage = new InMemoryStorage();

            using (var vault = Open(Config(storage: storage)))
            {
                var held = HoldNextWrite(storage);

                var first = vault.SpendAsync("coins", 10);
                var second = vault.SpendAsync("coins", 20);
                Assert.IsFalse(first.IsCompleted, "the test needs the first write held open");

                FailWrite(held);

                Assert.AreEqual(SpendFailure.StorageUnavailable, Run(first).Failure);
                Assert.IsTrue(Run(second).Success);
                Assert.AreEqual(80, vault.GetBalance("coins"));
            }

            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(80, reopened.GetBalance("coins"), "disk must agree with memory: only the 20 was spent");
        }

        [Test]
        public void A_failed_grant_is_not_saved_by_the_grant_queued_behind_it()
        {
            var storage = new InMemoryStorage();

            using (var vault = Open(Config(storage: storage)))
            {
                var held = HoldNextWrite(storage);

                var first = vault.GrantAsync("coins", 10);
                var second = vault.GrantAsync("coins", 20);

                FailWrite(held);

                Assert.AreEqual(SpendFailure.StorageUnavailable, Run(first).Failure);
                Assert.IsTrue(Run(second).Success);
                Assert.AreEqual(120, vault.GetBalance("coins"));
            }

            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(120, reopened.GetBalance("coins"));
        }

        [Test]
        public void A_failed_purchase_is_not_saved_by_the_spend_queued_behind_it()
        {
            var storage = new InMemoryStorage();

            using (var vault = Open(Config(storage: storage)))
            {
                var held = HoldNextWrite(storage);

                var purchase = vault.TransactAsync(VaultTransaction.Purchase("coins", 30, "lives"));
                var spend = vault.SpendAsync("coins", 5);

                FailWrite(held);

                Assert.AreEqual(SpendFailure.StorageUnavailable, Run(purchase).Failure);
                Assert.IsTrue(Run(spend).Success);
                Assert.AreEqual(95, vault.GetBalance("coins"));
                Assert.AreEqual(3, vault.GetBalance("lives"));
            }

            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(95, reopened.GetBalance("coins"), "neither half of the failed purchase may reach disk");
            Assert.AreEqual(3, reopened.GetBalance("lives"));
        }

        [Test]
        public void A_claim_grant_that_failed_to_save_is_not_saved_by_the_next_commit()
        {
            var storage = new InMemoryStorage();

            using (var vault = Open(Config(FakeTransport.Ok(), storage)))
            {
                var held = HoldNextWrite(storage, payload => payload.Contains("Granted"));

                var claim = vault.ClaimAsync("level-10", "coins", 250);
                var spend = vault.SpendAsync("coins", 10);
                Assert.IsFalse(claim.IsCompleted, "the test needs the grant write held open");

                FailWrite(held);

                var result = Run(claim);
                Assert.AreEqual(ClaimStatus.Pending, result.Status);
                Assert.AreEqual(ClaimFailure.Storage, result.Failure);
                Assert.IsTrue(Run(spend).Success);
                Assert.AreEqual(90, vault.GetBalance("coins"));
            }

            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(90, reopened.GetBalance("coins"), "the unsaved grant must not reach disk through the spend");
            Assert.AreEqual(ClaimStatus.Pending, reopened.GetClaim("level-10").Status, "it is retried instead");
        }

        [Test]
        public void A_flush_does_not_save_a_spend_whose_write_is_failing()
        {
            var storage = new InMemoryStorage();

            using (var vault = Open(Config(storage: storage)))
            {
                var held = HoldNextWrite(storage);

                var spend = vault.SpendAsync("coins", 10);
                var flush = vault.FlushAsync();

                FailWrite(held);

                Assert.AreEqual(SpendFailure.StorageUnavailable, Run(spend).Failure);
                Run(flush);
            }

            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(100, reopened.GetBalance("coins"));
        }

        [Test]
        public void Background_saves_queued_behind_a_write_share_one_write()
        {
            var storage = new InMemoryStorage();
            var vault = Open(Config(storage: storage));

            var writesBefore = storage.Writes;
            var held = HoldNextWrite(storage);

            vault.Spend("coins", 1);   // its write is held
            for (var i = 0; i < 5; i++) vault.Spend("coins", 1);
            var flush = vault.FlushAsync();

            held.SetResult(true);
            Run(flush);

            Assert.AreEqual(2, storage.Writes - writesBefore, "the held write, then one write for everything queued behind it");

            vault.Dispose();
            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(94, reopened.GetBalance("coins"));
        }

        /// <summary>Holds the next write that matches until the test completes or faults the task.</summary>
        static TaskCompletionSource<bool> HoldNextWrite(InMemoryStorage storage, Func<string, bool> matches = null)
        {
            var held = new TaskCompletionSource<bool>();
            var taken = 0;

            storage.Hold = payload =>
                (matches == null || matches(payload)) && Interlocked.Exchange(ref taken, 1) == 0 ? held.Task : null;

            return held;
        }

        static void FailWrite(TaskCompletionSource<bool> held) =>
            held.SetException(new IOException("the disk went away"));
    }
}
