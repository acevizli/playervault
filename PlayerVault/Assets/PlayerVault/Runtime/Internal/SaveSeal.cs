using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PlayerVault.Internal
{
    /// <summary>
    /// The file written when tamper detection is on: the save document as a string, plus a
    /// number that goes up with every write and an HMAC over both.
    /// </summary>
    /// <remarks>
    /// The signature sits in the same file as the data, so the existing atomic rename covers
    /// both and there is never a moment where one has been written without the other.
    /// </remarks>
    [Serializable]
    public class SealedSave
    {
        public const int CurrentSchemaVersion = 2;

        public int schemaVersion;
        public string playerId;
        public long counter;
        public string mac;
        public string payload;
    }

    /// <summary>The outcome of checking a sealed save.</summary>
    public enum SealCheck
    {
        Valid,

        /// <summary>Not JSON. Handled like any other unreadable save.</summary>
        Unreadable,

        /// <summary>Written by a newer SDK, whose signature this one may not understand.</summary>
        NewerSchema,

        /// <summary>Signed for, or saved by, another player.</summary>
        WrongPlayer,

        Unsigned,
        SignatureMismatch,
        RolledBack
    }

    /// <summary>
    /// Signs saves on write and checks them on load, using a key and counter from
    /// <see cref="IVaultKeyStore"/>. Not replaceable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HMAC-SHA256 rather than a public-key signature: the same device writes the save and
    /// checks it, so there is nobody who needs to check without being able to sign.
    /// </para>
    /// <para>
    /// The signature alone does not stop a player putting back an older copy of the save, which
    /// is still correctly signed. That would undo a claim and let the reward be claimed again.
    /// The counter catches it: the key store keeps the number of the last save written, and a
    /// save with a lower number was put back.
    /// </para>
    /// <para>
    /// The vault writes the file first and records the counter second. A crash between the two
    /// leaves the file one ahead of the key store, which is accepted, so an honest crash never
    /// looks like tampering. <see cref="Seal"/> is only called while the vault holds its write
    /// lock, so numbers are issued in the order files are written.
    /// </para>
    /// </remarks>
    public sealed class SaveSeal
    {
        readonly string _playerId;
        readonly byte[] _key;

        long _counter;

        public SaveSeal(string playerId, byte[] key, long storedCounter)
        {
            if (key == null || key.Length < 32)
                throw new ArgumentException("A save signing key must be at least 32 bytes.", nameof(key));

            _playerId = playerId;
            _key = key;
            StoredCounter = storedCounter;
            _counter = storedCounter;
        }

        /// <summary>The counter the key store held when the vault opened.</summary>
        public long StoredCounter { get; }

        /// <summary>The number of the newest save read or written.</summary>
        public long Counter => _counter;

        /// <summary>
        /// Checks a stored file. On <see cref="SealCheck.Valid"/>, <paramref name="payload"/> is
        /// the save document. <paramref name="envelope"/> is whatever parsed, for error messages.
        /// </summary>
        public SealCheck Open(string stored, out string payload, out SealedSave envelope)
        {
            payload = null;

            if (!TryParse(stored, out envelope)) return SealCheck.Unreadable;

            if (envelope.schemaVersion > SealedSave.CurrentSchemaVersion) return SealCheck.NewerSchema;

            // Checked before the signature so a save copied from another player is reported as
            // that, the same as with tamper detection off.
            if (!string.IsNullOrEmpty(envelope.playerId) &&
                !string.Equals(envelope.playerId, _playerId, StringComparison.Ordinal))
                return SealCheck.WrongPlayer;

            if (envelope.schemaVersion < SealedSave.CurrentSchemaVersion ||
                string.IsNullOrEmpty(envelope.mac) || envelope.payload == null)
                return SealCheck.Unsigned;

            byte[] expected = Mac(envelope.counter, envelope.payload);
            byte[] actual;
            try { actual = Convert.FromBase64String(envelope.mac); }
            catch (FormatException) { return SealCheck.SignatureMismatch; }

            if (!FixedTimeEquals(expected, actual)) return SealCheck.SignatureMismatch;

            if (envelope.counter < StoredCounter) return SealCheck.RolledBack;

            _counter = Math.Max(_counter, envelope.counter);
            payload = envelope.payload;
            return SealCheck.Valid;
        }

        /// <summary>Wraps a save document for writing, under the next counter.</summary>
        public string Seal(string payload, out long counter)
        {
            counter = ++_counter;

            var envelope = new SealedSave
            {
                schemaVersion = SealedSave.CurrentSchemaVersion,
                playerId = _playerId,
                counter = counter,
                mac = Convert.ToBase64String(Mac(counter, payload)),
                payload = payload
            };

            return JsonUtility.ToJson(envelope, true);
        }

        /// <summary>
        /// With tamper detection off, reads the document out of a sealed file without checking it,
        /// so turning detection off does not lose an existing save. Anything else is returned as is.
        /// </summary>
        public static string Unwrap(string stored)
        {
            return TryParse(stored, out var envelope) &&
                   envelope.schemaVersion == SealedSave.CurrentSchemaVersion &&
                   !string.IsNullOrEmpty(envelope.mac) && envelope.payload != null
                ? envelope.payload
                : stored;
        }

        static bool TryParse(string stored, out SealedSave envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(stored)) return false;

            try { envelope = JsonUtility.FromJson<SealedSave>(stored); }
            catch (Exception) { return false; }

            return envelope != null;
        }

        /// <summary>
        /// Everything that gives the save its meaning is signed. The player id is length-prefixed
        /// so no id and payload can be shifted into a different id and payload with the same bytes.
        /// </summary>
        byte[] Mac(long counter, string payload)
        {
            var message = string.Concat(
                "playervault-save\n",
                SealedSave.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture), "\n",
                _playerId.Length.ToString(CultureInfo.InvariantCulture), ":", _playerId, "\n",
                counter.ToString(CultureInfo.InvariantCulture), "\n",
                payload);

            using var hmac = new HMACSHA256(_key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        }

        /// <summary>Compares without stopping at the first difference, so timing does not reveal how much matched.</summary>
        static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;

            var difference = 0;
            for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
