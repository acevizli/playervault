using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace PlayerVault
{
    /// <summary>
    /// The default transport, over <see cref="UnityWebRequest"/>.
    /// </summary>
    /// <remarks>
    /// Callable from any thread. That matters more than it sounds: the SDK awaits its
    /// retry backoff, and an awaited continuation resumes on the thread pool, so the
    /// second attempt of any claim arrives here off Unity's main thread. Every
    /// UnityWebRequest member — including construction — throws there, so this class
    /// captures Unity's synchronization context when it is built and marshals onto it.
    /// </remarks>
    public sealed class UnityWebRequestTransport : IVaultTransport
    {
        readonly TimeSpan _timeout;

        /// <summary>
        /// Unity's context, captured at construction. Null when constructed off the main
        /// thread, in which case calls run inline and Unity will complain — which is the
        /// correct, loud failure rather than a silent one.
        /// </summary>
        readonly SynchronizationContext _unityContext;

        public UnityWebRequestTransport(TimeSpan timeout)
        {
            _timeout = timeout;
            _unityContext = SynchronizationContext.Current;
        }

        public Task<TransportResponse> PostAsync(string url, string jsonBody, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<TransportResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            RunOnUnityThread(() =>
            {
                UnityWebRequest request = null;
                var registration = default(CancellationTokenRegistration);

                try
                {
                    // The three-argument overload. The two-argument UnityWebRequest.Post(url, string)
                    // is obsolete and hidden from IntelliSense in Unity 6, and routes to PostWwwForm:
                    // it percent-encodes the entire JSON document into a form field *key* and sends
                    // application/x-www-form-urlencoded. Servers still answer 200, so the mistake
                    // looks like success while transmitting nothing usable.
                    request = UnityWebRequest.Post(url, jsonBody, "application/json");
                    request.timeout = Math.Max(1, (int)Math.Ceiling(_timeout.TotalSeconds));

                    var pending = request;
                    registration = cancellationToken.Register(() =>
                    {
                        try { pending.Abort(); } catch (Exception) { /* already finished or disposed */ }
                    });

                    var operation = request.SendWebRequest();

                    // Read and dispose here: this callback is the last point guaranteed to be
                    // on the main thread, and the awaiting caller resumes on the thread pool.
                    operation.completed += _ =>
                    {
                        try { completion.TrySetResult(Classify(pending)); }
                        catch (Exception exception) { completion.TrySetException(exception); }
                        finally
                        {
                            registration.Dispose();
                            pending.Dispose();
                        }
                    };
                }
                catch (Exception exception)
                {
                    registration.Dispose();
                    request?.Dispose();
                    completion.TrySetException(exception);
                }
            });

            return completion.Task;
        }

        void RunOnUnityThread(Action action)
        {
            if (_unityContext == null || SynchronizationContext.Current == _unityContext) action();
            else _unityContext.Post(_ => action(), null);
        }

        static TransportResponse Classify(UnityWebRequest request)
        {
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
