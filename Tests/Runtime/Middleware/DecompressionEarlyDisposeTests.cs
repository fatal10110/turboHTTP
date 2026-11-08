using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// Phase 22a.6 Step 1, decompression item 31 (#102): a streamed gzip body the caller abandons is not read
    /// to its trailer, so its CRC-32 is not validated and disposing it does not fail. The same bytes read to
    /// the end do fail, which proves the payload's CRC is wrong and the early dispose really skipped the check.
    /// </summary>
    [TestFixture]
    public sealed class DecompressionEarlyDisposeTests
    {
        private static readonly byte[] s_payload = CreatePayload(256 * 1024);

        [Test]
        public void StreamingGzip_DisposedBeforeTheTrailer_SkipsCrcValidation()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(CorruptCrc(Gzip(s_payload)));

                var response = await client.Get("https://decomp.local/early").SendStreamingAsync();
                var buffer = new byte[1024];
                var read = await response.Body.ReadAsync(buffer, 0, buffer.Length);
                Assert.Greater(read, 0);
                CollectionAssert.AreEqual(new ArraySegment<byte>(s_payload, 0, read), new ArraySegment<byte>(buffer, 0, read));

                await response.DisposeAsync(); // must not throw: the trailer and its CRC were never reached
            });
        }

        [Test]
        public void StreamingGzip_ReadToTheEnd_FailsOnTheSameCorruptCrc()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(CorruptCrc(Gzip(s_payload)));

                await using var response = await client.Get("https://decomp.local/full").SendStreamingAsync();
                var buffer = new byte[16 * 1024];
                Exception failure = null;
                try
                {
                    while (await response.Body.ReadAsync(buffer, 0, buffer.Length) > 0)
                    {
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }

                Assert.IsNotNull(failure, "reading the whole body must validate the gzip trailer CRC");
            });
        }

        private static UHttpClient CreateClient(byte[] compressed)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new GzipTransport(compressed),
                DisposeTransport = true,
                FollowRedirects = false,
                Interceptors = new List<IHttpInterceptor> { new DecompressionInterceptor() }
            });
        }

        private static byte[] CorruptCrc(byte[] gzip)
        {
            gzip[gzip.Length - 8] ^= 0xFF; // first byte of the CRC-32 in the gzip trailer (RFC 1952 Section 2.2)
            return gzip;
        }

        private static byte[] Gzip(byte[] payload)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(payload, 0, payload.Length);
            return output.ToArray();
        }

        private static byte[] CreatePayload(int length)
        {
            var bytes = new byte[length];
            var random = new Random(22);
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)(i % 7 == 0 ? random.Next(256) : i * 31 % 251);
            return bytes;
        }

        private sealed class GzipTransport : IHttpTransport
        {
            private readonly byte[] _compressed;

            internal GzipTransport(byte[] compressed)
            {
                _compressed = compressed;
            }

            public Task DispatchAsync(UHttpRequest request, IHttpHandler handler, RequestContext context, CancellationToken cancellationToken = default)
            {
                handler.OnRequestStart(request, context);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Length", _compressed.Length.ToString());
                var chunks = new List<ReadOnlyMemory<byte>>();
                for (var offset = 0; offset < _compressed.Length; offset += 512)
                    chunks.Add(new ReadOnlyMemory<byte>(_compressed, offset, Math.Min(512, _compressed.Length - offset)));
                return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(chunks, _compressed.Length, HttpHeaders.Empty),
                        context)
                    .AsTask();
            }

            public void Dispose()
            {
            }
        }
    }
}
