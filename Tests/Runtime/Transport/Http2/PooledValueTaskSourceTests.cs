using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class PooledValueTaskSourceTests
    {
        [Test]
        public void PoolableValueTaskSource_Bool_CompletesAndReturnsToPool()
        {
            Task.Run(async () =>
            {
                var pool = new PoolableValueTaskSourcePool<bool>(maxSize: 4);
                var source = pool.Rent();
                var pending = source.CreateValueTask();

                source.SetResult(true);
                var result = await pending.ConfigureAwait(false);

                Assert.IsTrue(result);
                Assert.AreEqual(1, pool.Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PoolableValueTaskSource_UHttpResponse_CompletesAndReturnsToPool()
        {
            Task.Run(async () =>
            {
                var pool = new PoolableValueTaskSourcePool<UHttpResponse>(maxSize: 4);
                var source = pool.Rent();
                var pending = source.CreateValueTask();

                source.SetResult(new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    TimeSpan.Zero,
                    new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/source"))));

                var response = await pending.ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, pool.Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PoolableValueTaskSource_ReusedSource_InvalidatesPriorValueTaskToken()
        {
            Task.Run(async () =>
            {
                var pool = new PoolableValueTaskSourcePool<bool>(maxSize: 1);

                var source = pool.Rent();
                var first = source.CreateValueTask();
                source.SetResult(true);
                Assert.IsTrue(await first.ConfigureAwait(false));

                var reused = pool.Rent();
                var second = reused.CreateValueTask();
                reused.SetResult(false);
                Assert.IsFalse(await second.ConfigureAwait(false));

                Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
            }).GetAwaiter().GetResult();
        }
    }
}
