using System.Text;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class BufferedResponseBodySourceTests
    {
        [Test]
        public void TryDetachBufferedBody_WithTrailers_ReturnsFalse()
        {
            AssertAsync.Run(async () =>
            {
                var trailers = new HttpHeaders();
                trailers.Set("X-Trailer", "ok");

                await using var source = new BufferedResponseBodySource(
                    Encoding.UTF8.GetBytes("payload"),
                    trailers);

                Assert.IsFalse(source.TryDetachBufferedBody(out _));
                Assert.IsTrue(source.TryGetBufferedData(out var data));
                Assert.AreEqual("payload", Encoding.UTF8.GetString(data.Span));

                var returnedTrailers = await source.GetTrailersAsync(CancellationToken.None);
                Assert.AreEqual("ok", returnedTrailers.Get("X-Trailer"));
            });
        }
    }
}
