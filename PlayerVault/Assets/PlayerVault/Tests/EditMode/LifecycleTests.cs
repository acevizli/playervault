using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static PlayerVault.Tests.VaultTestHarness;
using Object = UnityEngine.Object;

namespace PlayerVault.Tests
{
    /// <summary>
    /// One vault per save, closing, sharing between components, and deleting a save.
    /// </summary>
    [TestFixture]
    internal sealed class LifecycleTests
    {
        // ------------------------------------------------------------ one vault per save

        [Test]
        public void A_second_vault_on_an_open_save_is_refused()
        {
            var storage = new InMemoryStorage();
            using var first = Open(Config(storage: storage));

            var exception = Assert.Throws<InvalidOperationException>(() => Open(Config(storage: storage)));
            StringAssert.Contains("already open", exception.Message);
        }

        [Test]
        public void Two_file_storages_on_one_folder_are_the_same_save()
        {
            var folder = TemporaryFolder();
            try
            {
                using var first = Open(Config(storage: new JsonFileStorage(folder)));

                Assert.Throws<InvalidOperationException>(() => Open(Config(storage: new JsonFileStorage(folder))),
                    "separate storage objects writing one file must still count as one save");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void Different_players_on_one_storage_open_side_by_side()
        {
            var storage = new InMemoryStorage();
            using var first = Open(Config(storage: storage, playerId: "a"));

            Assert.DoesNotThrow(() => Open(Config(storage: storage, playerId: "b")).Dispose());
        }

        [Test]
        public void A_save_can_be_opened_again_once_closed()
        {
            var storage = new InMemoryStorage();

            var first = Open(Config(storage: storage));
            first.Spend("coins", 30);
            Run(first.CloseAsync());

            using var second = Open(Config(storage: storage));
            Assert.AreEqual(70, second.GetBalance("coins"));
        }

        [Test]
        public void Reopening_after_dispose_waits_for_the_write_still_running()
        {
            // The scene-change case: the old scene's vault is disposed while its last write is
            // still on its way to disk, and the new scene opens the same save straight away.
            var storage = new InMemoryStorage { CompleteAsynchronously = true };
            var writing = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();

            var old = Open(Config(storage: storage));
            storage.OnWrite = _ =>
            {
                writing.Set();
                release.Wait(5000);
            };

            old.Spend("coins", 30);
            Assert.IsTrue(writing.Wait(5000), "the write should have started");

            old.Dispose();
            var reopening = Vault.OpenAsync(Config(storage: storage));

            Assert.IsFalse(reopening.Wait(200), "the new vault must not load while the old one is still writing");

            storage.OnWrite = null;
            release.Set();

            using var reopened = Run(reopening);
            Assert.AreEqual(70, reopened.GetBalance("coins"), "the new vault must see the old one's last write");
        }

        // ------------------------------------------------------------ deleting a save

        [Test]
        public void Deleting_a_save_starts_the_player_over()
        {
            var storage = new InMemoryStorage();

            var vault = Open(Config(storage: storage));
            vault.Spend("coins", 30);
            Run(vault.ClaimAsync("level-1", "coins", 50));
            Run(vault.CloseAsync());

            Run(Vault.DeleteSaveAsync(Player, storage, KeyStoreFor(storage)));

            using var fresh = Open(Config(storage: storage));
            Assert.AreEqual(100, fresh.GetBalance("coins"));
            Assert.IsNull(fresh.GetClaim("level-1"), "a deleted save forgets its claims, so the reward can be claimed again");
        }

        [Test]
        public void Deleting_the_save_of_an_open_vault_is_refused()
        {
            var storage = new InMemoryStorage();
            using var vault = Open(Config(storage: storage));
            vault.Spend("coins", 30);
            Run(vault.FlushAsync());

            Assert.Throws<InvalidOperationException>(() => Run(Vault.DeleteSaveAsync(Player, storage, KeyStoreFor(storage))));
            Assert.IsTrue(storage.Has(Player), "the save must be left alone");
        }

        [Test]
        public void Deleting_a_file_save_removes_quarantined_copies_too()
        {
            var folder = TemporaryFolder();
            try
            {
                var storage = new JsonFileStorage(folder);
                Run(storage.WriteAsync(Player, "not json"));
                Run(storage.QuarantineAsync(Player));
                Run(storage.WriteAsync(Player, "{}"));

                Assert.AreEqual(2, Directory.GetFiles(folder).Length, "a save and a quarantined copy");

                Run(Vault.DeleteSaveAsync(Player, storage, KeyStoreFor(storage)));

                Assert.IsEmpty(Directory.GetFiles(folder));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void A_storage_without_delete_says_so()
        {
            Assert.Throws<NotSupportedException>(() => Run(Vault.DeleteSaveAsync(Player, new ReadWriteOnlyStorage())));
        }

        // ------------------------------------------------------------ VaultBehaviour

        [Test]
        public void Components_for_one_player_share_one_vault()
        {
            var config = BehaviourConfig();
            var first = CreateBehaviour();
            var second = CreateBehaviour();

            try
            {
                Run(first.OpenAsync(config));
                Run(second.OpenAsync(BehaviourConfig(config.PlayerId, config.Storage)));

                Assert.IsNotNull(first.Vault);
                Assert.AreSame(first.Vault, second.Vault, "the second component must not open the save again");
            }
            finally
            {
                Cleanup(first, second);
            }
        }

        [Test]
        public void WhenOpen_runs_at_once_for_an_open_vault_and_on_open_otherwise()
        {
            var behaviour = CreateBehaviour();

            try
            {
                Vault early = null, late = null;

                behaviour.WhenOpen(vault => early = vault);
                Assert.IsNull(early, "nothing to run before the open");

                Run(behaviour.OpenAsync(BehaviourConfig()));
                Assert.IsNotNull(early, "runs when the vault opens");

                behaviour.WhenOpen(vault => late = vault);
                Assert.AreSame(early, late, "runs immediately for a vault that is already open");
            }
            finally
            {
                Cleanup(behaviour);
            }
        }

        [Test]
        public void WhenOpen_reports_a_failed_open_and_still_runs_after_a_retry()
        {
            LogAssert.ignoreFailingMessages = true;   // the failure is logged by design

            var storage = new InMemoryStorage { FailReads = true };
            var config = BehaviourConfig(storage: storage);
            var behaviour = CreateBehaviour();

            try
            {
                Vault opened = null;
                var failures = 0;
                behaviour.WhenOpen(vault => opened = vault, _ => failures++);

                Assert.Throws<VaultStorageException>(() => Run(behaviour.OpenAsync(config)));
                Assert.AreEqual(1, failures);
                Assert.IsTrue(behaviour.HasFailedToOpen);
                Assert.IsTrue(behaviour.WhenOpenAsync().IsFaulted, "an awaiter must not wait forever on a failed open");

                storage.FailReads = false;
                Run(behaviour.OpenAsync(BehaviourConfig(config.PlayerId, storage)));

                Assert.IsNotNull(opened, "the hook keeps waiting, so a successful retry still runs it");
                Assert.AreSame(opened, Run(behaviour.WhenOpenAsync()));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Cleanup(behaviour);
            }
        }

        [Test]
        public void A_component_cannot_open_a_save_already_open_in_code()
        {
            LogAssert.ignoreFailingMessages = true;

            var config = BehaviourConfig();
            using var inCode = Open(BehaviourConfig(config.PlayerId, config.Storage));
            var behaviour = CreateBehaviour();

            try
            {
                Assert.Throws<InvalidOperationException>(() => Run(behaviour.OpenAsync(config)));
                Assert.IsInstanceOf<InvalidOperationException>(behaviour.OpenError);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Cleanup(behaviour);
            }
        }

        [Test]
        public void A_throwing_Opened_subscriber_does_not_fail_the_open_or_block_the_others()
        {
            LogAssert.ignoreFailingMessages = true;   // the broken handler is logged

            var behaviour = CreateBehaviour();

            try
            {
                var second = false;
                behaviour.Opened += _ => throw new InvalidOperationException("a broken HUD");
                behaviour.Opened += _ => second = true;
                var waiting = behaviour.WhenOpenAsync();

                Assert.DoesNotThrow(() => Run(behaviour.OpenAsync(BehaviourConfig())));

                Assert.IsTrue(second, "later subscribers still run");
                Assert.IsTrue(waiting.IsCompleted && !waiting.IsFaulted, "WhenOpenAsync still completes");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Cleanup(behaviour);
            }
        }

        [Test]
        public void A_component_opening_while_the_last_scenes_vault_closes_can_use_the_default_services()
        {
            // The scene-change case. The next scene's component waits for the old vault's final
            // write and resumes on a worker thread. Creating the default JsonFileStorage there
            // threw, because Application.persistentDataPath is main-thread only.
            var playerId = "behaviour-" + Guid.NewGuid().ToString("N");
            var storage = new InMemoryStorage();
            var old = CreateBehaviour();
            var next = CreateBehaviour();
            Vault opened = null;

            try
            {
                Run(old.OpenAsync(BehaviourConfig(playerId, storage)));

                var held = new TaskCompletionSource<bool>();
                storage.Hold = _ => held.Task;

                // OnDestroy does not run in edit mode, so call it: the last user leaving closes
                // the vault, and its final write is held.
                typeof(VaultBehaviour)
                    .GetMethod("OnDestroy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(old, null);

                var config = BehaviourConfig(playerId);
                config.Transport = null;
                config.Storage = null;
                config.KeyStore = null;

                var opening = next.OpenAsync(config);
                Assert.IsFalse(opening.IsCompleted, "the open waits for the old vault's final write");

                held.SetResult(true);
                opened = Run(opening);

                Assert.IsNotNull(opened);
            }
            finally
            {
                storage.Hold = null;
                opened?.Dispose();
                Cleanup(old, next);
                Run(Vault.DeleteSaveAsync(playerId));
            }
        }

        [Test]
        public void A_vault_with_default_services_refuses_to_be_created_off_the_main_thread()
        {
            var config = Config();
            config.Storage = null;

            var exception = Assert.Throws<InvalidOperationException>(() => Run(Task.Run(() => Vault.OpenAsync(config))));
            StringAssert.Contains("main thread", exception.Message);
        }

        // ------------------------------------------------------------ helpers

        static string TemporaryFolder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "playervault-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Components share vaults by player id for as long as the editor runs, so every test
        /// uses its own player.
        /// </summary>
        static VaultConfig BehaviourConfig(string playerId = null, IVaultStorage storage = null) =>
            Config(storage: storage ?? new InMemoryStorage(), playerId: playerId ?? "behaviour-" + Guid.NewGuid().ToString("N"));

        static VaultBehaviour CreateBehaviour()
        {
            // Inactive, so Awake does not open a vault from the Inspector defaults.
            var gameObject = new GameObject("VaultBehaviour test") { hideFlags = HideFlags.HideAndDontSave };
            gameObject.SetActive(false);
            return gameObject.AddComponent<VaultBehaviour>();
        }

        /// <summary>
        /// OnDestroy does not run in edit mode, so the vault is disposed here directly.
        /// </summary>
        static void Cleanup(params VaultBehaviour[] behaviours)
        {
            foreach (var behaviour in behaviours)
            {
                behaviour.Vault?.Dispose();
                Object.DestroyImmediate(behaviour.gameObject);
            }
        }

        /// <summary>A storage written before DeleteAsync existed.</summary>
        sealed class ReadWriteOnlyStorage : IVaultStorage
        {
            public Task<string> ReadAsync(string key, CancellationToken cancellationToken = default) =>
                Task.FromResult<string>(null);

            public Task WriteAsync(string key, string payload, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task QuarantineAsync(string key, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
