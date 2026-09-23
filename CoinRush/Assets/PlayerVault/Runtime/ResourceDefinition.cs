using System;

namespace PlayerVault
{
    /// <summary>
    /// Declares a resource the vault manages, such as coins or lives.
    /// </summary>
    [Serializable]
    public sealed class ResourceDefinition
    {
        /// <summary>The key used at every API boundary. Case-sensitive.</summary>
        public string Key { get; set; }

        /// <summary>Balance a brand-new player starts with.</summary>
        public long Initial { get; set; }

        /// <summary>
        /// Optional maximum. Null means no limit. A claim that would go over the maximum is
        /// clamped instead of rejected, because the backend has already accepted it.
        /// </summary>
        public long? Max { get; set; }

        public ResourceDefinition() { }

        public ResourceDefinition(string key, long initial = 0, long? max = null)
        {
            Key = key;
            Initial = initial;
            Max = max;
        }

        /// <summary>
        /// Lets a definition stand in for its key, so a resource declared once in code can be
        /// passed to every API instead of repeating its key as a string.
        /// </summary>
        /// <example>
        /// <code>
        /// static readonly ResourceDefinition Coins = new ResourceDefinition("coins", initial: 100);
        ///
        /// config.Resources.Add(Coins);
        /// vault.Spend(Coins, 30);
        /// </code>
        /// </example>
        public static implicit operator string(ResourceDefinition definition) => definition?.Key;

        public override string ToString() => Key;
    }
}
