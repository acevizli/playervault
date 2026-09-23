using System.Threading;
using System.Threading.Tasks;

namespace PlayerVault
{
    /// <summary>
    /// How the SDK talks to a reward backend.
    /// </summary>
    /// <remarks>
    /// One method and no Unity types, so a test fake is about ten lines and the tests run
    /// without a network. A game implements this to use its own backend instead of the
    /// sample endpoint.
    /// </remarks>
    public interface IVaultTransport
    {
        Task<TransportResponse> PostAsync(string url, string jsonBody, CancellationToken cancellationToken);
    }

    /// <summary>
    /// How the SDK should treat a request result. The transport decides this because only it
    /// can see the underlying error.
    /// </summary>
    public enum TransportOutcome
    {
        /// <summary>2xx with a body.</summary>
        Success,

        /// <summary>4xx. Retrying would fail the same way.</summary>
        Rejected,

        /// <summary>5xx or 429. The server saw the request and could not serve it.</summary>
        Retryable,

        /// <summary>
        /// No response arrived: no connection, DNS failure, TLS failure, timeout or abort. The
        /// server may still have processed the request, so the outcome is unknown.
        /// </summary>
        Indeterminate
    }

    public readonly struct TransportResponse
    {
        public TransportOutcome Outcome { get; }

        /// <summary>HTTP status, or -1 when no response was processed.</summary>
        public int StatusCode { get; }

        public string Body { get; }

        /// <summary>Error detail for logging. The SDK does not parse it.</summary>
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
