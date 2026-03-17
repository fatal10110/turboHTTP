using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Cache
{
    [TestFixture]
    public partial class CacheInterceptorTests
    {
        private static async Task WaitUntilAsync(
            Func<Task<bool>> predicate,
            TimeSpan timeout,
            string failureMessage)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await predicate().ConfigureAwait(false))
                    return;

                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.Fail(failureMessage);
        }

        private sealed class DelayedSetCacheStorage : ICacheStorage, IDisposable
        {
            private readonly MemoryCacheStorage _inner = new MemoryCacheStorage();
            private readonly TaskCompletionSource<object> _allowSet =
                new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ManualResetEventSlim SetStarted { get; } = new ManualResetEventSlim(false);

            public Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default)
            {
                return _inner.GetAsync(key, cancellationToken);
            }

            public async Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
            {
                SetStarted.Set();
                using (cancellationToken.Register(() => _allowSet.TrySetCanceled(cancellationToken)))
                {
                    await _allowSet.Task.ConfigureAwait(false);
                }

                await _inner.SetAsync(key, entry, cancellationToken).ConfigureAwait(false);
            }

            public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
            {
                return _inner.RemoveAsync(key, cancellationToken);
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
            {
                return _inner.ClearAsync(cancellationToken);
            }

            public Task<int> GetCountAsync(CancellationToken cancellationToken = default)
            {
                return _inner.GetCountAsync(cancellationToken);
            }

            public Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
            {
                return _inner.GetSizeAsync(cancellationToken);
            }

            internal void ReleaseSet()
            {
                _allowSet.TrySetResult(null);
            }

            public void Dispose()
            {
                _allowSet.TrySetCanceled();
                SetStarted.Dispose();
                _inner.Dispose();
            }
        }

        private sealed class BlockingCacheStorage : ICacheStorage
        {
            private readonly TaskCompletionSource<object> _allowSet =
                new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ManualResetEventSlim SetStarted { get; } = new ManualResetEventSlim(false);
            internal TaskCompletionSource<object> SetCompleted { get; } =
                new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<CacheEntry>(null);
            }

            public async Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
            {
                SetStarted.Set();
                using (cancellationToken.Register(() => _allowSet.TrySetCanceled(cancellationToken)))
                {
                    await _allowSet.Task.ConfigureAwait(false);
                }

                SetCompleted.TrySetResult(null);
            }

            public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<int> GetCountAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(0);
            }

            public Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(0L);
            }

            internal void ReleaseSet()
            {
                _allowSet.TrySetResult(null);
            }
        }

        private sealed class FailingDelayedSetCacheStorage : ICacheStorage, IDisposable
        {
            private readonly MemoryCacheStorage _inner = new MemoryCacheStorage();
            private readonly TaskCompletionSource<object> _allowSet =
                new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ManualResetEventSlim SetStarted { get; } = new ManualResetEventSlim(false);

            public Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default)
            {
                return _inner.GetAsync(key, cancellationToken);
            }

            public async Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
            {
                SetStarted.Set();
                using (cancellationToken.Register(() => _allowSet.TrySetCanceled(cancellationToken)))
                {
                    await _allowSet.Task.ConfigureAwait(false);
                }

                throw new InvalidOperationException("Simulated cache store failure.");
            }

            public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
            {
                return _inner.RemoveAsync(key, cancellationToken);
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
            {
                return _inner.ClearAsync(cancellationToken);
            }

            public Task<int> GetCountAsync(CancellationToken cancellationToken = default)
            {
                return _inner.GetCountAsync(cancellationToken);
            }

            public Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
            {
                return _inner.GetSizeAsync(cancellationToken);
            }

            internal void FailSet()
            {
                _allowSet.TrySetResult(null);
            }

            public void Dispose()
            {
                _allowSet.TrySetCanceled();
                SetStarted.Dispose();
                _inner.Dispose();
            }
        }

        private sealed class BlockingEndHandler : IHttpHandler, IDisposable
        {
            private readonly ManualResetEventSlim _allowEnd = new ManualResetEventSlim(false);

            internal ManualResetEventSlim EndEntered { get; } = new ManualResetEventSlim(false);

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                EndEntered.Set();
                _allowEnd.Wait(TimeSpan.FromSeconds(5));
                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
            }

            internal void ReleaseEnd()
            {
                _allowEnd.Set();
            }

            public void Dispose()
            {
                _allowEnd.Set();
                _allowEnd.Dispose();
                EndEntered.Dispose();
            }
        }
    }
}
