using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// SslStream provider: one authentication call per SslStream (#171) and no unobserved
    /// handshake task after cancellation (#172). The handshake seam stands in for
    /// <c>AuthenticateAsClientAsync</c>; the inner stream records whether any TLS bytes were written.
    /// </summary>
    [TestFixture]
    public class SslStreamSingleHandshakeTests
    {
        private static readonly string[] H2AndHttp11 = { "h2", "http/1.1" };

        [Test]
        public void WrapAsync_AlpnHandshakeThrowsInvalidOperation_PropagatesWithoutSecondHandshake()
        {
            var stateError = new InvalidOperationException("handshake already in progress");
            int handshakeCalls = 0;
            var inner = new RecordingStream();
            var provider = new SslStreamTlsProvider(simulateNoAlpn: false, (sslStream, ct) =>
            {
                Interlocked.Increment(ref handshakeCalls);
                // Mid-handshake: the ClientHello is out, then the stream reports a state error.
                return FailAfterClientHelloAsync(inner, stateError);
            });

            // Off the Unity main thread: the seam's Task.Yield must not post to a blocked context.
            Exception failure = Capture(() => provider.WrapAsync(inner, "localhost", H2AndHttp11, CancellationToken.None));

            // Before the fix InvalidOperationException was treated as "ALPN unavailable" and a plain
            // AuthenticateAsClientAsync ran on the same SslStream, writing a second ClientHello.
            Assert.AreSame(stateError, failure);
            Assert.AreEqual(1, Volatile.Read(ref handshakeCalls));
            Assert.AreEqual(1, inner.WriteCalls, "Only the seam's single ClientHello may reach the wire.");
            Assert.IsTrue(inner.Disposed, "The failed SslStream (and its inner stream) must be disposed.");
        }

        [Test]
        public void WrapAsync_PlainHandshakeThrowsInvalidOperation_Propagates()
        {
            var stateError = new InvalidOperationException("stream state");
            int handshakeCalls = 0;
            var provider = new SslStreamTlsProvider(simulateNoAlpn: true, (sslStream, ct) =>
            {
                Interlocked.Increment(ref handshakeCalls);
                return Task.FromException(stateError);
            });

            Exception failure = Capture(() => provider.WrapAsync(new RecordingStream(), "localhost", H2AndHttp11, CancellationToken.None));

            Assert.AreSame(stateError, failure);
            Assert.AreEqual(1, Volatile.Read(ref handshakeCalls));
        }

        [Test]
        public void IsAlpnSetupFailure_ExcludesInvalidOperationException()
        {
            Assert.IsFalse(SslStreamTlsProvider.IsAlpnSetupFailure(new InvalidOperationException()));
            Assert.IsFalse(SslStreamTlsProvider.IsAlpnSetupFailure(new IOException()));
            Assert.IsFalse(SslStreamTlsProvider.IsAlpnSetupFailure(new System.Security.Authentication.AuthenticationException()));
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new System.Reflection.TargetInvocationException(new Exception())));
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new MissingMethodException()));
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new TypeLoadException()));
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new MemberAccessException()));
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new PlatformNotSupportedException()));
        }

        [Test]
        public void WrapAsync_CancelledPlainHandshakeFaultsLater_FaultIsObserved()
        {
            using var probe = new UnobservedTaskProbe();
            var marker = new ObjectDisposedException("SslStream");

            var outcome = CancelHandshakeThenFault(marker);
            probe.CollectWithControl();

            if (!probe.ControlReported)
            {
                TestRequirements.IgnoreEnvironment("This runtime did not report a known unobserved faulted task.");
                return;
            }

            Assert.IsInstanceOf<OperationCanceledException>(outcome.Failure);
            Assert.IsTrue(outcome.InnerDisposed, "Cancellation must dispose the SslStream and its inner stream.");
            Assert.IsFalse(probe.WasReported(marker),
                "The abandoned handshake faulted after the SslStream was disposed and nobody observed it.");
        }

        private sealed class HandshakeBox
        {
            public TaskCompletionSource<bool> Pending;
        }

        private struct CancelOutcome
        {
            public Exception Failure;
            public bool InnerDisposed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static CancelOutcome CancelHandshakeThenFault(Exception marker)
        {
            var box = new HandshakeBox();
            // simulateNoAlpn: the plain overload, which takes no CancellationToken.
            var provider = new SslStreamTlsProvider(simulateNoAlpn: true, (sslStream, ct) =>
            {
                box.Pending = new TaskCompletionSource<bool>();
                return box.Pending.Task;
            });
            var inner = new RecordingStream();
            using var cts = new CancellationTokenSource();

            var wrap = provider.WrapAsync(inner, "localhost", H2AndHttp11, cts.Token);
            Assert.IsFalse(wrap.IsCompleted, "The seam handshake never completes on its own.");
            cts.Cancel();
            var outcome = new CancelOutcome { Failure = Capture(wrap) };
            outcome.InnerDisposed = inner.Disposed;

            // The disposed SslStream ends the abandoned handshake with a fault.
            box.Pending.SetException(marker);
            box.Pending = null;
            return outcome;
        }

        private static async Task FailAfterClientHelloAsync(Stream inner, Exception failure)
        {
            await Task.Yield();
            inner.Write(new byte[] { 0x16, 0x03, 0x01 }, 0, 3);
            throw failure;
        }

        private static Exception Capture(Task task) => Capture(() => task);

        private static Exception Capture(Func<Task> action)
        {
            try
            {
                Task.Run(action).GetAwaiter().GetResult();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>Inner transport stream: counts writes, reads hit end of stream.</summary>
        private sealed class RecordingStream : Stream
        {
            private int _writeCalls;
            private int _disposed;

            public int WriteCalls => Volatile.Read(ref _writeCalls);
            public bool Disposed => Volatile.Read(ref _disposed) != 0;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                Interlocked.Increment(ref _writeCalls);
            }

            protected override void Dispose(bool disposing)
            {
                Interlocked.Exchange(ref _disposed, 1);
                base.Dispose(disposing);
            }
        }
    }
}
