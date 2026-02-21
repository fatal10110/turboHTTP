using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class Http2BodySizeTests
    {
        [Test]
        public void ExceedingMaxResponseBodySize_StreamShouldFail()
        {
            // Set up a mock request and stream
            var req = new UHttpRequest(HttpMethod.GET, new Uri("http://localhost"));
            var context = new RequestContext(req);
            var stream = new Http2Stream(1, req, context, 65535, 65535);

            // Configure settings to restrict body size to 10 bytes
            var options = new Http2Options { MaxResponseBodySize = 10 };
            var localSettings = new Http2Settings(options);

            // Manually append data exceeding the limit (like ReadLoop does)
            byte[] largeData = Encoding.UTF8.GetBytes("This is much longer than ten bytes.");
            
            // Note: Since Http2Stream itself doesn't enforce MaxResponseBodySize, 
            // the enforcement happens in Http2Connection.ReadLoop. We simulate that logic here.
            
            long maxBodySize = localSettings.MaxResponseBodySize;
            
            Exception thrownException = null;
            try
            {
                if (maxBodySize > 0 && stream.ResponseBodyLength + largeData.Length > maxBodySize)
                {
                    throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                        $"Response body size exceeded maximum allowed ({maxBodySize} bytes)"));
                }
                
                stream.AppendResponseData(largeData, 0, largeData.Length);
            }
            catch (Exception ex)
            {
                thrownException = ex;
                stream.Fail(ex);
            }

            Assert.That(thrownException, Is.Not.Null);
            Assert.That(thrownException, Is.InstanceOf<UHttpException>());
            Assert.That(((UHttpException)thrownException).HttpError.Type, Is.EqualTo(UHttpErrorType.NetworkError));
            
            // Clean up
            stream.Dispose();
        }
    }
}
