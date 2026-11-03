using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #243: on HTTP/1.1 a request header or trailer field that may not be sent fails with the same
    /// non-retryable InvalidRequest HTTP/2 reports, with the validator's ArgumentException inside,
    /// and the field value never reaches the message.
    /// </summary>
    [TestFixture]
    public class RawSocketTransportInvalidFieldTests
    {
        [TestCase("X-Bad", "a\u0001b")]
        [TestCase("X-Bad", "nul\0value")]
        [TestCase("X-Bad", " leading")]
        [TestCase("X Bad", "value")]
        public void InvalidRequestHeader_FailsWithInvalidRequest_AndSendsNoBytes(string name, string value)
        {
            AssertAsync.Run(async () =>
            {
                using var server = new DrainingServer();
                using var client = CreateClient();

                var request = client.Get("http://127.0.0.1:" + server.Port + "/x");
                request.Headers.Set(name, value);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () => await request.SendBufferedAsync());

                AssertInvalidRequest(ex, value);
                Assert.AreEqual(0, await server.ReceivedByteCountAsync(),
                    "the field is validated before any request byte is written");
            });
        }

        [Test]
        public void InvalidRequestTrailer_FailsWithInvalidRequest()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new DrainingServer();
                using var client = CreateClient();

                const string badValue = "sha-256=\u0001";
                var request = client.Post("http://127.0.0.1:" + server.Port + "/upload")
                    .WithBody(Encoding.ASCII.GetBytes("abc"))
                    .WithRequestTrailers(
                        new[] { "X-Checksum" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("X-Checksum", badValue);
                            return trailers;
                        });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () => await request.SendBufferedAsync());

                AssertInvalidRequest(ex, badValue);
                // The connection is closed, not reused, after the partial (unterminated) message.
                await server.ReceivedByteCountAsync();
            });
        }

        private static void AssertInvalidRequest(UHttpException ex, string value)
        {
            Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
            Assert.IsFalse(ex.HttpError.IsRetryable());
            Assert.IsInstanceOf<ArgumentException>(ex.InnerException, ex.ToString());
            StringAssert.DoesNotContain(value, ex.HttpError.Message, "the value must not reach the message");
        }

        private static UHttpClient CreateClient()
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new RawSocketTransport(),
                DisposeTransport = true,
                AllowCleartext = true,
                DefaultTimeout = TimeSpan.FromSeconds(10)
            });
        }

        /// <summary>Accepts one connection and counts the bytes it receives until the client closes it.</summary>
        private sealed class DrainingServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Task<int> _received;

            public DrainingServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _received = Task.Run(DrainAsync);
            }

            public int Port { get; }

            public async Task<int> ReceivedByteCountAsync()
            {
                var done = await Task.WhenAny(_received, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                Assert.AreSame(_received, done, "the client did not close the connection");
                return await _received.ConfigureAwait(false);
            }

            private async Task<int> DrainAsync()
            {
                using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using var stream = client.GetStream();
                var buffer = new byte[4096];
                int total = 0;
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        total += read;
                }
                catch (IOException)
                {
                    // A reset after the client disposed the connection ends the count as well.
                }

                return total;
            }

            public void Dispose()
            {
                _listener.Stop();
            }
        }
    }
}
