using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class HttpMethodTests
    {
        [Test]
        public void IsIdempotent_ReturnsTrue_ForIdempotentMethods()
        {
            Assert.IsTrue(HttpMethod.GET.IsIdempotent());
            Assert.IsTrue(HttpMethod.PUT.IsIdempotent());
            Assert.IsTrue(HttpMethod.DELETE.IsIdempotent());
            Assert.IsTrue(HttpMethod.HEAD.IsIdempotent());
            Assert.IsTrue(HttpMethod.OPTIONS.IsIdempotent());
        }

        [Test]
        public void IsIdempotent_ReturnsFalse_ForNonIdempotentMethods()
        {
            Assert.IsFalse(HttpMethod.POST.IsIdempotent());
            Assert.IsFalse(HttpMethod.PATCH.IsIdempotent());
        }

        [Test]
        public void HasBody_ReturnsTrue_ForMethodsWithBody()
        {
            Assert.IsTrue(HttpMethod.POST.HasBody());
            Assert.IsTrue(HttpMethod.PUT.HasBody());
            Assert.IsTrue(HttpMethod.PATCH.HasBody());
        }

        [Test]
        public void HasBody_ReturnsFalse_ForMethodsWithoutBody()
        {
            Assert.IsFalse(HttpMethod.GET.HasBody());
            Assert.IsFalse(HttpMethod.DELETE.HasBody());
            Assert.IsFalse(HttpMethod.HEAD.HasBody());
        }
    }
}
