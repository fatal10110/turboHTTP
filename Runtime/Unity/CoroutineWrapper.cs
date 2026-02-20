using System;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
    /// Cancellation suppresses callback invocation. Once canceled, success/error callbacks
    /// are skipped even if the underlying task later completes.
    /// </remarks>
    public static class CoroutineWrapper
    {
        /// <summary>
        /// Sends a request via coroutine callback pattern.
        /// </summary>
        /// <remarks>
        /// If <paramref name="cancellationToken"/> is canceled (or <paramref name="callbackOwner"/>
        /// is destroyed), callbacks are intentionally suppressed.
        /// </remarks>
        public static IEnumerator SendCoroutine(
            this UHttpRequestBuilder builder,
            Action<UHttpResponse> onSuccess,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default,
            UnityEngine.Object callbackOwner = null,
            bool cancelOnOwnerInactive = false)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            return SendCoroutineImpl(
                builder,
                onSuccess,
                onError,
                cancellationToken,
                callbackOwner,
                cancelOnOwnerInactive);
        }

        /// <summary>
        /// Sends a JSON GET request via coroutine callback pattern.
        /// </summary>
        /// <remarks>
        /// If <paramref name="cancellationToken"/> is canceled (or <paramref name="callbackOwner"/>
        /// is destroyed), callbacks are intentionally suppressed.
        /// </remarks>
        public static IEnumerator GetJsonCoroutine<T>(
            this UHttpClient client,
            string url,
            Action<T> onSuccess,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default,
            UnityEngine.Object callbackOwner = null,
            bool cancelOnOwnerInactive = false)
            where T : class
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty.", nameof(url));

            return GetJsonCoroutineImpl(
                client,
                url,
                onSuccess,
                onError,
                cancellationToken,
                callbackOwner,
                cancelOnOwnerInactive);
        }

        private static IEnumerator SendCoroutineImpl(
            UHttpRequestBuilder builder,
            Action<UHttpResponse> onSuccess,
            Action<Exception> onError,
            CancellationToken cancellationToken,
            UnityEngine.Object callbackOwner,
            bool cancelOnOwnerInactive)
        {
            var lifecycleBinding = LifecycleCancellation.Bind(
                callbackOwner,
                cancellationToken,
                cancelOnOwnerInactive);

            Task<UHttpResponse> task;
            try
            {
                task = builder.SendAsync(lifecycleBinding.Token);
            }
            catch (Exception ex)
            {
                InvokeErrorIfEligible(onError, ex, callbackOwner, lifecycleBinding);
                lifecycleBinding.Dispose();
                yield break;
            }

            yield return RunTaskCoroutine(
                task,
                () => task.Result,
                onSuccess,
                onError,
                callbackOwner,
                lifecycleBinding);
        }

        private static IEnumerator GetJsonCoroutineImpl<T>(
            UHttpClient client,
            string url,
            Action<T> onSuccess,
            Action<Exception> onError,
            CancellationToken cancellationToken,
            UnityEngine.Object callbackOwner,
            bool cancelOnOwnerInactive)
            where T : class
        {
            var lifecycleBinding = LifecycleCancellation.Bind(
                callbackOwner,
                cancellationToken,
                cancelOnOwnerInactive);

            Task<T> jsonTask;
            try
            {
                jsonTask = CreateJsonTask<T>(client, url, lifecycleBinding.Token);
            }
            catch (Exception ex)
            {
                InvokeErrorIfEligible(onError, ex, callbackOwner, lifecycleBinding);
                lifecycleBinding.Dispose();
                yield break;
            }

            yield return RunTaskCoroutine(
                jsonTask,
                () => jsonTask.Result,
                onSuccess,
                onError,
                callbackOwner,
                lifecycleBinding);
        }

        private static IEnumerator RunTaskCoroutine<T>(
            Task task,
            Func<T> getResult,
            Action<T> onSuccess,
            Action<Exception> onError,
            UnityEngine.Object callbackOwner,
            LifecycleCancellationBinding lifecycleBinding)
        {
            var terminalState = 0;

            bool TryEnterTerminal()
            {
                return Interlocked.CompareExchange(ref terminalState, 1, 0) == 0;
            }

            void InvokeError(Exception exception)
            {
                if (!TryEnterTerminal())
                    return;

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

            try
            {
                while (!task.IsCompleted)
                {
                    if (IsCallbackSuppressed(callbackOwner, lifecycleBinding))
                        yield break;

                    yield return null;
                }

                if (task.IsCanceled || IsCallbackSuppressed(callbackOwner, lifecycleBinding))
                    yield break;

                if (task.IsFaulted)
                {
                    InvokeError(UnwrapTaskException(task.Exception));
                    yield break;
                }

                try
                {
                    var result = getResult();

                    if (!TryEnterTerminal())
                        yield break;

                    if (ShouldInvokeCallbacks(callbackOwner, lifecycleBinding))
                    {
                        onSuccess?.Invoke(result);
                    }
                }
                catch (Exception ex)
                {
                    InvokeError(UnwrapTaskException(ex));
                }
            }
            finally
            {
                lifecycleBinding?.Dispose();
            }
        }

        private static bool IsCallbackSuppressed(
            UnityEngine.Object callbackOwner,
            LifecycleCancellationBinding lifecycleBinding)
        {
            if (lifecycleBinding != null && lifecycleBinding.IsCancellationRequested)
                return true;

            return !ShouldInvokeCallbacks(callbackOwner, lifecycleBinding);
        }

        private static bool ShouldInvokeCallbacks(
            UnityEngine.Object callbackOwner,
            LifecycleCancellationBinding lifecycleBinding)
        {
            if (lifecycleBinding != null && lifecycleBinding.IsCancellationRequested)
                return false;

            return callbackOwner == null || callbackOwner;
        }

        private static void InvokeErrorIfEligible(
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

        private static Exception UnwrapTaskException(Exception exception)
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

            if (exception is TargetInvocationException tie && tie.InnerException != null)
                return tie.InnerException;

            return exception;
        }

        private static Task<T> CreateJsonTask<T>(
            UHttpClient client,
            string url,
            CancellationToken cancellationToken)
            where T : class
        {
            var extensionsType = Type.GetType(
                "TurboHTTP.JSON.JsonExtensions, TurboHTTP.JSON",
                throwOnError: false);

            if (extensionsType == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON assembly is required for GetJsonCoroutine<T>. " +
                    "Install or enable TurboHTTP.JSON in your project. " +
                    "For IL2CPP builds with managed stripping, ensure TurboHTTP.JSON/link.xml " +
                    "is included so JsonExtensions is preserved for reflection.");
            }

            var methods = extensionsType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            MethodInfo getJsonMethod = null;

            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (method.Name != "GetJsonAsync" || !method.IsGenericMethodDefinition)
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 3)
                    continue;

                if (parameters[0].ParameterType != typeof(UHttpClient) ||
                    parameters[1].ParameterType != typeof(string) ||
                    parameters[2].ParameterType != typeof(CancellationToken))
                {
                    continue;
                }

                getJsonMethod = method;
                break;
            }

            if (getJsonMethod == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON.JsonExtensions.GetJsonAsync<T>(UHttpClient, string, CancellationToken) " +
                    "was not found.");
            }

            try
            {
                var genericMethod = getJsonMethod.MakeGenericMethod(typeof(T));
                var task = genericMethod.Invoke(
                    obj: null,
                    parameters: new object[] { client, url, cancellationToken });

                if (task is Task<T> typedTask)
                    return typedTask;

                throw new InvalidOperationException(
                    "TurboHTTP.JSON.JsonExtensions.GetJsonAsync<T> returned an unexpected task type.");
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    "GetJsonCoroutine<T> cannot materialize the requested generic type at runtime. " +
                    "For IL2CPP, prefer reference types and ensure required generic usages are " +
                    "preserved for AOT compilation.",
                    ex);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
        }
    }
}
