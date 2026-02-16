using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    public class CoroutineWrapperTests
    {
        [UnityTest]
        public IEnumerator SendCoroutine_SuccessCallbackIsInvoked()
        {
            var transport = new MockTransport(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("ok"));
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            var successCalled = false;
            Exception error = null;

            yield return client
                .Get("https://example.test/success")
                .SendCoroutine(
                    onSuccess: _ => successCalled = true,
                    onError: ex => error = ex);

            Assert.IsTrue(successCalled);
            Assert.IsNull(error);
        }

        [UnityTest]
        public IEnumerator SendCoroutine_ErrorCallbackReceivesRootException()
        {
            var transport = new MockTransport(HttpStatusCode.OK);
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            var builder = client.Get("https://example.test/error");
            client.Dispose();

            var successCalled = false;
            Exception error = null;

            yield return builder.SendCoroutine(
                onSuccess: _ => successCalled = true,
                onError: ex => error = ex);

            Assert.IsFalse(successCalled);
            Assert.IsNotNull(error);
            Assert.IsInstanceOf<ObjectDisposedException>(error);
        }

        [UnityTest]
        public IEnumerator SendCoroutine_CancellationSkipsCallbacks()
        {
            var transport = new MockTransport(HttpStatusCode.OK);
            transport.EnqueueResponse(
                statusCode: HttpStatusCode.OK,
                body: Encoding.UTF8.GetBytes("slow"),
                delay: TimeSpan.FromMilliseconds(200));

            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            var cts = new CancellationTokenSource();
            cts.CancelAfter(20);

            var successCalled = false;
            var errorCalled = false;

            yield return client
                .Get("https://example.test/cancel")
                .SendCoroutine(
                    onSuccess: _ => successCalled = true,
                    onError: _ => errorCalled = true,
                    cancellationToken: cts.Token);

            Assert.IsFalse(successCalled);
            Assert.IsFalse(errorCalled);
        }

        [UnityTest]
        public IEnumerator GetJsonCoroutine_ReturnsTypedPayload()
        {
            var payload = new Dictionary<string, object>
            {
                { "Id", 7 },
                { "Name", "phase11" }
            };

            var transport = new MockTransport();
            transport.EnqueueJsonResponse(payload);

            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            Dictionary<string, object> result = null;
            Exception error = null;

            yield return client.GetJsonCoroutine<Dictionary<string, object>>(
                "https://example.test/json",
                onSuccess: value => result = value,
                onError: ex => error = ex);

            Assert.IsNull(error);
            Assert.IsNotNull(result);
            Assert.AreEqual("phase11", result["Name"].ToString());
        }
    }
}
