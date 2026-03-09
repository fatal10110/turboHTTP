using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Testing
{
    [TestFixture]
    public class RecordReplayTransportTests
    {
        [Test]
        public void Dispose_SwallowsSaveAndInnerDisposeExceptions()
        {
            var recordingPath = Path.Combine(
                Path.GetTempPath(),
                "turbohttp-record-replay-dispose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recordingPath);

            try
            {
                var transport = new RecordReplayTransport(
                    new ThrowingDisposeTransport(),
                    new RecordReplayTransportOptions
                    {
                        Mode = RecordReplayMode.Record,
                        RecordingPath = recordingPath,
                        AutoFlushOnDispose = true
                    });

                Assert.DoesNotThrow(() => transport.Dispose());
            }
            finally
            {
                if (Directory.Exists(recordingPath))
                    Directory.Delete(recordingPath, recursive: true);
            }
        }

        private sealed class ThrowingDisposeTransport : IHttpTransport
        {
            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
                throw new InvalidOperationException("inner-dispose-failure");
            }
        }
    }
}
