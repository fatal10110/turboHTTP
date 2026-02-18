using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    public class LoggingMiddlewareTests
    {
        [Test]
        public void LogsRequestAndResponse()        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingMiddleware(msg => logs.Add(msg));
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/api"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(2, logs.Count);
                Assert.That(logs[0], Does.Contain("GET"));
                Assert.That(logs[0], Does.Contain("test.com"));
                Assert.That(logs[1], Does.Contain("200"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void LogLevelNone_NoLogs()        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingMiddleware(
                    msg => logs.Add(msg),
                    LoggingMiddleware.LogLevel.None);
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.IsEmpty(logs);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NonSuccessStatus_LogsWarn()        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingMiddleware(msg => logs.Add(msg));
                var transport = new MockTransport(HttpStatusCode.NotFound);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.That(logs[1], Does.Contain("[WARN]"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Exception_LogsError()
        {
            var logs = new List<string>();
            var middleware = new LoggingMiddleware(msg => logs.Add(msg));
            var transport = new MockTransport((req, ctx, ct) =>
            {
                throw new UHttpException(
                    new UHttpError(UHttpErrorType.NetworkError, "Connection refused"));
            });
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            AssertAsync.ThrowsAsync<UHttpException>(
                () => pipeline.ExecuteAsync(request, context));

            Assert.AreEqual(2, logs.Count); // Request log + error log
            Assert.That(logs[1], Does.Contain("[ERROR]"));
        }
    }
}
