using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace TurboHTTP.Transport.Http2
{
    internal sealed class SingleReaderChannel<T>
    {
        private const int SegmentSize = 32;

        private sealed class Segment
        {
            public readonly T[] Items;
            public int Head;
            public int Tail;
            public Segment Next;

            public Segment(T[] items)
            {
                Items = items ?? throw new ArgumentNullException(nameof(items));
            }
        }

        private readonly object _gate = new object();
        private readonly PendingReadSource _pendingRead = new PendingReadSource();
        private readonly int _capacity;

        private CancellationTokenRegistration _pendingCancellation;
        private CancellationToken _pendingCancellationToken;
        private Exception _error;
        private int _count;
        private bool _completed;
        private bool _readPending;
        private Segment _headSegment;
        private Segment _tailSegment;

        public SingleReaderChannel(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Must be greater than 0.");

            _capacity = capacity;
        }

        public int Capacity => _capacity;

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        public bool TryWrite(T item)
        {
            PendingReadSource pendingRead = null;

            lock (_gate)
            {
                ThrowIfCompletedOrFaulted();

                if (_readPending)
                {
                    pendingRead = _pendingRead;
                    _readPending = false;
                    _pendingCancellation.Dispose();
                    _pendingCancellationToken = default;
                }
                else
                {
                    if (_count == _capacity)
                        return false;

                    Enqueue(item);
                    return true;
                }
            }

            pendingRead.SetResult(item);
            return true;
        }

        public bool TryRead(out T item)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    item = default;
                    return false;
                }

                item = Dequeue();
                return true;
            }
        }

        public ValueTask<T> ReadAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_count > 0)
                    return new ValueTask<T>(Dequeue());

                if (_error != null)
                    return new ValueTask<T>(Task.FromException<T>(_error));

                if (_completed)
                {
                    return new ValueTask<T>(Task.FromException<T>(
                        new InvalidOperationException("Channel completed.")));
                }

                if (_readPending)
                    throw new InvalidOperationException("Only one pending reader is allowed.");

                if (ct.IsCancellationRequested)
                    return new ValueTask<T>(Task.FromCanceled<T>(ct));

                _readPending = true;
                _pendingRead.Reset();
                _pendingCancellationToken = ct;
                if (ct.CanBeCanceled)
                {
                    _pendingCancellation = ct.Register(
                        static state => ((SingleReaderChannel<T>)state).CancelPendingRead(),
                        this);
                }

                return _pendingRead.AsValueTask();
            }
        }

        public void Complete(Exception error = null)
        {
            PendingReadSource pendingRead = null;
            Exception pendingError = error;

            lock (_gate)
            {
                if (_completed)
                    return;

                _completed = true;
                if (error != null)
                    _error = error;

                if (_readPending)
                {
                    pendingRead = _pendingRead;
                    _readPending = false;
                    _pendingCancellation.Dispose();
                    _pendingCancellationToken = default;
                    if (pendingError == null)
                        pendingError = new InvalidOperationException("Channel completed.");
                }
            }

            if (pendingRead != null)
                pendingRead.SetException(pendingError);
        }

        private void CancelPendingRead()
        {
            PendingReadSource pendingRead = null;
            CancellationToken cancellationToken = default;

            lock (_gate)
            {
                if (!_readPending)
                    return;

                _readPending = false;
                pendingRead = _pendingRead;
                cancellationToken = _pendingCancellationToken;
                _pendingCancellation.Dispose();
                _pendingCancellationToken = default;
            }

            pendingRead.SetException(new OperationCanceledException(cancellationToken));
        }

        private void Enqueue(T item)
        {
            if (_tailSegment == null)
            {
                _headSegment = _tailSegment = RentSegment();
            }
            else if (_tailSegment.Tail >= SegmentSize)
            {
                var next = RentSegment();
                _tailSegment.Next = next;
                _tailSegment = next;
            }

            _tailSegment.Items[_tailSegment.Tail++] = item;
            _count++;
        }

        private T Dequeue()
        {
            var segment = _headSegment;
            var item = segment.Items[segment.Head];
            segment.Items[segment.Head] = default;
            segment.Head++;
            _count--;

            if (segment.Head == segment.Tail)
            {
                if (segment.Next != null)
                {
                    var next = segment.Next;
                    ReturnSegment(segment);
                    _headSegment = next;
                }
                else
                {
                    segment.Head = 0;
                    segment.Tail = 0;
                    _tailSegment = segment;
                }
            }

            return item;
        }

        private static Segment RentSegment()
        {
            var items = ArrayPool<T>.Shared.Rent(SegmentSize);
            Array.Clear(items, 0, Math.Min(items.Length, SegmentSize));
            return new Segment(items);
        }

        private static void ReturnSegment(Segment segment)
        {
            if (segment == null)
                return;

            var items = segment.Items;
            Array.Clear(items, 0, Math.Min(items.Length, SegmentSize));
            segment.Head = 0;
            segment.Tail = 0;
            segment.Next = null;
            ArrayPool<T>.Shared.Return(items);
        }

        private void ThrowIfCompletedOrFaulted()
        {
            if (_error != null)
                throw new InvalidOperationException("Channel faulted.", _error);
            if (_completed)
                throw new InvalidOperationException("Channel completed.");
        }

        private sealed class PendingReadSource : IValueTaskSource<T>
        {
            private ManualResetValueTaskSourceCore<int> _core;
            private T _result;

            public PendingReadSource()
            {
                _core = new ManualResetValueTaskSourceCore<int>
                {
                    RunContinuationsAsynchronously = true
                };
            }

            public void Reset()
            {
                _result = default;
                _core.Reset();
            }

            public ValueTask<T> AsValueTask()
            {
                return new ValueTask<T>(this, _core.Version);
            }

            public void SetResult(T result)
            {
                _result = result;
                _core.SetResult(1);
            }

            public void SetException(Exception error)
            {
                if (error == null)
                    throw new ArgumentNullException(nameof(error));

                _core.SetException(error);
            }

            T IValueTaskSource<T>.GetResult(short token)
            {
                _core.GetResult(token);
                return _result;
            }

            ValueTaskSourceStatus IValueTaskSource<T>.GetStatus(short token)
            {
                return _core.GetStatus(token);
            }

            void IValueTaskSource<T>.OnCompleted(
                Action<object> continuation,
                object state,
                short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                _core.OnCompleted(continuation, state, token, flags);
            }
        }
    }
}
