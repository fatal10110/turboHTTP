# Phase 10: Performance & Hardening

**Milestone:** M3 (v1.0 "production")
**Dependencies:** Phase 9 (Testing Infrastructure)
**Estimated Complexity:** High
**Critical:** Yes - Production performance

## Overview

Optimize TurboHTTP for production use with memory pooling, backpressure handling, concurrency control, and comprehensive error handling. Ensure the library performs well under load and handles edge cases gracefully.

## Goals

1. Implement memory pooling for byte arrays
2. Add backpressure handling for concurrent requests
3. Implement concurrency limits per host
4. Optimize timeline event allocation
5. Add request queuing system
6. Implement proper disposal patterns
7. Add stress testing
8. Profile and optimize hot paths

## Tasks

### Task 10.1: Object Pool

**File:** `Runtime/Performance/ObjectPool.cs`

```csharp
using System;
using System.Collections.Concurrent;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Generic object pool for reducing allocations.
    /// </summary>
    public class ObjectPool<T> where T : class, new()
    {
        private readonly ConcurrentBag<T> _objects = new ConcurrentBag<T>();
        private readonly Func<T> _factory;
        private readonly Action<T> _reset;
        private readonly int _maxSize;
        private int _count;

        public ObjectPool(Func<T> factory = null, Action<T> reset = null, int maxSize = 100)
        {
            _factory = factory ?? (() => new T());
            _reset = reset;
            _maxSize = maxSize;
        }

        /// <summary>
        /// Rent an object from the pool.
        /// </summary>
        public T Rent()
        {
            if (_objects.TryTake(out var obj))
            {
                System.Threading.Interlocked.Decrement(ref _count);
                return obj;
            }

            return _factory();
        }

        /// <summary>
        /// Return an object to the pool.
        /// </summary>
        public void Return(T obj)
        {
            if (obj == null)
                return;

            if (_count >= _maxSize)
            {
                // Pool is full, let GC handle it
                return;
            }

            _reset?.Invoke(obj);
            _objects.Add(obj);
            System.Threading.Interlocked.Increment(ref _count);
        }

        /// <summary>
        /// Clear all pooled objects.
        /// </summary>
        public void Clear()
        {
            while (_objects.TryTake(out _))
            {
                System.Threading.Interlocked.Decrement(ref _count);
            }
        }

        public int Count => _count;
    }
}
```

### Task 10.2: Byte Array Pool

**File:** `Runtime/Performance/ByteArrayPool.cs`

```csharp
using System;
using System.Collections.Concurrent;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Pool for byte arrays to reduce GC pressure.
    /// </summary>
    public static class ByteArrayPool
    {
        private static readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _pools
            = new ConcurrentDictionary<int, ConcurrentBag<byte[]>>();

        private static readonly int[] _standardSizes = { 1024, 4096, 8192, 16384, 32768, 65536, 131072 };

        /// <summary>
        /// Rent a byte array of at least the specified size.
        /// </summary>
        public static byte[] Rent(int minimumSize)
        {
            var size = GetStandardSize(minimumSize);
            var pool = _pools.GetOrAdd(size, _ => new ConcurrentBag<byte[]>());

            if (pool.TryTake(out var buffer))
            {
                return buffer;
            }

            return new byte[size];
        }

        /// <summary>
        /// Return a byte array to the pool.
        /// </summary>
        public static void Return(byte[] buffer)
        {
            if (buffer == null)
                return;

            var size = buffer.Length;
            if (!IsStandardSize(size))
                return; // Don't pool non-standard sizes

            var pool = _pools.GetOrAdd(size, _ => new ConcurrentBag<byte[]>());

            // Clear buffer for security
            Array.Clear(buffer, 0, buffer.Length);

            pool.Add(buffer);
        }

        private static int GetStandardSize(int minimumSize)
        {
            foreach (var size in _standardSizes)
            {
                if (size >= minimumSize)
                    return size;
            }

            // If larger than largest standard size, round up to nearest power of 2
            return RoundUpToPowerOfTwo(minimumSize);
        }

        private static bool IsStandardSize(int size)
        {
            return Array.IndexOf(_standardSizes, size) >= 0;
        }

        private static int RoundUpToPowerOfTwo(int value)
        {
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value++;
            return value;
        }

        /// <summary>
        /// Clear all pooled arrays.
        /// </summary>
        public static void Clear()
        {
            _pools.Clear();
        }
    }
}
```

