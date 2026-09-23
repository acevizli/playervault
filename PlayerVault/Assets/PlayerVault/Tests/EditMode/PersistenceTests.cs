using System;
using NUnit.Framework;
using static PlayerVault.Tests.VaultTestHarness;

namespace PlayerVault.Tests
{
    /// <summary>
    /// A restart is simulated by disposing the vault and opening a new one on the same
    /// storage, which is what a relaunch does with the real file.
    /// </summary>
    [TestFixture]
    internal sealed class PersistenceTests
    {
        [Test]
        public void Balances_survive_a_restart()
        {
            var storage = new InMemoryStorage();

            using (var first = Open(Config(storage: storage)))
            {
                first.Spend("coins", 40);
                Run(first.FlushAsync());
            }

            using var second = Open(Config(storage: storage));

            Assert.AreEqual(60, second.GetBalance("coins"));
        }

        [Test]
        public void A_granted_reward_stays_granted_across_a_restart()
        {
            var storage = new InMemoryStorage();
            var transport = FakeTransport.Ok();

            using (var first = Open(Config(transport, storage)))
                Run(first.ClaimAsync("level-10", "coins", 250));

            var secondTransport = FakeTransport.Ok();
            using var second = Open(Config(secondTransport, storage));

            var result = Run(second.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.AlreadyGranted, result.Status);
            Assert.AreEqual(0, secondTransport.CallCount, "the guard must survive the restart, not just the session");
            Assert.AreEqual(350, second.GetBalance("coins"), "and the reward must not be applied a second time");
        }

        [Test]
        public void An_unfinished_claim_survives_a_restart_and_completes_on_resume()
        {
            var storage = new InMemoryStorage();

            using (var offline = Open(Config(
                FakeTransport.Always(TransportResponse.Indeterminate("no connection")), storage, maxAttempts: 1)))
            {
                Run(offline.ClaimAsync("level-10", "coins", 250));
                Assert.AreEqual(1, offline.PendingClaims.Count);
            }

            var backOnline = FakeTransport.Ok();
            using var resumed = Open(Config(backOnline, storage));

            Assert.AreEqual(1, resumed.PendingClaims.Count, "the pending claim should be restored from disk");

            Run(resumed.ResumePendingAsync());

            Assert.AreEqual(0, resumed.PendingClaims.Count);
            Assert.AreEqual(ClaimStatus.Granted, resumed.GetClaim("level-10").Status);
            Assert.AreEqual(350, resumed.GetBalance("coins"));
        }

        [Test]
        public void Resuming_twice_does_not_grant_twice()
        {
            var storage = new InMemoryStorage();

            using (var offline = Open(Config(
                FakeTransport.Always(TransportResponse.Indeterminate("offline")), storage, maxAttempts: 1)))
                Run(offline.ClaimAsync("level-10", "coins", 250));

            var transport = FakeTransport.Ok();
            using var resumed = Open(Config(transport, storage));

            Run(resumed.ResumePendingAsync());
            Run(resumed.ResumePendingAsync());

            Assert.AreEqual(1, transport.CallCount);
            Assert.AreEqual(350, resumed.GetBalance("coins"));
        }

        [Test]
        public void A_terminal_failure_is_not_replayed_on_the_next_launch()
        {
            var storage = new InMemoryStorage();

            using (var first = Open(Config(FakeTransport.Always(TransportResponse.Rejected(403, "{}")), storage)))
                Run(first.ClaimAsync("level-10", "coins", 250));

            var transport = FakeTransport.Ok();
            using var second = Open(Config(transport, storage));

            Run(second.ResumePendingAsync());

            Assert.AreEqual(0, transport.CallCount, "a deterministic rejection should not be retried forever");
            Assert.AreEqual(ClaimStatus.Failed, second.GetClaim("level-10").Status);
        }

        [Test]
        public void Unreadable_state_is_quarantined_and_the_player_starts_fresh()
        {
            var storage = new InMemoryStorage();
            storage.Seed(Player, "this is not json");

            using var vault = Open(Config(storage: storage));

            Assert.AreEqual(1, storage.Quarantined.Count, "the bad payload should be kept for support, not deleted");
            Assert.AreEqual(100, vault.GetBalance("coins"));
        }

