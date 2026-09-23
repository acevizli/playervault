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

        /// <param name="pretty">Indented for a person reading the file. Off when the document is
        /// sealed, where it is stored as one escaped string and indentation only adds noise.</param>
        public static string Serialize(VaultDocument document, bool pretty = true) => JsonUtility.ToJson(document, pretty);

        /// <summary>
        /// Parses a saved document. Returns false instead of throwing, so the caller can apply
        /// the corrupt-data policy.
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

            // JsonUtility returns an empty object for JSON with none of the expected fields,
            // so a missing player id means this is not a vault document.
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
            updatedAt = record.UpdatedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            lastStatusCode = record.LastStatusCode,
            lastError = record.LastError
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
                ParseTimestamp(entry.updatedAt),
                entry.lastStatusCode,
                entry.lastError);
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
        /// A basic check that a successful response contains a JSON object.
        /// </summary>
        /// <remarks>
        /// Unity's DownloadHandlerBuffer does not parse the body, so an HTML error page from a
        /// proxy can arrive as <c>UnityWebRequest.Result.Success</c>. Without this check, such
        /// a page would grant the reward. The check is kept simple and does not assume the
        /// sample endpoint's response format, since a game's own backend will respond
        /// differently.
        /// </remarks>
        public static bool LooksLikeJsonObject(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            var trimmed = body.TrimStart();
            return trimmed.Length > 0 && trimmed[0] == '{';
        }
    }
}
