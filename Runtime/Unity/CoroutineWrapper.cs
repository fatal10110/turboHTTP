using System;
using System.Collections;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Coroutine wrappers over TurboHTTP async APIs for legacy MonoBehaviour workflows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancellation suppresses callback invocation. Once canceled, success/error callbacks
    /// are skipped even if the underlying task later completes.
    /// </para>
    /// <para>
    /// The returned enumerator also implements <see cref="IDisposable"/>. Unity never disposes a
    /// coroutine enumerator it stops, so dispose it when you stop the coroutine yourself
    /// (<c>StopCoroutine</c>, <c>StopAllCoroutines</c>, deactivating its host): that cancels the
    /// request and disposes a response that was not delivered. Destroying
    /// <c>callbackOwner</c>, deactivating it with <c>cancelOnOwnerInactive</c>, or canceling the
    /// token does the same without a dispose call.
    /// </para>
    /// </remarks>
    public static class CoroutineWrapper
    {
        private static readonly Action<UHttpResponse> DisposeResponse = response => response?.Dispose();

        /// <summary>
        /// Sends a request via coroutine callback pattern.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If <paramref name="cancellationToken"/> is canceled (or <paramref name="callbackOwner"/>
        /// is destroyed), callbacks are intentionally suppressed and the request is canceled.
        /// </para>
        /// <para>
        /// <paramref name="onSuccess"/> owns the response and disposes it. A response that is not
        /// handed to <paramref name="onSuccess"/> (callbacks suppressed, <paramref name="onSuccess"/>
        /// null, coroutine disposed) is disposed by the coroutine.
        /// </para>
        /// </remarks>
        /// <returns>
        /// An enumerator for <c>StartCoroutine</c> that sends the request on its first step. It
        /// implements <see cref="IDisposable"/>: dispose it when you stop the coroutine yourself.
        /// </returns>
        public static IEnumerator SendCoroutine(
            this UHttpRequest request,
            Action<UHttpResponse> onSuccess,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default,
            UnityEngine.Object callbackOwner = null,
            bool cancelOnOwnerInactive = false)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            return new CoroutineOperation<UHttpResponse>(
                token => request.SendBufferedAsync(token).AsTask(),
                DisposeResponse,
                onSuccess,
                onError,
                cancellationToken,
                callbackOwner,
                cancelOnOwnerInactive);
        }

        /// <summary>
        /// Sends a JSON GET request via coroutine callback pattern.
        /// </summary>
        /// <typeparam name="T">
        /// Payload type: a reference or a value type. An empty body yields <c>default(T)</c>.
        /// </typeparam>
        /// <remarks>
        /// <para>
        /// Sends <c>Accept: application/json</c>, fails on a non-success status, and deserializes the
        /// UTF-8 body through the JSON bridge registered with <see cref="ProjectJsonBridge"/>
        /// (TurboHTTP.JSON registers it at startup; no reflection is involved). Without a registered
        /// bridge, <paramref name="onError"/> receives an <see cref="InvalidOperationException"/>
        /// naming TurboHTTP.JSON. Deserialization errors reach <paramref name="onError"/> unwrapped.
        /// </para>
        /// <para>
        /// If <paramref name="cancellationToken"/> is canceled (or <paramref name="callbackOwner"/>
        /// is destroyed), callbacks are intentionally suppressed and the request is canceled.
        /// </para>
        /// </remarks>
        /// <returns>
        /// An enumerator for <c>StartCoroutine</c> that sends the request on its first step. It
        /// implements <see cref="IDisposable"/>: dispose it when you stop the coroutine yourself.
        /// </returns>
        public static IEnumerator GetJsonCoroutine<T>(
            this UHttpClient client,
            string url,
            Action<T> onSuccess,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default,
            UnityEngine.Object callbackOwner = null,
            bool cancelOnOwnerInactive = false)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty.", nameof(url));

            return new CoroutineOperation<T>(
                token => CreateJsonTask<T>(client, url, token),
                null,
                onSuccess,
                onError,
                cancellationToken,
                callbackOwner,
                cancelOnOwnerInactive);
        }

        internal static bool IsCallbackSuppressed(
            UnityEngine.Object callbackOwner,
            LifecycleCancellationBinding lifecycleBinding)
        {
            if (lifecycleBinding != null && lifecycleBinding.IsCancellationRequested)
                return true;

            return !ShouldInvokeCallbacks(callbackOwner, lifecycleBinding);
        }

        internal static bool ShouldInvokeCallbacks(
            UnityEngine.Object callbackOwner,
            LifecycleCancellationBinding lifecycleBinding)
        {
            if (lifecycleBinding != null && lifecycleBinding.IsCancellationRequested)
                return false;

            return callbackOwner == null || callbackOwner;
        }

        internal static void InvokeErrorIfEligible(
            Action<Exception> onError,
            Exception exception,
            UnityEngine.Object callbackOwner,
            LifecycleCancellationBinding lifecycleBinding)
        {
            if (!ShouldInvokeCallbacks(callbackOwner, lifecycleBinding))
                return;

            if (onError != null)
            {
                onError(exception);
            }
            else if (exception != null)
            {
                Debug.LogException(exception);
            }
        }

        internal static Exception UnwrapTaskException(Exception exception)
        {
            if (exception == null)
                return new InvalidOperationException("Task failed with an unknown error.");

            if (exception is AggregateException aggregate)
            {
                var flattened = aggregate.Flatten();
                if (flattened.InnerExceptions.Count > 0)
                    return flattened.InnerExceptions[0];
                return aggregate;
            }

            return exception;
        }

        private static Task<T> CreateJsonTask<T>(
            UHttpClient client,
            string url,
            CancellationToken cancellationToken)
        {
            // Resolved before the request is sent: a missing JSON module fails fast.
            var bridge = ProjectJsonBridge.GetRequired("GetJsonCoroutine<T>");
            return GetJsonAsync<T>(bridge, client, url, cancellationToken);
        }

        private static async Task<T> GetJsonAsync<T>(
            IJsonBridge bridge,
            UHttpClient client,
            string url,
            CancellationToken cancellationToken)
        {
            using var response = await client
                .Get(url)
                .WithHeader("Accept", ContentTypes.Json)
                .SendBufferedAsync(cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            if (response.Body.IsEmpty)
                return default;

            // RFC 8259 section 8.1: JSON exchanged between systems is UTF-8.
            var value = bridge.Deserialize(response.GetBodyAsString(Encoding.UTF8), typeof(T));
            if (value is T typed)
                return typed;
            if (value == null && default(T) == null)
                return default;

            throw new InvalidOperationException(
                "The registered JSON bridge returned " + (value == null ? "null" : value.GetType().FullName) +
                " for GetJsonCoroutine<" + typeof(T).FullName + ">.");
        }
    }
}
