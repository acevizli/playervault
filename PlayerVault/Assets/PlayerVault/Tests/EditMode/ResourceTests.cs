using NUnit.Framework;
using static PlayerVault.Tests.VaultTestHarness;

namespace PlayerVault.Tests
{
    [TestFixture]
    internal sealed class ResourceTests
    {
        [Test]
        public void Balances_start_at_their_declared_initial_value()
        {
            using var vault = Open(Config());

            Assert.AreEqual(100, vault.GetBalance("coins"));
            Assert.AreEqual(3, vault.GetBalance("lives"));
        }

        [Test]
        public void Unknown_resources_read_as_zero_rather_than_throwing()
        {
            using var vault = Open(Config());

            Assert.AreEqual(0, vault.GetBalance("gems"));
            Assert.AreEqual(0, vault.GetBalance(null));
        }

        [Test]
        public void Spending_reduces_the_balance()
        {
            using var vault = Open(Config());

            var result = vault.Spend("coins", 30);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(SpendFailure.None, result.Failure);
            Assert.AreEqual(70, result.Balance);
            Assert.AreEqual(70, vault.GetBalance("coins"));
        }

        [Test]
        public void Spending_more_than_the_balance_is_refused_and_changes_nothing()
        {
            using var vault = Open(Config());

            var result = vault.Spend("coins", 101);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(SpendFailure.InsufficientBalance, result.Failure);
            Assert.AreEqual(100, vault.GetBalance("coins"), "a refused spend must not move the balance");
        }

        [Test]
        public void Spending_the_entire_balance_is_allowed()
        {
            using var vault = Open(Config());

            Assert.IsTrue(vault.Spend("coins", 100).Success);
            Assert.AreEqual(0, vault.GetBalance("coins"));
        }

        [TestCase(0)]
        [TestCase(-5)]
        public void Non_positive_spends_are_refused(long amount)
        {
            using var vault = Open(Config());

            var result = vault.Spend("coins", amount);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(SpendFailure.InvalidAmount, result.Failure);
            Assert.AreEqual(100, vault.GetBalance("coins"));
        }

        [Test]
        public void Spending_an_undeclared_resource_is_refused()
        {
            using var vault = Open(Config());

            var result = vault.Spend("gems", 1);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(SpendFailure.UnknownResource, result.Failure);
        }

        [Test]
        public void CanSpend_agrees_with_Spend()
        {
            using var vault = Open(Config());

            Assert.IsTrue(vault.CanSpend("coins", 100));
            Assert.IsFalse(vault.CanSpend("coins", 101));
            Assert.IsFalse(vault.CanSpend("coins", 0));
            Assert.IsFalse(vault.CanSpend("gems", 1));
        }

        [Test]
        public void A_local_grant_is_clamped_by_the_resource_maximum()
        {
            using var vault = Open(Config());

            var result = vault.GrantLocal("lives", 10);   // 3 already, max 5

            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.AmountApplied, "only the headroom should land");
            Assert.AreEqual(5, vault.GetBalance("lives"));
        }

        [Test]
        public void A_local_grant_at_the_maximum_applies_nothing()
        {
            using var vault = Open(Config());
            vault.GrantLocal("lives", 10);

            var result = vault.GrantLocal("lives", 10);

            Assert.AreEqual(0, result.AmountApplied);
            Assert.AreEqual(5, vault.GetBalance("lives"));
        }

        [Test]
        public void Undeclared_resources_are_refused_by_default_and_allowed_when_opted_in()
        {
            using (var strict = Open(Config()))
                Assert.AreEqual(SpendFailure.UnknownResource, strict.GrantLocal("gems", 5).Failure);

            var config = Config();
            config.AllowUndeclaredResources = true;
            using var relaxed = Open(config);

            Assert.IsTrue(relaxed.GrantLocal("gems", 5).Success);
            Assert.AreEqual(5, relaxed.GetBalance("gems"));
        }
    }
}
