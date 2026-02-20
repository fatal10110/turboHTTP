using System;
using System.Net;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    public sealed class MockResponseBuilder
    {
        private readonly MockResponse _response = new MockResponse();

        public MockResponseBuilder Status(HttpStatusCode statusCode)
        {
            _response.StatusCode = (int)statusCode;
            return this;
        }

        public MockResponseBuilder Status(int statusCode)
        {
            _response.StatusCode = statusCode;
            return this;
        }

        public MockResponseBuilder Header(string name, string value)
        {
            _response.Headers.Set(name, value);
            return this;
        }

        public MockResponseBuilder Body(byte[] body)
        {
            _response.Body = body != null ? (byte[])body.Clone() : null;
            return this;
        }

        public MockResponseBuilder Text(string text)
        {
            _response.Body = text == null ? null : Encoding.UTF8.GetBytes(text);
            if (!_response.Headers.Contains("Content-Type"))
                _response.Headers.Set("Content-Type", "text/plain; charset=utf-8");
            return this;
        }

        public MockResponseBuilder Json<T>(T payload)
        {
            var json = SerializeViaProjectJson(payload, typeof(T));
            _response.Body = Encoding.UTF8.GetBytes(json);
            _response.Headers.Set("Content-Type", "application/json");
            return this;
        }

        public MockResponseBuilder Delay(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be >= 0.");
            _response.Delay = delay;
            return this;
        }

        public MockResponse Build()
        {
            return new MockResponse
            {
                StatusCode = _response.StatusCode,
                Headers = _response.Headers.Clone(),
                Body = _response.Body != null ? (byte[])_response.Body.Clone() : null,
                Delay = _response.Delay
            };
        }

        private static string SerializeViaProjectJson(object payload, Type payloadType)
        {
            return ProjectJsonBridge.Serialize(
                payload,
                payloadType,
                requiredBy: "MockResponseBuilder.Json(...)");
        }
    }
}
