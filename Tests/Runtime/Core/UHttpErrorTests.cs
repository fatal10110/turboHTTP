using NUnit.Framework;
using System.Net;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class UHttpErrorTests
    {
        [Test]
        public void IsRetryable_ReturnsTrue_ForNetworkErrors()
        {
            var error = new UHttpError(UHttpErrorType.NetworkError, "Connection failed");
            Assert.IsTrue(error.IsRetryable());
        }

        [Test]
        public void IsRetryable_ReturnsTrue_ForTimeouts()
        {
            var error = new UHttpError(UHttpErrorType.Timeout, "Request timeout");
            Assert.IsTrue(error.IsRetryable());
        }

        [Test]
        public void IsRetryable_ReturnsTrue_For5xxErrors()
        {
            var error = new UHttpError(
                UHttpErrorType.HttpError,
                "Server error",
                statusCode: HttpStatusCode.InternalServerError
            );
            Assert.IsTrue(error.IsRetryable());
        }

        [Test]
        public void IsRetryable_ReturnsFalse_For4xxErrors()
        {
            var error = new UHttpError(
                UHttpErrorType.HttpError,
                "Not found",
                statusCode: HttpStatusCode.NotFound
            );
            Assert.IsFalse(error.IsRetryable());
        }

        [Test]
        public void IsRetryable_ReturnsFalse_ForCancelled()
        {
            var error = new UHttpError(UHttpErrorType.Cancelled, "User cancelled");
            Assert.IsFalse(error.IsRetryable());
        }
    }
}
