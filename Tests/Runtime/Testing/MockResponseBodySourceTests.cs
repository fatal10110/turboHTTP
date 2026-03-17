using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Testing
{
    [TestFixture]
    public class MockResponseBodySourceTests
    {
        [Test]
        public async Task ReadAsync_ReadsQueuedChunksSequentially()
        {
            var source = new MockResponseBodySource(
                new ReadOnlyMemory<byte>[]
                {
                    Encoding.UTF8.GetBytes("ab"),
                    Encoding.UTF8.GetBytes("cd"),
                    Encoding.UTF8.GetBytes("ef")
                },
                length: 6);

            Assert.IsFalse(source.TryGetBufferedData(out _));

            var buffer = new byte[2];
            using var output = new MemoryStream();
            while (true)
            {
                var read = await source.ReadAsync(buffer, CancellationToken.None);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer, 0, read);
            }

            Assert.AreEqual("abcdef", Encoding.UTF8.GetString(output.ToArray()));
        }

        [Test]
        public void TryGetBufferedData_ReturnsSingleBufferedBlockWhenConfigured()
        {
            var source = new MockResponseBodySource(
                new ReadOnlyMemory<byte>[]
                {
                    Encoding.UTF8.GetBytes("pay"),
                    Encoding.UTF8.GetBytes("load")
                },
                length: 7,
                exposeBufferedData: true);

            Assert.IsTrue(source.TryGetBufferedData(out var data));
            Assert.AreEqual("payload", Encoding.UTF8.GetString(data.Span));
        }

        [Test]
        public void InjectFault_CausesSubsequentReadToThrow()
        {
            var source = new MockResponseBodySource(Encoding.UTF8.GetBytes("payload"), length: 7);
            source.InjectFault(new InvalidOperationException("boom"));

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await source.ReadAsync(new byte[4], CancellationToken.None));
            Assert.That(ex.Message, Is.EqualTo("boom"));
        }

        [Test]
        public async Task GetTrailersAsync_ReturnsConfiguredTrailers()
        {
            var trailers = new TurboHTTP.Core.HttpHeaders();
            trailers.Set("X-Trailer", "ok");

            var source = new MockResponseBodySource(ReadOnlyMemory<byte>.Empty, length: 0, trailers: trailers);
            var result = await source.GetTrailersAsync(CancellationToken.None);

            Assert.AreEqual("ok", result.Get("X-Trailer"));
        }
    }
}
