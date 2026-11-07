using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// The worst case of #293/#345/#226: a read that ignores both its token and
    /// <see cref="Stream.Dispose()"/>, as Mono and IL2CPP socket reads sometimes do. Queued answers are
    /// returned synchronously; every other read stays pending until the test calls
    /// <see cref="EndRead(int)"/>, <see cref="EndRead(string)"/> or <see cref="FailRead"/>. Writes succeed
    /// and are discarded.
    /// </summary>
    internal sealed class AbandonedReadStream : Stream
    {
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _answers = new Queue<byte[]>();
        private TaskCompletionSource<bool> _readStarted = NewSignal();
        private TaskCompletionSource<int> _read;
        private int _answerOffset;
        private int _disposed;

        public AbandonedReadStream(params string[] answers)
        {
            foreach (var answer in answers)
                _answers.Enqueue(Encoding.ASCII.GetBytes(answer));
        }

        /// <summary>Completes once the next pending read started.</summary>
        public Task ReadPending
        {
            get
            {
                lock (_gate)
                    return _readStarted.Task;
            }
        }

        /// <summary>The buffer the last pending read writes into.</summary>
        public byte[] ReadBuffer { get; private set; }

        public int ReadOffset { get; private set; }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Called with each write as ASCII text (a handshake request, say), before the write returns; it
        /// may queue the reply with <see cref="Answer"/>.
        /// </summary>
        public Action<string> OnWrite { get; set; }

        /// <summary>Queues <paramref name="bytes"/> as the answer to the next reads that start.</summary>
        public void Answer(byte[] bytes)
        {
            lock (_gate)
                _answers.Enqueue(bytes);
        }

        public void EndRead(int count) => TakePendingRead().TrySetResult(count);

        public void EndRead(string ascii)
        {
            var bytes = Encoding.ASCII.GetBytes(ascii);
            Array.Copy(bytes, 0, ReadBuffer, ReadOffset, bytes.Length);
            TakePendingRead().TrySetResult(bytes.Length);
        }

        public void FailRead(Exception ex) => TakePendingRead().TrySetException(ex);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> started;
            TaskCompletionSource<int> read;
            lock (_gate)
            {
                if (_answers.Count > 0)
                {
                    var answer = _answers.Peek();
                    int n = Math.Min(answer.Length - _answerOffset, count);
                    Array.Copy(answer, _answerOffset, buffer, offset, n);
                    _answerOffset += n;
                    if (_answerOffset == answer.Length)
                    {
                        _answers.Dequeue();
                        _answerOffset = 0;
                    }

                    return Task.FromResult(n);
                }

                // Inline continuations: EndRead/FailRead return after the reader's observer ran.
                read = new TaskCompletionSource<int>();
                _read = read;
                ReadBuffer = buffer;
                ReadOffset = offset;
                started = _readStarted;
            }

            started.TrySetResult(true);
            return read.Task;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ObserveWrite(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => ObserveWrite(buffer, offset, count);

        private void ObserveWrite(byte[] buffer, int offset, int count) =>
            OnWrite?.Invoke(Encoding.ASCII.GetString(buffer, offset, count));

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Deliberately does not end a pending read.
            Interlocked.Exchange(ref _disposed, 1);
            base.Dispose(disposing);
        }

        private TaskCompletionSource<int> TakePendingRead()
        {
            lock (_gate)
            {
                var read = _read ?? throw new InvalidOperationException("No read is pending.");
                _read = null;
                _readStarted = NewSignal();
                return read;
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