        [Test]
        public void Unreadable_state_throws_when_the_game_asks_it_to()
        {
            var storage = new InMemoryStorage();
            storage.Seed(Player, "this is not json");

            var config = Config(storage: storage);
            config.OnCorruptData = CorruptDataPolicy.Throw;

            Assert.Throws<InvalidOperationException>(() => Open(config));
        }

        [Test]
        public void State_from_a_newer_SDK_is_refused_rather_than_truncated()
        {
            var storage = new InMemoryStorage();
            storage.Seed(Player,
                "{\"schemaVersion\":99,\"playerId\":\"test-player\",\"balances\":[],\"claims\":[]}");

            var exception = Assert.Throws<InvalidOperationException>(() => Open(Config(storage: storage)));
            StringAssert.Contains("newer SDK", exception.Message);
        }

        [Test]
        public void Lowering_a_maximum_clamps_the_stored_balance_on_load()
        {
            var storage = new InMemoryStorage();

            using (var generous = Open(Config(storage: storage,
                resources: new[] { new ResourceDefinition("lives", initial: 0, max: 99) })))
            {
                generous.GrantLocal("lives", 50);
                Run(generous.FlushAsync());
            }

            using var strict = Open(Config(storage: storage,
                resources: new[] { new ResourceDefinition("lives", initial: 0, max: 5) }));

            Assert.AreEqual(5, strict.GetBalance("lives"));
        }

        [Test]
        public void A_pending_record_is_written_before_the_request_goes_out()
        {
            // If the process dies during the request, the claim must already be on disk so it
            // can be retried.
            var storage = new InMemoryStorage();
            string persistedDuringRequest = null;

            var transport = new FakeTransport(_ =>
            {
                persistedDuringRequest = storage.Read(Player);
                return TransportResponse.Success(200, "{\"json\":{}}");
            });

            using var vault = Open(Config(transport, storage));
            Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.IsNotNull(persistedDuringRequest, "nothing was on disk when the request was sent");
            StringAssert.Contains("level-10", persistedDuringRequest);
            StringAssert.Contains("Pending", persistedDuringRequest);
        }

        [Test]
        public void A_claim_that_cannot_be_saved_is_not_reported_as_granted()
        {
            // A failed write must not be reported as Granted, or the game would give out a
            // reward that is lost on restart.
            var storage = new InMemoryStorage();
            var logger = new NullLogger();

            using var vault = Open(Config(storage: storage, logger: logger));
            storage.FailWrites = true;

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.Pending, result.Status, "a claim that did not reach disk is not granted");
            Assert.AreEqual(ClaimFailure.Storage, result.Failure);
            Assert.IsFalse(storage.Has(Player), "nothing should have reached disk");
            Assert.AreEqual(100, vault.GetBalance("coins"), "the balance must not move without the write that records it");
            Assert.IsNotEmpty(logger.Errors, "a persist failure must be reported, not swallowed");
        }

        [Test]
        public void A_claim_whose_write_ahead_record_fails_never_reaches_the_network()
        {
            // Without the pending record on disk, a crash would lose the claim, so the request
            // must not be sent.
            var storage = new InMemoryStorage { FailWrites = true };
            var transport = FakeTransport.Ok();

            using var vault = Open(Config(transport, storage));

            var result = Run(vault.ClaimAsync("level-10", "coins", 250));

            Assert.AreEqual(ClaimStatus.Pending, result.Status);
            Assert.AreEqual(ClaimFailure.Storage, result.Failure);
            Assert.AreEqual(0, transport.CallCount, "the request must wait for the write-ahead record");
        }

        [Test]
        public void A_spend_that_cannot_be_saved_is_refused_and_rolled_back()
        {
            var storage = new InMemoryStorage();
            using var vault = Open(Config(storage: storage));

            storage.FailWrites = true;
            var result = Run(vault.SpendAsync("coins", 30));

            Assert.IsFalse(result.Success, "SpendAsync promises durability; it must not report success without it");
            Assert.AreEqual(SpendFailure.StorageUnavailable, result.Failure);
            Assert.AreEqual(100, vault.GetBalance("coins"), "the deduction must be rolled back, not left in memory");
        }

