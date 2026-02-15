using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;

namespace TurboHTTP.Tests.Transport
{
    [TestFixture]
    public class Http11ResponseParserPerformanceTests
    {
        private sealed class CountingReadStream : MemoryStream
        {
            public int ReadCallCount { get; private set; }
            public int SingleByteReadRequestCount { get; private set; }

            public CountingReadStream(byte[] buffer)
                : base(buffer)
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCallCount++;
                if (count == 1)
                    SingleByteReadRequestCount++;
                return base.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ReadCallCount++;
                if (count == 1)
                    SingleByteReadRequestCount++;
                return base.ReadAsync(buffer, offset, count, cancellationToken);
            }
        }

        [Test]
        [Category("Benchmark")]
        public void Performance_ParserDoesNotIssueSingleByteReads()
        {
            Task.Run(async () =>
            {
                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 200 OK\r\n");
                for (int i = 0; i < 40; i++)
                {
                    sb.Append("X-H");
                    sb.Append(i);
                    sb.Append(": value");
                    sb.Append(i);
                    sb.Append("\r\n");
                }
                sb.Append("Content-Length: 5\r\n\r\nHello");

                var bytes = Encoding.ASCII.GetBytes(sb.ToString());
                using var stream = new CountingReadStream(bytes);

                var parsed = await Http11ResponseParser.ParseAsync(stream, HttpMethod.GET, CancellationToken.None);

                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
                Assert.AreEqual("Hello", Encoding.ASCII.GetString(parsed.Body));
                Assert.AreEqual(0, stream.SingleByteReadRequestCount,
                    "Parser regression: single-byte read requests reintroduced.");
                Assert.LessOrEqual(stream.ReadCallCount, 64,
                    "Unexpected read call count suggests buffering regression.");
            }).GetAwaiter().GetResult();
        }
    }
}
