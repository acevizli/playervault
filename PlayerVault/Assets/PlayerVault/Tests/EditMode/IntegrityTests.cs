using System;
using System.IO;
using NUnit.Framework;
using PlayerVault.Internal;
using UnityEngine;
using static PlayerVault.Tests.VaultTestHarness;

namespace PlayerVault.Tests
{
    /// <summary>
    /// Tamper detection. The storage stands in for the file a player can reach; the key store
    /// stands in for the Keychain or Keystore they cannot.
    /// </summary>
    [TestFixture]
    internal sealed class IntegrityTests
    {
        [Test]
        public void A_signed_save_reopens_and_advances_the_counter()
        {
            var storage = new InMemoryStorage();

            using (var first = Open(Config(storage: storage)))
            {
                first.Spend("coins", 40);
                Run(first.FlushAsync());
            }

            var envelope = Envelope(storage);
            Assert.AreEqual(SealedSave.CurrentSchemaVersion, envelope.schemaVersion);
            Assert.IsNotEmpty(envelope.mac);
            Assert.AreEqual(envelope.counter, KeyStoreFor(storage).Counter(Player),
                "the key store records the number of the last save written");

            using var second = Open(Config(storage: storage));
            Assert.AreEqual(60, second.GetBalance("coins"));
        }

        [Test]
        public void An_edited_balance_blocks_the_open_and_leaves_the_save_in_place()
        {
            var storage = SavedWithSpend();
            var envelope = Envelope(storage);
            envelope.payload = envelope.payload.Replace("\"value\":60", "\"value\":99999");
            Assert.AreNotEqual(Envelope(storage).payload, envelope.payload, "the edit must change something");
            storage.Seed(Player, JsonUtility.ToJson(envelope));

            var exception = Assert.Throws<VaultTamperedException>(() => Open(Config(storage: storage)));

            Assert.AreEqual(TamperReason.SignatureMismatch, exception.Reason);
            Assert.AreEqual(Player, exception.PlayerId);
            Assert.IsTrue(storage.Has(Player), "a blocked save stays, so the next launch is blocked too");
            Assert.Throws<VaultTamperedException>(() => Open(Config(storage: storage)), "blocked again on relaunch");
        }

        [Test]
        public void A_changed_counter_is_detected()
        {
            // Raising the number would let a copy outrank the real save; it is signed, so it cannot be.
            var storage = SavedWithSpend();
            var envelope = Envelope(storage);
            envelope.counter += 100;
            storage.Seed(Player, JsonUtility.ToJson(envelope));

            var exception = Assert.Throws<VaultTamperedException>(() => Open(Config(storage: storage)));
            Assert.AreEqual(TamperReason.SignatureMismatch, exception.Reason);
        }

        [Test]
        public void A_changed_mac_is_detected()
        {
            var storage = SavedWithSpend();
            var envelope = Envelope(storage);
            envelope.mac = "not base64 at all";
            storage.Seed(Player, JsonUtility.ToJson(envelope));

            var exception = Assert.Throws<VaultTamperedException>(() => Open(Config(storage: storage)));
            Assert.AreEqual(TamperReason.SignatureMismatch, exception.Reason);
        }

        [Test]
        public void Another_players_save_is_refused()
        {
            var storage = new InMemoryStorage();

            using (var other = Open(Config(storage: storage, playerId: "rich-player")))
            {
                Run(other.GrantAsync("coins", 500));
            }

            var copied = storage.Read("rich-player");

            // Copied as is: the file says whose it is.
            storage.Seed(Player, copied);
            Assert.Throws<InvalidOperationException>(() => Open(Config(storage: storage)));

            // Relabelled to look like this player's: the signature covers the player id.
            var envelope = JsonUtility.FromJson<SealedSave>(copied);
            envelope.playerId = Player;
            storage.Seed(Player, JsonUtility.ToJson(envelope));

            var exception = Assert.Throws<VaultTamperedException>(() => Open(Config(storage: storage)));
            Assert.AreEqual(TamperReason.SignatureMismatch, exception.Reason);
        }

