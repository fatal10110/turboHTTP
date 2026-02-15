using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Main HTTP client for TurboHTTP. Provides fluent verb methods for building
    /// and sending HTTP requests. Thread-safe for concurrent use.
    /// </summary>
    public class UHttpClient : IDisposable
    {
        private readonly UHttpClientOptions _options;
        private readonly IHttpTransport _transport;
        private readonly bool _ownsTransport;
        private readonly HttpPipeline _pipeline;
        private int _disposed; // 0 = not disposed, 1 = disposed (for Interlocked)

        /// <summary>
        /// The snapshotted options for this client (read-only access for builder).
        /// </summary>
        internal UHttpClientOptions ClientOptions => _options;

        /// <summary>
        /// Create a new HTTP client with optional configuration.
        /// Options are snapshotted at construction — mutations after this call
        /// have no effect. Transport is a shared reference (not cloned).
        /// </summary>
        public UHttpClient(UHttpClientOptions options = null)
        {
            _options = options?.Clone() ?? new UHttpClientOptions();

            if (_options.Http2MaxDecodedHeaderBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(_options.Http2MaxDecodedHeaderBytes),
                    _options.Http2MaxDecodedHeaderBytes,
                    "Must be greater than 0.");
            }

            if (_options.Transport != null)
            {
                _transport = _options.Transport;
                _ownsTransport = _options.DisposeTransport;
            }
            else if (_options.TlsBackend != TlsBackend.Auto ||
                _options.Http2MaxDecodedHeaderBytes != UHttpClientOptions.DefaultHttp2MaxDecodedHeaderBytes)
            {
                // Non-default TLS backend or custom transport hardening options require
                // a dedicated transport instance because the shared default singleton
                // uses default configuration.
                _transport = HttpTransportFactory.CreateWithOptions(
                    _options.TlsBackend,
                    _options.Http2MaxDecodedHeaderBytes);
                _ownsTransport = true;
            }
            else
            {
                _transport = HttpTransportFactory.Default;
                _ownsTransport = false;
            }

            _pipeline = new HttpPipeline(_options.Middlewares, _transport);
        }

        public UHttpRequestBuilder Get(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.GET, url);
        }

        public UHttpRequestBuilder Post(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.POST, url);
        }

        public UHttpRequestBuilder Put(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.PUT, url);
        }

        public UHttpRequestBuilder Delete(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.DELETE, url);
        }

        public UHttpRequestBuilder Patch(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.PATCH, url);
        }

        public UHttpRequestBuilder Head(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.HEAD, url);
        }

        public UHttpRequestBuilder Options(string url)
        {
            ThrowIfDisposed();
            return new UHttpRequestBuilder(this, HttpMethod.OPTIONS, url);
        }

        /// <summary>
        /// Send an HTTP request and return the response.
        /// Does NOT use ConfigureAwait(false) — continuations return to the
        /// caller's SynchronizationContext (typically Unity main thread).
        /// </summary>
        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ThrowIfDisposed();

            var context = new RequestContext(request);
            context.RecordEvent("RequestStart");

            try
            {
                var response = await _pipeline.ExecuteAsync(request, context, cancellationToken);

                context.RecordEvent("RequestComplete");
                context.Stop();

                return response;
            }
            catch (UHttpException)
            {
                context.RecordEvent("RequestFailed");
                context.Stop();
                throw;
            }
            catch (OperationCanceledException)
            {
                context.RecordEvent("RequestCancelled");
                context.Stop();
                throw;
            }
            catch (Exception ex)
            {
                context.RecordEvent("RequestFailed");
                context.Stop();
                throw new UHttpException(
                    new UHttpError(UHttpErrorType.Unknown, ex.Message, ex));
            }
            finally
            {
                context.Clear();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            GC.SuppressFinalize(this);

            List<Exception> disposeErrors = null;

            if (_options.Middlewares != null)
            {
                // Dispose in reverse registration order (LIFO).
                for (int i = _options.Middlewares.Count - 1; i >= 0; i--)
                {
                    if (!(_options.Middlewares[i] is IDisposable disposable))
                        continue;

                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        if (disposeErrors == null)
                            disposeErrors = new List<Exception>();
                        disposeErrors.Add(ex);
                    }
                }
            }

            // Transport is disposed last.
            if (_ownsTransport)
            {
                try
                {
                    _transport.Dispose();
                }
                catch (Exception ex)
                {
                    if (disposeErrors == null)
                        disposeErrors = new List<Exception>();
                    disposeErrors.Add(ex);
                }
            }

            if (disposeErrors != null && disposeErrors.Count > 0)
                throw new AggregateException("One or more errors occurred while disposing UHttpClient.", disposeErrors);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UHttpClient));
        }
    }
}
