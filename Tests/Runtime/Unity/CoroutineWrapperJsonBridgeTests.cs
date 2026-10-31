using System;
using System.Collections;
using System.Net;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;
using TurboHTTP.Unity;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// <c>GetJsonCoroutine&lt;T&gt;</c> deserializes through the registered JSON bridge, without
    /// reflection or <c>MakeGenericMethod</c>, for reference and value types (#95).
    /// </summary>
    public class CoroutineWrapperJsonBridgeTests
    {
        public sealed class ClassPayload
        {
            public int Id;
            public string Name;
        }

        public struct StructPayload
        {
            public int Id;
            public string Name;
        }

        [TearDown]
        public void TearDown()
        {
            JsonModuleRegistration.EnsureRegistered();
        }

        [UnityTest]
        public IEnumerator GetJsonCoroutine_ClassT_UsesRegisteredBridge()
        {
            var bridge = new PayloadBridge();
            ProjectJsonBridge.Register(bridge);
            var transport = new MockTransport(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("{\"Id\":1}"));
            using var client = NewClient(transport);

            ClassPayload result = null;
            Exception error = null;
            yield return client.GetJsonCoroutine<ClassPayload>(
                "https://example.test/json",
                onSuccess: value => result = value,
                onError: ex => error = ex);

            Assert.IsNull(error);
            Assert.IsNotNull(result);
            Assert.AreEqual("class", result.Name);
            Assert.AreEqual(typeof(ClassPayload), bridge.LastType);
            Assert.AreEqual("{\"Id\":1}", bridge.LastJson);
            Assert.AreEqual("application/json", transport.LastRequest.Headers.Get("Accept"));
        }

        [UnityTest]
        public IEnumerator GetJsonCoroutine_StructT_UsesRegisteredBridge()
        {
            var bridge = new PayloadBridge();
            ProjectJsonBridge.Register(bridge);
            var transport = new MockTransport(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("{\"Id\":2}"));
            using var client = NewClient(transport);

            var result = default(StructPayload);
            var called = false;
            Exception error = null;
            yield return client.GetJsonCoroutine<StructPayload>(
                "https://example.test/json",
                onSuccess: value =>
                {
                    result = value;
                    called = true;
                },
                onError: ex => error = ex);

            Assert.IsNull(error);
            Assert.IsTrue(called);
            Assert.AreEqual(2, result.Id);
            Assert.AreEqual("struct", result.Name);
            Assert.AreEqual(typeof(StructPayload), bridge.LastType);
        }

        [UnityTest]
        public IEnumerator GetJsonCoroutine_PrimitiveStructT_ThroughTurboHttpJson()
        {
            JsonModuleRegistration.EnsureRegistered();
            var transport = new MockTransport(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("42"));
            using var client = NewClient(transport);

            var result = 0;
            Exception error = null;
            yield return client.GetJsonCoroutine<int>(
                "https://example.test/json",
                onSuccess: value => result = value,
                onError: ex => error = ex);

            Assert.IsNull(error);
            Assert.AreEqual(42, result);
        }

        [UnityTest]
        public IEnumerator GetJsonCoroutine_EmptyBody_YieldsDefault()
        {
            var bridge = new PayloadBridge();
            ProjectJsonBridge.Register(bridge);
            var transport = new MockTransport(HttpStatusCode.OK);
            using var client = NewClient(transport);

            var result = new StructPayload { Id = 99 };
            var called = false;
            Exception error = null;
            yield return client.GetJsonCoroutine<StructPayload>(
                "https://example.test/json",
                onSuccess: value =>
                {
                    result = value;
                    called = true;
                },
                onError: ex => error = ex);

            Assert.IsNull(error);
            Assert.IsTrue(called);
            Assert.AreEqual(0, result.Id);
            Assert.IsNull(bridge.LastType, "An empty body is not handed to the bridge.");
        }

        [UnityTest]
        public IEnumerator GetJsonCoroutine_WithoutBridge_ReportsErrorNamingJsonModule()
        {
            ProjectJsonBridge.Reset();
            var transport = new MockTransport(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("{}"));
            using var client = NewClient(transport);

            var called = false;
            Exception error = null;
            yield return client.GetJsonCoroutine<ClassPayload>(
                "https://example.test/json",
                onSuccess: _ => called = true,
                onError: ex => error = ex);

            Assert.IsFalse(called);
            Assert.IsInstanceOf<InvalidOperationException>(error);
            StringAssert.Contains("TurboHTTP.JSON", error.Message);
            Assert.AreEqual(0, transport.RequestCount, "A missing JSON module fails before the request is sent.");
        }

        private static UHttpClient NewClient(MockTransport transport)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });
        }

        private sealed class PayloadBridge : IJsonBridge
        {
            public Type LastType;
            public string LastJson;

            public string Serialize(object value, Type type)
            {
                throw new NotSupportedException();
            }

            public object Deserialize(string json, Type type)
            {
                LastType = type;
                LastJson = json;
                if (type == typeof(ClassPayload))
                    return new ClassPayload { Id = 1, Name = "class" };
                if (type == typeof(StructPayload))
                    return new StructPayload { Id = 2, Name = "struct" };
                throw new NotSupportedException(type.FullName);
            }
        }
    }
}