        [Test]
        public void Putting_back_an_older_save_cannot_claim_a_reward_twice()
        {
            var storage = new InMemoryStorage();

            using (var vault = Open(Config(storage: storage)))
            {
                Run(vault.FlushAsync());
            }

            var beforeClaim = storage.Read(Player);

            using (var vault = Open(Config(storage: storage)))
            {
                Assert.AreEqual(ClaimStatus.Granted, Run(vault.ClaimAsync("level-10", "coins", 100)).Status);
            }

            storage.Seed(Player, beforeClaim);

            var exception = Assert.Throws<VaultTamperedException>(() => Open(Config(storage: storage)));
            Assert.AreEqual(TamperReason.RolledBack, exception.Reason);
        }

        [Test]
        public void An_unsigned_save_is_treated_as_tampered()
        {
            var storage = new InMemoryStorage();
            storage.Seed(Player,
                "{\"schemaVersion\":1,\"playerId\":\"test-player\",\"balances\":[{\"key\":\"coins\",\"value\":99999}],\"claims\":[]}");

            var exception = Assert.Throws<VaultTamperedException>(() => Open(Config(storage: storage)));
            Assert.AreEqual(TamperReason.Unsigned, exception.Reason);
        }

        [Test]
        public void A_missing_save_is_a_new_player_and_the_counter_keeps_counting()
        {
            // Deleting the save only throws away the player's own progress. The counter is not
            // reset, so a save taken before the delete still counts as old afterwards.
            var storage = SavedWithSpend();
            var old = storage.Read(Player);
            var before = KeyStoreFor(storage).Counter(Player);

            Run(storage.DeleteAsync(Player));

            using (var vault = Open(Config(storage: storage)))
            {
                Assert.AreEqual(100, vault.GetBalance("coins"));
                vault.Spend("coins", 1);
                Run(vault.FlushAsync());
            }

            Assert.Greater(Envelope(storage).counter, before);
            Assert.Greater(KeyStoreFor(storage).Counter(Player), before);

            storage.Seed(Player, old);
            Assert.Throws<VaultTamperedException>(() => Open(Config(storage: storage)));
        }

        [Test]
        public void Stopping_between_the_save_and_the_counter_is_not_tampering()
        {
            var storage = new InMemoryStorage();
            var keys = KeyStoreFor(storage);

            using (var vault = Open(Config(storage: storage)))
            {
                vault.Spend("coins", 10);
                Run(vault.FlushAsync());

                keys.DropCounterWrites = true;
                vault.Spend("coins", 10);
                Run(vault.FlushAsync());
            }

            keys.DropCounterWrites = false;
            Assert.Less(keys.Counter(Player), Envelope(storage).counter, "the file is ahead of the key store");

            using (var vault = Open(Config(storage: storage)))
            {
                Assert.AreEqual(80, vault.GetBalance("coins"));
            }

            Assert.AreEqual(Envelope(storage).counter, keys.Counter(Player), "opening catches the key store up");
        }

        [Test]
        public void Quarantine_policy_moves_a_tampered_save_aside_and_starts_fresh()
        {
            var storage = SavedWithSpend();
            var envelope = Envelope(storage);
            envelope.payload = envelope.payload.Replace("\"value\":60", "\"value\":99999");
            storage.Seed(Player, JsonUtility.ToJson(envelope));

            var logger = new NullLogger();
            var config = Config(storage: storage, logger: logger);
            config.OnTampered = TamperedDataPolicy.Quarantine;

            using (var vault = Open(config))
            {
                Assert.AreEqual(100, vault.GetBalance("coins"));
                vault.Spend("coins", 5);
                Run(vault.FlushAsync());
            }

            Assert.AreEqual(1, storage.Quarantined.Count);

            using var reopened = Open(Config(storage: storage));
            Assert.AreEqual(95, reopened.GetBalance("coins"), "the fresh save is signed and loads normally");
        }

