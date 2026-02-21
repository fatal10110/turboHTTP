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
        /// Zero-overhead UniTask wrapper for UHttpClient.Get(...).SendAsync(...).
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
        /// Zero-overhead UniTask wrapper for UHttpClient.Delete(...).SendAsync(...).
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
        /// Zero-overhead UniTask wrapper for UHttpClient.Post(...).WithBody(byte[]).SendAsync(...).
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
        /// Zero-overhead UniTask wrapper for UHttpClient.Post(...).WithBody(string).SendAsync(...).
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
        /// Zero-overhead UniTask wrapper for UHttpClient.Put(...).WithBody(byte[]).SendAsync(...).
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
        /// Zero-overhead UniTask wrapper for UHttpClient.Put(...).WithBody(string).SendAsync(...).
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
        /// Zero-overhead UniTask wrapper for UHttpClient.Patch(...).WithBody(byte[]).SendAsync(...).
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
        /// Zero-overhead UniTask wrapper for UHttpClient.Patch(...).WithBody(string).SendAsync(...).
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

        public static UniTask<UHttpResponse> AsUniTask(
            this UHttpClient client,
            UHttpRequest request,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            var timing = playerLoopTiming ?? TurboHttpUniTaskOptions.DefaultPlayerLoopTiming;
            var operation = client.SendAsync(request, cancellationToken);
            return timing == PlayerLoopTiming.Update
                ? operation.AsUniTask()
                : ConvertWithTiming(operation, timing);
        }

        public static UniTask<UHttpResponse> AsUniTask(
            this UHttpRequestBuilder builder,
            CancellationToken cancellationToken = default,
            PlayerLoopTiming? playerLoopTiming = null)
        {
            var timing = playerLoopTiming ?? TurboHttpUniTaskOptions.DefaultPlayerLoopTiming;
            var operation = builder.SendAsync(cancellationToken);
            return timing == PlayerLoopTiming.Update
                ? operation.AsUniTask()
                : ConvertWithTiming(operation, timing);
        }

        private static async UniTask<UHttpResponse> ConvertWithTiming(
            ValueTask<UHttpResponse> operation,
            PlayerLoopTiming playerLoopTiming)
        {
            var response = await operation.AsUniTask();
            if (playerLoopTiming != PlayerLoopTiming.Update)
            {
                await UniTask.Yield(playerLoopTiming);
            }

            return response;
        }
    }
}
#endif
