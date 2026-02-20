using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    public sealed class MockRoute
    {
        public HttpMethod Method { get; set; }
        public string PathPattern { get; set; }
        public Func<MockRequestContext, ValueTask<MockResponse>> Handler { get; set; }
        public int? RemainingInvocations { get; internal set; }
        public int Priority { get; set; }

        internal string RouteId { get; set; }
        internal long RegistrationOrder { get; set; }
        internal List<Func<MockRequestContext, bool>> Matchers { get; } = new List<Func<MockRequestContext, bool>>();
    }

    public sealed class MockResponse
    {
        public int StatusCode { get; set; } = 200;
        public HttpHeaders Headers { get; set; } = new HttpHeaders();
        public byte[] Body { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    }

    public sealed class MockRequestContext
    {
        public MockRequestContext(UHttpRequest request, RequestContext pipelineContext, CancellationToken cancellationToken)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            PipelineContext = pipelineContext ?? throw new ArgumentNullException(nameof(pipelineContext));
            CancellationToken = cancellationToken;
        }

        public UHttpRequest Request { get; }
        public RequestContext PipelineContext { get; }
        public CancellationToken CancellationToken { get; }

        public string Path => Request.Uri.AbsolutePath;
        public string Query => Request.Uri.Query;
        public HttpHeaders Headers => Request.Headers;
        public ReadOnlyMemory<byte> Body => Request.Body ?? Array.Empty<byte>();
    }

    public sealed class MockHistoryEntry
    {
        public DateTime TimestampUtc { get; set; }
        public HttpMethod Method { get; set; }
        public string Path { get; set; }
        public HttpHeaders RequestHeaders { get; set; }
        public byte[] RequestBody { get; set; }
        public string RouteId { get; set; }
        public int ResponseStatusCode { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
