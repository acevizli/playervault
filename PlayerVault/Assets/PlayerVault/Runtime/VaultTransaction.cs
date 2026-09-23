using System;
using System.Collections.Generic;

namespace PlayerVault
{
    /// <summary>
    /// Several resource changes that are applied together or not at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The main use is a purchase: take the coins and give the item. As two separate calls
    /// this is two writes, and if the process is killed between them the player has paid and
    /// received nothing. <see cref="Vault.TransactAsync"/> checks every step, applies them
    /// together and saves them in one write.
    /// </para>
    /// <para>
    /// Steps run in order against the running balance, so
    /// <c>Spend("coins", 50).Grant("coins", 10)</c> needs 50 coins, not 40. If a spend fails,
    /// the whole transaction fails. A grant clamped by the resource maximum does not fail;
    /// the clamped amount is reported in <see cref="TransactionResult.Changes"/>, the same as
    /// <see cref="Vault.GrantLocal"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var result = await vault.TransactAsync(
    ///     new VaultTransaction()
    ///         .Spend("coins", 250)
    ///         .GrantExact("level-3-unlocked", 1));
    /// </code>
    /// </example>
    public sealed class VaultTransaction
    {
        internal enum Kind
        {
            Spend,
            Grant,

            /// <summary>A grant that must be applied in full, or the transaction fails.</summary>
            GrantExact
        }

        internal readonly struct Step
        {
            public readonly string Resource;
            public readonly long Amount;
            public readonly Kind Kind;

            public Step(string resource, long amount, Kind kind)
            {
                Resource = resource;
                Amount = amount;
                Kind = kind;
            }
        }

        readonly List<Step> _steps = new List<Step>();

        internal IReadOnlyList<Step> Steps => _steps;

        public int Count => _steps.Count;

        /// <summary>Subtracts from a balance. The whole transaction fails if the balance is too low.</summary>
        public VaultTransaction Spend(string resource, long amount)
        {
            _steps.Add(new Step(resource, amount, Kind.Spend));
            return this;
        }

        /// <summary>Adds to a balance, clamped to the resource maximum.</summary>
        public VaultTransaction Grant(string resource, long amount)
        {
            _steps.Add(new Step(resource, amount, Kind.Grant));
            return this;
        }

        /// <summary>
        /// Adds to a balance. The whole transaction fails with
        /// <see cref="SpendFailure.AtMaximum"/> if the maximum would clamp any of the amount.
        /// </summary>
        /// <remarks>
        /// Use this for purchases, so the player is not charged for an item they have no room
        /// for. For example, an item stored as a resource with a maximum of 1 cannot be bought
        /// twice.
        /// </remarks>
        public VaultTransaction GrantExact(string resource, long amount)
        {
            _steps.Add(new Step(resource, amount, Kind.GrantExact));
            return this;
        }

        /// <summary>
        /// Shortcut for a purchase: spend the price and grant the item. The item uses
        /// <see cref="GrantExact"/>, so a player who already owns it is refused and not charged.
        /// </summary>
        public static VaultTransaction Purchase(string priceResource, long price, string itemResource, long quantity = 1) =>
            new VaultTransaction().Spend(priceResource, price).GrantExact(itemResource, quantity);

        public override string ToString()
        {
            if (_steps.Count == 0) return "VaultTransaction(empty)";

            var parts = new string[_steps.Count];
            for (var i = 0; i < _steps.Count; i++)
                parts[i] = $"{(_steps[i].Kind == Kind.Spend ? "-" : "+")}{_steps[i].Amount} {_steps[i].Resource}";

            return "VaultTransaction(" + string.Join(", ", parts) + ")";
        }
    }

    /// <summary>
    /// Thrown when the vault cannot read or write its durable state.
    /// </summary>
    /// <remarks>
    /// Thrown instead of logged. A failed read treated as a new player could lead to the save
    /// being overwritten, and a failed write reported as success could let the game give out
    /// items that are lost on restart.
    /// </remarks>
    public sealed class VaultStorageException : Exception
    {
        /// <summary>The player whose state could not be read or written.</summary>
        public string PlayerId { get; }

        public VaultStorageException(string playerId, string message, Exception inner)
            : base(message, inner)
        {
            PlayerId = playerId;
        }
    }
}
