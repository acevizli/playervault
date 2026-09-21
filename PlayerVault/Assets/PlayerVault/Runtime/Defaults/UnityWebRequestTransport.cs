using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace PlayerVault
{
    /// <summary>
    /// The default transport, over <see cref="UnityWebRequest"/>.
    /// </summary>
    public sealed class UnityWebRequestTransport : IVaultTransport
    {
        readonly TimeSpan _timeout;

        public UnityWebRequestTransport(TimeSpan timeout)
        {
            _timeout = timeout;
        }

        public async Task<TransportResponse> PostAsync(string url, string jsonBody, CancellationToken cancellationToken)
        {
            // The three-argument overload. The two-argument UnityWebRequest.Post(url, string)
            // is obsolete and hidden from IntelliSense in Unity 6, and routes to PostWwwForm:
            // it percent-encodes the entire JSON document into a form field *key* and sends
            // application/x-www-form-urlencoded. Servers still answer 200, so the mistake
            // looks like success while transmitting nothing usable.
            using var request = UnityWebRequest.Post(url, jsonBody, "application/json");

            request.timeout = Math.Max(1, (int)Math.Ceiling(_timeout.TotalSeconds));

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = request.SendWebRequest();
            operation.completed += _ => completion.TrySetResult(true);

            using (cancellationToken.Register(() =>
            {
                try { request.Abort(); } catch (Exception) { /* already finished */ }
            }))
            {
                await completion.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var status = (int)request.responseCode;
            var body = request.downloadHandler != null ? request.downloadHandler.text : null;

            switch (request.result)
            {
                case UnityWebRequest.Result.Success:
                    return TransportResponse.Success(status, body);

                case UnityWebRequest.Result.ProtocolError:
                    // 429 is a throttle, not a refusal, so it retries with the 5xx family.
                    if (status == 429 || status >= 500)
                        return TransportResponse.Retryable(status, body, request.error);
                    return TransportResponse.Rejected(status, body, request.error);

                case UnityWebRequest.Result.ConnectionError:
                    // No connection, DNS failure, TLS failure, timeout and Abort all collapse
                    // into this one value with responseCode -1; Unity does not expose which.
                    // It means no response was processed — NOT that the server did nothing.
                    return TransportResponse.Indeterminate(request.error);

                default:
                    return TransportResponse.Indeterminate(request.error);
            }
        }
    }
}
