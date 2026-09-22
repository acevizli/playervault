using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace PlayerVault.Internal
{
    /// <summary>
    /// Translates between the in-memory model and the on-disk document.
    /// </summary>
    public static class VaultSerializer
    {
        const string TimestampFormat = "o";

        public static string Serialize(VaultDocument document) => JsonUtility.ToJson(document, true);

        /// <summary>
        /// Parses a stored document. Returns false rather than throwing, so the caller can
        /// apply the configured corruption policy.
        /// </summary>
        public static bool TryDeserialize(string payload, out VaultDocument document, out string error)
        {
            document = null;
            error = null;

            if (string.IsNullOrWhiteSpace(payload))
            {
                error = "payload was empty";
                return false;
            }

            try
            {
                document = JsonUtility.FromJson<VaultDocument>(payload);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            if (document == null)
            {
                error = "payload did not parse to a document";
                return false;
            }

            // JsonUtility happily returns a default-constructed object for JSON that
            // contains none of the expected fields, so absence of a player id is the
            // signal that this was not one of our documents.
            if (string.IsNullOrEmpty(document.playerId))
            {
                error = "document has no playerId";
                return false;
            }

            document.balances ??= new List<BalanceEntry>();
            document.claims ??= new List<ClaimEntry>();
            return true;
        }

        public static ClaimEntry ToEntry(ClaimRecord record) => new ClaimEntry
        {
            rewardId = record.RewardId,
            resource = record.Resource,
            amountRequested = record.AmountRequested,
            amountApplied = record.AmountApplied,
            status = record.Status.ToString(),
            failure = record.Failure.ToString(),
            attempts = record.Attempts,
            createdAt = record.CreatedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            updatedAt = record.UpdatedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture)
        };

        public static ClaimRecord ToRecord(ClaimEntry entry)
        {
            if (!Enum.TryParse(entry.status, out ClaimStatus status))
                status = ClaimStatus.Pending;

            if (!Enum.TryParse(entry.failure, out ClaimFailure failure))
                failure = ClaimFailure.None;

            return new ClaimRecord(
                entry.rewardId,
                entry.resource,
                entry.amountRequested,
                entry.amountApplied,
                status,
                failure,
                entry.attempts,
                ParseTimestamp(entry.createdAt),
                ParseTimestamp(entry.updatedAt));
        }

        static DateTimeOffset ParseTimestamp(string value)
        {
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : default;
        }

        /// <summary>
        /// A minimal check that a successful response actually carried a JSON object.
        /// </summary>
        /// <remarks>
        /// This exists because of a specific trap: Unity's DownloadHandlerBuffer does no
        /// parsing, so an HTML error page from a proxy arrives as
        /// <c>UnityWebRequest.Result.Success</c>. Without this check the SDK would grant a
        /// reward on the strength of a 502 page. The check is deliberately shallow — it
        /// must not assume the sample endpoint's echo shape, because a game swapping in its
        /// own backend will return something else entirely.
        /// </remarks>
        public static bool LooksLikeJsonObject(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            var trimmed = body.TrimStart();
            return trimmed.Length > 0 && trimmed[0] == '{';
        }
    }
}
