using System;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Coroutine wrappers over TurboHTTP async APIs for legacy MonoBehaviour workflows.
    /// </summary>
    public static class CoroutineWrapper
    {
        /// <summary>
        /// Sends a request via coroutine callback pattern.
        /// </summary>
        public static IEnumerator SendCoroutine(
            this UHttpRequestBuilder builder,
            Action<UHttpResponse> onSuccess,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default,
            UnityEngine.Object callbackOwner = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var task = builder.SendAsync(cancellationToken);
            return RunTaskCoroutine(
                task,
                () => task.Result,
                onSuccess,
                onError,
                cancellationToken,
                callbackOwner);
        }

        /// <summary>
        /// Sends a JSON GET request via coroutine callback pattern.
        /// </summary>
        public static IEnumerator GetJsonCoroutine<T>(
            this UHttpClient client,
            string url,
            Action<T> onSuccess,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default,
            UnityEngine.Object callbackOwner = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty.", nameof(url));

            Task<T> jsonTask;
            try
            {
                jsonTask = CreateJsonTask<T>(client, url, cancellationToken);
            }
            catch (Exception ex)
            {
                if (ShouldInvokeCallbacks(callbackOwner))
                    onError?.Invoke(ex);
                yield break;
            }

            yield return RunTaskCoroutine(
                jsonTask,
                () => jsonTask.Result,
                onSuccess,
                onError,
                cancellationToken,
                callbackOwner);
        }

        private static IEnumerator RunTaskCoroutine<T>(
            Task task,
            Func<T> getResult,
            Action<T> onSuccess,
            Action<Exception> onError,
            CancellationToken cancellationToken,
            UnityEngine.Object callbackOwner)
        {
            var errorInvoked = false;

            void InvokeError(Exception ex)
            {
                if (errorInvoked)
                    return;

                errorInvoked = true;
                if (ShouldInvokeCallbacks(callbackOwner))
                    onError?.Invoke(ex);
            }

            while (!task.IsCompleted)
            {
                if (cancellationToken.IsCancellationRequested || !ShouldInvokeCallbacks(callbackOwner))
                    yield break;

                yield return null;
            }

            if (cancellationToken.IsCancellationRequested ||
                task.IsCanceled ||
                !ShouldInvokeCallbacks(callbackOwner))
            {
                yield break;
            }

            try
            {
                if (task.IsFaulted)
                {
                    InvokeError(UnwrapTaskException(task.Exception));
                    yield break;
                }

                var result = getResult();
                onSuccess?.Invoke(result);
            }
            catch (Exception ex)
            {
                InvokeError(UnwrapTaskException(ex));
            }
        }

        private static bool ShouldInvokeCallbacks(UnityEngine.Object callbackOwner)
        {
            return callbackOwner == null || callbackOwner;
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
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
        }
    }
}