        [Test]
        public void An_unavailable_key_store_fails_the_open_without_touching_the_save()
        {
            var storage = SavedWithSpend();
            var saved = storage.Read(Player);
            KeyStoreFor(storage).Fail = true;

            Assert.Throws<VaultStorageException>(() => Open(Config(storage: storage)));
            Assert.AreEqual(saved, storage.Read(Player));

            KeyStoreFor(storage).Fail = false;
            using var vault = Open(Config(storage: storage));
            Assert.AreEqual(60, vault.GetBalance("coins"), "opening can be retried");
        }

        [Test]
        public void With_detection_off_saves_are_plain_and_signed_saves_still_load()
        {
            var storage = SavedWithSpend();

            var config = Config(storage: storage);
            config.DetectTampering = false;

            using (var vault = Open(config))
            {
                Assert.AreEqual(60, vault.GetBalance("coins"), "turning detection off keeps an existing save");
                vault.Spend("coins", 10);
                Run(vault.FlushAsync());
            }

            StringAssert.DoesNotContain("\"mac\"", storage.Read(Player));
            StringAssert.Contains("\"playerId\"", storage.Read(Player));
        }

        [Test]
        public void Deleting_the_save_also_clears_the_key_store()
        {
            var storage = SavedWithSpend();
            var keys = KeyStoreFor(storage);

            Run(Vault.DeleteSaveAsync(Player, storage, keys));

            Assert.IsFalse(keys.HasKey(Player));
            Assert.AreEqual(0, keys.Counter(Player));

            using var vault = Open(Config(storage: storage));
            Assert.AreEqual(100, vault.GetBalance("coins"));
        }

        [Test]
        public void The_file_key_store_keeps_its_key_and_never_lowers_the_counter()
        {
            var folder = Path.Combine(Path.GetTempPath(), "playervault-keys-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileKeyStore(folder);
                var key = Run(store.GetOrCreateKeyAsync(Player));

                Assert.AreEqual(32, key.Length);
                CollectionAssert.AreEqual(key, Run(new FileKeyStore(folder).GetOrCreateKeyAsync(Player)),
                    "a second instance, like a relaunch, finds the same key");
                CollectionAssert.AreNotEqual(key, Run(store.GetOrCreateKeyAsync("someone-else")));

                Run(store.WriteCounterAsync(Player, 7));
                Run(store.WriteCounterAsync(Player, 3));
                Assert.AreEqual(7, Run(store.ReadCounterAsync(Player)));

                Run(store.DeleteAsync(Player));
                Assert.AreEqual(0, Run(store.ReadCounterAsync(Player)));
                CollectionAssert.AreNotEqual(key, Run(store.GetOrCreateKeyAsync(Player)));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void An_edited_file_save_is_detected()
        {
            // The same flow as editing the JSON in a text editor.
            var folder = Path.Combine(Path.GetTempPath(), "playervault-" + Guid.NewGuid().ToString("N"));
            try
            {
                var storage = new JsonFileStorage(Path.Combine(folder, "saves"));
                var config = Config(storage: storage);
                config.KeyStore = new FileKeyStore(Path.Combine(folder, "keys"));

                using (var vault = Open(config))
                {
                    vault.Spend("coins", 40);
                    Run(vault.CloseAsync());
                }

                var path = storage.PathFor(Player);
                var text = File.ReadAllText(path);
                StringAssert.Contains("\\\"value\\\":60", text, "the balance is readable, and so editable");
                File.WriteAllText(path, text.Replace("\\\"value\\\":60", "\\\"value\\\":61"));

                config = Config(storage: storage);
                config.KeyStore = new FileKeyStore(Path.Combine(folder, "keys"));
                Assert.Throws<VaultTamperedException>(() => Open(config));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        static InMemoryStorage SavedWithSpend()
        {
            var storage = new InMemoryStorage();
            using var vault = Open(Config(storage: storage));
            vault.Spend("coins", 40);
            Run(vault.FlushAsync());
            return storage;
        }

        static SealedSave Envelope(InMemoryStorage storage) => JsonUtility.FromJson<SealedSave>(storage.Read(Player));
    }
}
