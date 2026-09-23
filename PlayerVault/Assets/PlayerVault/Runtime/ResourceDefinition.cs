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
    }
}