### Task 10.3: Concurrency Limiter

**File:** `Runtime/Performance/ConcurrencyLimiter.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Limits concurrent operations per key (e.g., per host).
    /// </summary>
    public class ConcurrencyLimiter
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores
            = new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly int _maxConcurrent;

        public ConcurrencyLimiter(int maxConcurrent = 6)
        {
            _maxConcurrent = maxConcurrent;
        }

        /// <summary>
        /// Execute an action with concurrency limited by key.
        /// </summary>
        public async Task<T> ExecuteAsync<T>(
            string key,
            Func<Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            var semaphore = _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(_maxConcurrent, _maxConcurrent));

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await action();
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Get the current number of available slots for a key.
        /// </summary>
        public int GetAvailableSlots(string key)
        {
            if (_semaphores.TryGetValue(key, out var semaphore))
            {
                return semaphore.CurrentCount;
            }
            return _maxConcurrent;
        }

        /// <summary>
        /// Dispose all semaphores.
        /// </summary>
        public void Dispose()
        {
            foreach (var semaphore in _semaphores.Values)
            {
                semaphore.Dispose();
            }
            _semaphores.Clear();
        }
    }
}
```

### Task 10.4: Concurrency Middleware

**File:** `Runtime/Performance/ConcurrencyMiddleware.cs`

```csharp
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Middleware that limits concurrent requests per host.
    /// </summary>
    public class ConcurrencyMiddleware : IHttpMiddleware
    {
        private readonly ConcurrencyLimiter _limiter;

        public ConcurrencyMiddleware(int maxConcurrentPerHost = 6)
        {
            _limiter = new ConcurrencyLimiter(maxConcurrentPerHost);
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            var host = request.Uri.Host;
            var availableSlots = _limiter.GetAvailableSlots(host);

            if (availableSlots == 0)
            {
                context.RecordEvent("ConcurrencyLimitWaiting");
                Debug.Log($"[ConcurrencyMiddleware] Waiting for available slot for {host}");
            }

            return await _limiter.ExecuteAsync(
                host,
                () => next(request, context, cancellationToken),
                cancellationToken
            );
        }
    }
}
```

### Task 10.5: Request Queue

