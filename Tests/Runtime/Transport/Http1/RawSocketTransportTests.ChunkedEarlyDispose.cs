using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Transport.Http1
{
    public partial class RawSocketTransportTests
    {
        // Phase 22a.6 Step 1, HTTP/1.1 item 15 (#102): a streamed chunked body abandoned with more than the
        // 64 KiB drain budget left (no last-chunk reached within it) must close the connection instead of
        // reusing it (RFC 9112 Section 9.6: the connection is only reusable once the message is complete).
        [Test]
        public void RawSocketTransport_StreamingChunked_EarlyDisposeOverBudget_ClosesConnection()
        {
            AssertAsync.Run(async () =>
            {
                string sameConnectionFollowUp = null;
                var chunk = new byte[16 * 1024];
                Array.Fill(chunk, (byte)'c');
                var wire = new MemoryStream();
                for (var i = 0; i < 6; i++) // 96 KiB of chunk data: past the 64 KiB drain budget
                {
                    var size = Encoding.ASCII.GetBytes(chunk.Length.ToString("X") + "\r\n");
                    wire.Write(size, 0, size.Length);
                    wire.Write(chunk, 0, chunk.Length);
                    wire.Write(new byte[] { 13, 10 }, 0, 2);
                }

                var last = Encoding.ASCII.GetBytes("0\r\n\r\n");
                wire.Write(last, 0, last.Length);
                var chunkedBody = wire.ToArray();

                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        try
                        {
                            await ReadRequestHeadersAsync(stream);
                            await WriteResponseAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n",
                                chunkedBody);

                            var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromMilliseconds(300));
                            sameConnectionFollowUp = followUp?.RequestLine;
                        }
                        catch (IOException)
                        {
                        }
                        catch (SocketException)
                        {
                        }
                        finally
                        {
                            client.Close();
                        }

                        return;
                    }

                    await ReadRequestHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    AllowCleartext = true,
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using (var response = await client.Get($"http://127.0.0.1:{server.Port}/chunked").SendStreamingAsync())
                {
                    var buffer = new byte[1];
                    Assert.AreEqual(1, await response.Body.ReadAsync(buffer, 0, 1));
                    await response.DisposeAsync();
                }

                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/second").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.IsNull(sameConnectionFollowUp, "the over-budget chunked connection was reused");
                Assert.AreEqual(2, server.AcceptCount);
            });
        }
    }
}
