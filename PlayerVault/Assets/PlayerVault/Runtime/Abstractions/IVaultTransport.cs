using System.Threading;
using System.Threading.Tasks;

namespace PlayerVault
{
    /// <summary>
    /// How the SDK talks to a reward backend.
    /// </summary>
    /// <remarks>
    /// This is deliberately the narrowest interface in the SDK: one method, no Unity
    /// types. A fake is about ten lines, which is what makes the whole test suite
    /// runnable without a network. Substituting a real implementation is how a game
    /// points PlayerVault at its own backend instead of the sample endpoint.
    /// </remarks>
    public interface IVaultTransport
    {
        Task<TransportResponse> PostAsync(string url, string jsonBody, CancellationToken cancellationToken);
    }

    /// <summary>
    /// How the SDK should treat a transport attempt. Classification lives in the
    /// transport because only the transport can see the underlying failure.
    /// </summary>
    public enum TransportOutcome
    {
        /// <summary>2xx with a body.</summary>
        Success,

        /// <summary>4xx. Deterministic — retrying will fail the same way.</summary>
        Rejected,

        /// <summary>5xx or 429. The server saw the request and could not serve it.</summary>
        Retryable,

        /// <summary>
        /// No response was processed: no connection, DNS failure, TLS failure, timeout, abort.
        /// Critically this does NOT mean the server did nothing — a client-side timeout does
        /// not cancel server-side work, so the claim's true outcome is unknown.
        /// </summary>
        Indeterminate
    }

    public readonly struct TransportResponse
    {
        public TransportOutcome Outcome { get; }

        /// <summary>HTTP status, or -1 when no response was processed.</summary>
        public int StatusCode { get; }

        public string Body { get; }

        /// <summary>Transport-level detail for logging. Never parsed or branched on.</summary>
        public string Error { get; }

        public TransportResponse(TransportOutcome outcome, int statusCode, string body, string error = null)
        {
            Outcome = outcome;
            StatusCode = statusCode;
            Body = body;
            Error = error;
        }

        public static TransportResponse Success(int statusCode, string body) =>
            new TransportResponse(TransportOutcome.Success, statusCode, body);

        public static TransportResponse Rejected(int statusCode, string body, string error = null) =>
            new TransportResponse(TransportOutcome.Rejected, statusCode, body, error);

        public static TransportResponse Retryable(int statusCode, string body, string error = null) =>
            new TransportResponse(TransportOutcome.Retryable, statusCode, body, error);

        public static TransportResponse Indeterminate(string error) =>
            new TransportResponse(TransportOutcome.Indeterminate, -1, null, error);
    }
}
