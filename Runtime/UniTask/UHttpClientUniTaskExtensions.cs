#if TURBOHTTP_UNITASK
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.UniTask
{
    /// <summary>
    /// Convenience adapters from TurboHTTP ValueTask APIs to UniTask.
    /// </summary>
    public static class UHttpClientUniTaskExtensions
    {
        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Get(...).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> GetAsync(
            this UHttpClient client,
            string url,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Get(url).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Delete(...).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> DeleteAsync(
            this UHttpClient client,
            string url,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Delete(url).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Head(...).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> HeadAsync(
            this UHttpClient client,
            string url,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Head(url).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Options(...).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> OptionsAsync(
            this UHttpClient client,
            string url,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Options(url).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Post(...).WithBody(byte[]).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> PostAsync(
            this UHttpClient client,
            string url,
            byte[] body,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Post(url).WithBody(body).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Post(...).WithBody(string).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> PostAsync(
            this UHttpClient client,
            string url,
            string body,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Post(url).WithBody(body).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Put(...).WithBody(byte[]).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> PutAsync(
            this UHttpClient client,
            string url,
            byte[] body,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Put(url).WithBody(body).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Put(...).WithBody(string).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> PutAsync(
            this UHttpClient client,
            string url,
            string body,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Put(url).WithBody(body).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Patch(...).WithBody(byte[]).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> PatchAsync(
            this UHttpClient client,
            string url,
            byte[] body,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Patch(url).WithBody(body).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Zero-overhead UniTask wrapper for UHttpClient.Patch(...).WithBody(string).SendBufferedAsync(...).
        /// </summary>
        public static UniTask<UHttpResponse> PatchAsync(
            this UHttpClient client,
            string url,
            string body,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            return client.Patch(url).WithBody(body).AsUniTask(cancellationToken, playerLoopTiming);
        }

        /// <summary>
        /// Sends <paramref name="request"/> through <paramref name="client"/> and returns the buffered
        /// response as a <see cref="UniTask{T}"/>. The caller owns the response and disposes it.
        /// </summary>
        /// <param name="client">The client that sends the request.</param>
        /// <param name="request">The request to send.</param>
        /// <param name="cancellationToken">Reaches the transport: cancelling it aborts the in-flight request
        /// and releases its connection. For a deadline, set <see cref="UHttpRequest.WithTimeout(System.TimeSpan)"/>
        /// on the request (enforced by the transport), or cancel this token from a timer.</param>
        /// <param name="playerLoopTiming">Player-loop timing the result is delivered on; defaults to
        /// <see cref="TurboHttpUniTaskOptions.DefaultPlayerLoopTiming"/>.</param>
        public static UniTask<UHttpResponse> AsUniTask(
            this UHttpClient client,
            UHttpRequest request,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (client == null) throw new System.ArgumentNullException(nameof(client));
            var timing = playerLoopTiming ?? TurboHttpUniTaskOptions.DefaultPlayerLoopTiming;
            return AwaitResponse(new ValueTask<UHttpResponse>(client.SendBufferedAsync(request, cancellationToken)), timing);
        }

        /// <summary>
        /// Sends <paramref name="request"/> through its owning client and returns the buffered response as a
        /// <see cref="UniTask{T}"/>. The caller owns the response and disposes it.
        /// </summary>
        /// <param name="request">A request created by a <see cref="UHttpClient"/>; it is disposed once sent.</param>
        /// <param name="cancellationToken">Reaches the transport: cancelling it aborts the in-flight request
        /// and releases its connection. For a deadline, set <see cref="UHttpRequest.WithTimeout(System.TimeSpan)"/>
        /// on the request (enforced by the transport), or cancel this token from a timer.</param>
        /// <param name="playerLoopTiming">Player-loop timing the result is delivered on; defaults to
        /// <see cref="TurboHttpUniTaskOptions.DefaultPlayerLoopTiming"/>.</param>
        public static UniTask<UHttpResponse> AsUniTask(
            this UHttpRequest request,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            if (request == null) throw new System.ArgumentNullException(nameof(request));
            var timing = playerLoopTiming ?? TurboHttpUniTaskOptions.DefaultPlayerLoopTiming;
            return AwaitResponse(request.SendBufferedAsync(cancellationToken), timing);
        }

        // UniTask bridges ValueTask only on Unity 2022.3+ (UniTask.AsValueTask.cs), and Task.AsUniTask()
        // throws without a SynchronizationContext, so both paths await here instead.
        private static async UniTask<UHttpResponse> AwaitResponse(
            ValueTask<UHttpResponse> operation,
            PlayerLoopTiming playerLoopTiming)
        {
            var response = await operation;
            if (playerLoopTiming == PlayerLoopTiming.Update)
                return response;

            try
            {
                await Cysharp.Threading.Tasks.UniTask.Yield(playerLoopTiming);
            }
            catch
            {
                response?.Dispose(); // the caller never receives it
                throw;
            }

            return response;
        }
    }
}
#endif