**File:** `Runtime/Performance/RequestQueue.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Priority for queued requests.
    /// </summary>
    public enum RequestPriority
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3
    }

    /// <summary>
    /// Queued request item.
    /// </summary>
    public class QueuedRequest
    {
        public UHttpRequest Request { get; set; }
        public RequestPriority Priority { get; set; }
        public TaskCompletionSource<UHttpResponse> CompletionSource { get; set; }
        public CancellationToken CancellationToken { get; set; }
    }

    /// <summary>
    /// Queue for managing HTTP requests with priorities.
    /// </summary>
    public class RequestQueue
    {
        private readonly ConcurrentQueue<QueuedRequest>[] _queues;
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _shutdownToken = new CancellationTokenSource();
        private readonly int _workerCount;
        private Task[] _workers;

        public RequestQueue(int workerCount = 4)
        {
            _workerCount = workerCount;
            _queues = new ConcurrentQueue<QueuedRequest>[4];
            for (int i = 0; i < _queues.Length; i++)
            {
                _queues[i] = new ConcurrentQueue<QueuedRequest>();
            }
        }

        /// <summary>
        /// Start processing queued requests.
        /// </summary>
        public void Start(Func<UHttpRequest, CancellationToken, Task<UHttpResponse>> executor)
        {
            _workers = new Task[_workerCount];
            for (int i = 0; i < _workerCount; i++)
            {
                _workers[i] = Task.Run(() => ProcessQueue(executor));
            }
        }

        /// <summary>
        /// Enqueue a request.
        /// </summary>
        public Task<UHttpResponse> EnqueueAsync(
            UHttpRequest request,
            RequestPriority priority = RequestPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<UHttpResponse>();
            var queuedRequest = new QueuedRequest
            {
                Request = request,
                Priority = priority,
                CompletionSource = tcs,
                CancellationToken = cancellationToken
            };

            _queues[(int)priority].Enqueue(queuedRequest);
            _signal.Release();

            return tcs.Task;
        }

        private async Task ProcessQueue(Func<UHttpRequest, CancellationToken, Task<UHttpResponse>> executor)
        {
            while (!_shutdownToken.Token.IsCancellationRequested)
            {
                await _signal.WaitAsync(_shutdownToken.Token);

                var request = DequeueHighestPriority();
                if (request != null)
                {
                    try
                    {
                        var response = await executor(request.Request, request.CancellationToken);
                        request.CompletionSource.SetResult(response);
                    }
                    catch (Exception ex)
                    {
                        request.CompletionSource.SetException(ex);
                    }
                }
            }
        }

        private QueuedRequest DequeueHighestPriority()
        {
            // Check queues from highest to lowest priority
            for (int i = _queues.Length - 1; i >= 0; i--)
            {
                if (_queues[i].TryDequeue(out var request))
                {
                    return request;
                }
            }
            return null;
        }

        /// <summary>
        /// Stop processing and wait for workers to finish.
        /// </summary>
        public async Task StopAsync()
        {
            _shutdownToken.Cancel();
            await Task.WhenAll(_workers);
        }

        /// <summary>
        /// Get the current queue size for a priority level.
        /// </summary>
        public int GetQueueSize(RequestPriority priority)
        {
            return _queues[(int)priority].Count;
        }
    }
}
```

### Task 10.6: Optimized Timeline

**File:** `Runtime/Core/RequestContext.cs` (update)

```csharp
// Add pooling for timeline events

private static readonly ObjectPool<TimelineEvent> _eventPool = new ObjectPool<TimelineEvent>(
    factory: () => new TimelineEvent(null, TimeSpan.Zero),
    reset: evt =>
    {
        evt.Name = null;
        evt.Data?.Clear();
    }
);

/// <summary>
/// Record a timeline event using pooled objects.
/// </summary>
public void RecordEvent(string eventName, Dictionary<string, object> data = null)
{
    var evt = _eventPool.Rent();
    evt.Name = eventName;
    evt.Timestamp = _stopwatch.Elapsed;
    evt.Data = data ?? new Dictionary<string, object>();
    _timeline.Add(evt);
}

/// <summary>
/// Return timeline events to pool when context is disposed.
/// </summary>
public void Dispose()
{
    foreach (var evt in _timeline)
    {
        _eventPool.Return(evt);
    }
    _timeline.Clear();
}
```

### Task 10.7: Disposable Pattern

**File:** `Runtime/Core/UHttpClient.cs` (update)

```csharp
using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Main HTTP client for TurboHTTP.
    /// Thread-safe and can be reused for multiple requests.
    /// </summary>
    public class UHttpClient : IDisposable
    {
        public UHttpClientOptions Options { get; }
        private readonly IHttpTransport _transport;
        private bool _disposed;

        // ... existing code ...

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // Dispose managed resources
                if (_transport is IDisposable disposableTransport)
                {
                    disposableTransport.Dispose();
                }

                foreach (var middleware in Options.Middlewares)
                {
                    if (middleware is IDisposable disposableMiddleware)
                    {
                        disposableMiddleware.Dispose();
                    }
                }
            }

            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UHttpClient));
            }
        }
    }
}
```

### Task 10.8: Stress Tests

**File:** `Tests/Runtime/Performance/StressTests.cs`

