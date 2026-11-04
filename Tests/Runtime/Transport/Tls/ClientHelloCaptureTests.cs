using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using NUnit.Framework;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #330: <see cref="ClientHelloCapture.ReadHandshakeMessage"/> retries a read that the socket
    /// layer reports as "would block" (IL2CPP Android EAGAIN) or timed out, and lets every other
    /// socket error through unchanged. Driven by a scripted stream, no sockets.
    /// </summary>
    [TestFixture]
    public class ClientHelloCaptureTests
    {
        [TestCase(SocketError.WouldBlock)]
        [TestCase(SocketError.TimedOut)]
        public void ReadHandshakeMessage_RetriesTransientSocketError(SocketError transient)
        {
            byte[] message = { ClientHelloCapture.ClientHelloType, 0x00, 0x00, 0x03, 0xAA, 0xBB, 0xCC };
            var stream = new ScriptedReadStream(
                Fail(transient),
                Record(message, 0, 2), // record header plus a partial fragment
                Fail(transient),
                Record(message, 2, message.Length - 2));

            byte[] result = ClientHelloCapture.ReadHandshakeMessage(stream);

            CollectionAssert.AreEqual(message, result);
            Assert.AreEqual(2, stream.Failures, "Both scripted transient errors were retried.");
        }

        [Test]
        public void ReadHandshakeMessage_OtherSocketError_Propagates()
        {
            byte[] message = { ClientHelloCapture.ClientHelloType, 0x00, 0x00, 0x01, 0xAA };
            var stream = new ScriptedReadStream(Fail(SocketError.ConnectionReset), Record(message, 0, message.Length));

            var ex = Assert.Throws<IOException>(() => ClientHelloCapture.ReadHandshakeMessage(stream));

            Assert.IsInstanceOf<SocketException>(ex.InnerException);
            Assert.AreEqual(SocketError.ConnectionReset, ((SocketException)ex.InnerException).SocketErrorCode);
            Assert.AreEqual(1, stream.Failures);
        }

        private static object Fail(SocketError error)
        {
            return new IOException("scripted " + error, new SocketException((int)error));
        }

        /// <summary>One TLS handshake record carrying <paramref name="count"/> bytes of the message.</summary>
        private static object Record(byte[] message, int offset, int count)
        {
            var record = new byte[5 + count];
            record[0] = ClientHelloCapture.HandshakeRecord;
            record[1] = 0x03;
            record[2] = 0x03;
            record[3] = (byte)(count >> 8);
            record[4] = (byte)count;
            Array.Copy(message, offset, record, 5, count);
            return record;
        }

        /// <summary>
        /// Read-only stream that plays a script: an <see cref="Exception"/> step is thrown by one
        /// Read call, a byte[] step is served across as many Read calls as the caller needs.
        /// </summary>
        private sealed class ScriptedReadStream : Stream
        {
            private readonly Queue<object> _steps;
            private byte[] _current;
            private int _offset;

            public ScriptedReadStream(params object[] steps)
            {
                _steps = new Queue<object>(steps);
            }

            public int Failures { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_current == null || _offset == _current.Length)
                {
                    if (_steps.Count == 0)
                        return 0;

                    object step = _steps.Dequeue();
                    if (step is Exception error)
                    {
                        Failures++;
                        throw error;
                    }

                    _current = (byte[])step;
                    _offset = 0;
                }

                int n = Math.Min(count, _current.Length - _offset);
                Array.Copy(_current, _offset, buffer, offset, n);
                _offset += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
