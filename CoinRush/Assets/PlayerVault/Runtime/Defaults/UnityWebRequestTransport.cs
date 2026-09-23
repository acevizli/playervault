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
    /// Can be called from any thread. The SDK awaits its retry delay, which resumes on the
    /// thread pool, so retries reach this class off Unity's main thread. UnityWebRequest
    /// throws when used off the main thread, so this class captures Unity's
    /// synchronization context when it is created and runs requests on it.
    /// </remarks>
    public sealed class UnityWebRequestTransport : IVaultTransport
    {
        readonly TimeSpan _timeout;

        /// <summary>
        /// Unity's context, captured at construction. Null if constructed off the main thread;
        /// calls then run inline and Unity throws, which makes the mistake visible.
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
                    // Use the three-argument overload. The two-argument Post(url, string) is
                    // obsolete in Unity 6 and sends the JSON as a URL-encoded form field. Servers
                    // still return 200, so the bug is easy to miss.
                    request = UnityWebRequest.Post(url, jsonBody, "application/json");
                    request.timeout = Math.Max(1, (int)Math.Ceiling(_timeout.TotalSeconds));

                    var pending = request;
                    registration = cancellationToken.Register(() =>
                    {
                        try { pending.Abort(); } catch (Exception) { /* already finished or disposed */ }
                    });

                    var operation = request.SendWebRequest();

                    // Read the response and dispose here. This callback runs on the main thread;
                    // the awaiting caller resumes on the thread pool.
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
                    // 429 means throttled, so it is retried like a 5xx.
                    if (status == 429 || status >= 500)
                        return TransportResponse.Retryable(status, body, request.error);
                    return TransportResponse.Rejected(status, body, request.error);

                case UnityWebRequest.Result.ConnectionError:
                    // No connection, DNS failure, TLS failure, timeout and abort all end up here
                    // with responseCode -1, and Unity does not say which. It means no response
                    // arrived. The server may still have processed the request.
                    return TransportResponse.Indeterminate(request.error);

                default:
                    return TransportResponse.Indeterminate(request.error);
            }
        }
    }
}