```csharp
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Performance;
using TurboHTTP.Testing;
using UnityEngine;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Stress tests for performance validation.
    /// </summary>
    public class StressTests
    {
        [Test]
        public async Task StressTest_10000_ConcurrentRequests()
        {
            var (client, transport) = TestHelpers.CreateMockClient();

            // Setup 10,000 responses
            for (int i = 0; i < 10000; i++)
            {
                transport.EnqueueResponse(new MockResponse
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Body = new byte[100]
                });
            }

            var stopwatch = Stopwatch.StartNew();

            // Execute 10,000 concurrent requests
            var tasks = Enumerable.Range(0, 10000)
                .Select(i => client.Get($"https://test.com/item/{i}").SendAsync())
                .ToArray();

            await Task.WhenAll(tasks);

            stopwatch.Stop();

            Debug.Log($"10,000 concurrent requests completed in {stopwatch.ElapsedMilliseconds}ms");
            Assert.Less(stopwatch.ElapsedMilliseconds, 30000, "Should complete within 30 seconds");
        }

        [Test]
        public async Task StressTest_ConcurrencyLimit_Works()
        {
            var middleware = new ConcurrencyMiddleware(maxConcurrentPerHost: 2);
            var (client, transport) = TestHelpers.CreateMockClient();
            client.Options.Middlewares.Add(middleware);

            for (int i = 0; i < 100; i++)
            {
                transport.EnqueueResponse(new MockResponse
                {
                    Delay = TimeSpan.FromMilliseconds(10)
                });
            }

            var stopwatch = Stopwatch.StartNew();

            // Execute 100 requests (only 2 concurrent at a time)
            var tasks = Enumerable.Range(0, 100)
                .Select(i => client.Get("https://test.com").SendAsync())
                .ToArray();

            await Task.WhenAll(tasks);

            stopwatch.Stop();

            // With 2 concurrent, 100 requests at 10ms each should take ~500ms minimum
            Debug.Log($"100 requests with concurrency=2 completed in {stopwatch.ElapsedMilliseconds}ms");
            Assert.GreaterOrEqual(stopwatch.ElapsedMilliseconds, 400);
        }

        [Test]
        public void StressTest_MemoryPool_ReducesAllocations()
        {
            GC.Collect();
            var beforeMemory = GC.GetTotalMemory(true);

            // Rent and return 1000 buffers
            for (int i = 0; i < 1000; i++)
            {
                var buffer = ByteArrayPool.Rent(8192);
                ByteArrayPool.Return(buffer);
            }

            var afterMemory = GC.GetTotalMemory(false);
            var allocated = afterMemory - beforeMemory;

            Debug.Log($"Memory allocated for 1000 buffer operations: {allocated} bytes");
            Assert.Less(allocated, 100000, "Should allocate less than 100KB with pooling");
        }
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] Object pool works correctly (rent/return)
- [ ] Byte array pool reduces GC allocations
- [ ] Concurrency limiter prevents too many concurrent requests
- [ ] Request queue processes requests by priority
- [ ] Stress test with 10,000 requests completes successfully
- [ ] Memory usage stays below 10MB for typical workloads
- [ ] GC allocations are < 1KB per request
- [ ] Dispose pattern implemented correctly

### Performance Targets

- **Throughput:** 1000+ requests/second (with mock transport)
- **Memory:** < 1KB GC allocation per request
- **Concurrency:** Handle 10,000 concurrent requests
- **Latency:** < 1ms overhead per request (pipeline + middleware)

## Next Steps

Once Phase 10 is complete and validated:

1. Move to [Phase 11: Platform Compatibility](phase-11-platform-compat.md)
2. Test on all target platforms
3. Fix platform-specific issues
4. Validate IL2CPP compatibility
5. M3 milestone near completion

## Notes

- Memory pooling dramatically reduces GC pressure
- Concurrency limiting prevents connection exhaustion
- Request queue enables priority-based execution
- Stress tests validate production readiness
- Proper disposal prevents resource leaks
- Performance targets based on industry standards