        [Test]
        public void Flushing_reports_a_write_failure_to_the_caller()
        {
            var storage = new InMemoryStorage();
            using var vault = Open(Config(storage: storage));

            vault.Spend("coins", 10);
            storage.FailWrites = true;

            Assert.Throws<VaultStorageException>(() => Run(vault.FlushAsync()));
        }

        [Test]
        public void An_unreadable_save_fails_the_open_rather_than_starting_a_new_player()
        {
            // A read error is not the same as no save. Starting fresh would let the first write
            // overwrite a save that was only temporarily unreadable.
            var storage = new InMemoryStorage();
            storage.Seed(Player, "{\"schemaVersion\":1,\"playerId\":\"test-player\",\"balances\":[],\"claims\":[]}");
            storage.FailReads = true;

            var exception = Assert.Throws<VaultStorageException>(() => Open(Config(storage: storage)));

            Assert.AreEqual(Player, exception.PlayerId);
            Assert.IsTrue(storage.Has(Player), "the existing save must be left alone");
        }

        [Test]
        public void Two_players_do_not_share_a_save_file()
        {
            var storage = new InMemoryStorage();

            using (var first = Open(Config(storage: storage, playerId: "a/b")))
            {
                first.Spend("coins", 40);
                Run(first.FlushAsync());
            }

            using var second = Open(Config(storage: storage, playerId: "a_b"));

            Assert.AreEqual(100, second.GetBalance("coins"), "one player's spend must not show up in another's ledger");
        }

        [Test]
        public void A_save_belonging_to_a_different_player_is_refused()
        {
            // Backup check behind the file name: the document records which player it belongs to.
            var storage = new InMemoryStorage();
            storage.Seed(Player, "{\"schemaVersion\":1,\"playerId\":\"somebody-else\",\"balances\":[],\"claims\":[]}");

            var exception = Assert.Throws<InvalidOperationException>(() => Open(Config(storage: storage)));
            StringAssert.Contains("somebody-else", exception.Message);
        }

        [Test]
        public void Distinct_player_ids_map_to_distinct_file_names()
        {
            Assert.AreNotEqual(JsonFileStorage.FileNameFor("a/b"), JsonFileStorage.FileNameFor("a_b"));
            Assert.AreNotEqual(JsonFileStorage.FileNameFor("player@one.com"), JsonFileStorage.FileNameFor("player_one_com"));
            Assert.AreEqual(JsonFileStorage.FileNameFor("steady"), JsonFileStorage.FileNameFor("steady"),
                "the same id must always map to the same file");
        }
    }

    [TestFixture]
    internal sealed class ConfigurationTests
    {
        [Test]
        public void A_missing_player_id_is_rejected_at_construction()
        {
            var config = Config();
            config.PlayerId = null;

            Assert.Throws<ArgumentException>(() => new Vault(config));
        }

        [Test]
        public void A_malformed_api_url_is_rejected_at_construction()
        {
            var config = Config();
            config.ApiUrl = "not-a-url";

            Assert.Throws<ArgumentException>(() => new Vault(config));
        }

        [Test]
        public void Duplicate_resource_keys_are_rejected()
        {
            var config = Config(resources: new[]
            {
                new ResourceDefinition("coins"),
                new ResourceDefinition("coins")
            });

            Assert.Throws<ArgumentException>(() => new Vault(config));
        }

        [Test]
        public void An_initial_value_above_the_maximum_is_rejected()
        {
            var config = Config(resources: new[] { new ResourceDefinition("lives", initial: 10, max: 5) });

            Assert.Throws<ArgumentException>(() => new Vault(config));
        }

        [Test]
        public void A_vault_with_no_resources_and_no_opt_in_is_rejected()
        {
            var config = Config(resources: Array.Empty<ResourceDefinition>());

            Assert.Throws<ArgumentException>(() => new Vault(config));
        }

        [Test]
        public void Using_a_disposed_vault_is_an_error()
        {
            var vault = Open(Config());
            vault.Dispose();

            Assert.Throws<ObjectDisposedException>(() => vault.Spend("coins", 1));
        }
    }
}
