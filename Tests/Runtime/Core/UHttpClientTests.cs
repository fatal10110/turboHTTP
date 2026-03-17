using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public partial class UHttpClientTests
    {
        private sealed class TrackingTransport : IHttpTransport
        {
            public bool Disposed { get; private set; }
            public Func<UHttpRequest, RequestContext, CancellationToken, ValueTask<UHttpResponse>> OnSendAsync { get; set; }

            public ValueTask<UHttpResponse> SendAsync(UHttpRequest request, RequestContext context, CancellationToken cancellationToken = default)
            {
                if (OnSendAsync != null)
                    return OnSendAsync(request, context, cancellationToken);

                return new ValueTask<UHttpResponse>(new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    TimeSpan.Zero,
                    request));
            }

            public async Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                handler.OnRequestStart(request, context);

                UHttpResponse response = null;
                try
                {
                    response = await SendAsync(request, context, cancellationToken).ConfigureAwait(false);
                    EmitResponse(response, handler, context);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UHttpException ex) when (ex.HttpError != null && ex.HttpError.Type == UHttpErrorType.Cancelled)
                {
                    throw new OperationCanceledException(ex.HttpError.Message, ex, cancellationToken);
                }
                catch (UHttpException ex)
                {
                    handler.OnResponseError(ex, context);
                }
                catch (Exception ex)
                {
                    handler.OnResponseError(new UHttpException(
                        new UHttpError(UHttpErrorType.Unknown, ex.Message, ex)), context);
                }
                finally
                {
                    response?.Dispose();
                }
            }

            private static void EmitResponse(
                UHttpResponse response,
                IHttpHandler handler,
                RequestContext context)
            {
                if (response == null)
                    throw new InvalidOperationException("TrackingTransport produced a null response.");

                if (response.Error != null)
                {
                    handler.OnResponseError(new UHttpException(response.Error), context);
                    return;
                }

                handler.OnResponseStart((int)response.StatusCode, response.Headers, context);

                var body = response.Body;
                if (body.IsSingleSegment)
                {
                    if (!body.FirstSpan.IsEmpty)
                        handler.OnResponseData(body.FirstSpan, context);
                }
                else
                {
                    foreach (ReadOnlyMemory<byte> segment in body)
                    {
                        if (!segment.Span.IsEmpty)
                            handler.OnResponseData(segment.Span, context);
                    }
                }

                handler.OnResponseEnd(HttpHeaders.Empty, context);
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private sealed class TrackingDisposeInterceptor : IHttpInterceptor, IDisposable
        {
            private readonly List<string> _disposeOrder;
            private readonly string _name;
            private readonly bool _throwOnDispose;

            public bool Disposed { get; private set; }

            public TrackingDisposeInterceptor(List<string> disposeOrder, string name, bool throwOnDispose = false)
            {
                _disposeOrder = disposeOrder;
                _name = name;
                _throwOnDispose = throwOnDispose;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return async (request, handler, context, cancellationToken) =>
                {
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                };
            }

            public void Dispose()
            {
                Disposed = true;
                _disposeOrder?.Add(_name);

                if (_throwOnDispose)
                    throw new InvalidOperationException($"{_name} dispose failed");
            }
        }

        private sealed class TrackingDisposeTransport : IHttpTransport
        {
            private readonly List<string> _disposeOrder;
            private readonly string _name;
            private readonly bool _throwOnDispose;

            public bool Disposed { get; private set; }

            public TrackingDisposeTransport(List<string> disposeOrder, string name = "transport", bool throwOnDispose = false)
            {
                _disposeOrder = disposeOrder;
                _name = name;
                _throwOnDispose = throwOnDispose;
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return new ValueTask<UHttpResponse>(new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    TimeSpan.Zero,
                    request));
            }

            public async Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                handler.OnRequestStart(request, context);

                UHttpResponse response = null;
                try
                {
                    response = await SendAsync(request, context, cancellationToken).ConfigureAwait(false);
                    handler.OnResponseStart((int)response.StatusCode, response.Headers, context);
                    if (!response.Body.IsEmpty)
                        handler.OnResponseData(response.Body.FirstSpan, context);
                    handler.OnResponseEnd(HttpHeaders.Empty, context);
                }
                finally
                {
                    response?.Dispose();
                }
            }

            public void Dispose()
            {
                Disposed = true;
                _disposeOrder?.Add(_name);

                if (_throwOnDispose)
                    throw new InvalidOperationException($"{_name} dispose failed");
            }
        }

        private sealed class ErrorObservingInterceptor : IHttpInterceptor
        {
            private readonly Action<UHttpException> _onError;

            public ErrorObservingInterceptor(Action<UHttpException> onError)
            {
                _onError = onError;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new ErrorObservingHandler(handler, _onError), context, cancellationToken);
            }

            private sealed class ErrorObservingHandler : IHttpHandler
            {
                private readonly IHttpHandler _inner;
                private readonly Action<UHttpException> _onError;

                public ErrorObservingHandler(IHttpHandler inner, Action<UHttpException> onError)
                {
                    _inner = inner;
                    _onError = onError;
                }

                public void OnRequestStart(UHttpRequest request, RequestContext context)
                {
                    _inner.OnRequestStart(request, context);
                }

                public ValueTask OnResponseStartAsync(
                    int statusCode,
                    HttpHeaders headers,
                    IResponseBodySource body,
                    RequestContext context)
                {
                    return _inner.OnResponseStartAsync(statusCode, headers, body, context);
                }

                public void OnResponseError(UHttpException error, RequestContext context)
                {
                    _onError?.Invoke(error);
                    _inner.OnResponseError(error, context);
                }
            }
        }

        [SetUp]
        public void SetUp()
        {
            HttpTransportFactory.Reset();
            RawSocketTransport.EnsureRegistered();
        }

        [TearDown]
        public void TearDown()
        {
            HttpTransportFactory.Reset();
        }

    }
}
