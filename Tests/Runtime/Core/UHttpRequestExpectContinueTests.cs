using System;
using NUnit.Framework;
using TurboHTTP.Core;
using CoreHttpMethod = TurboHTTP.Core.HttpMethod;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class UHttpRequestExpectContinueTests
    {
        [Test]
        public void WithExpectContinue_Enabled_SetsHeader()
        {
            var request = new UHttpRequest(CoreHttpMethod.POST, new Uri("https://example.test/upload"))
                .WithBody("payload")
                .WithExpectContinue();

            Assert.AreEqual("100-continue", request.Headers.Get("Expect"));
        }

        [Test]
        public void WithExpectContinue_Disabled_RemovesHeader()
        {
            var request = new UHttpRequest(CoreHttpMethod.POST, new Uri("https://example.test/upload"))
                .WithBody("payload")
                .WithExpectContinue()
                .WithExpectContinue(false);

            Assert.IsFalse(request.Headers.Contains("Expect"));
        }

        [Test]
        public void WithExpectContinue_BodylessRequest_StillSetsHeader()
        {
            var request = new UHttpRequest(CoreHttpMethod.GET, new Uri("https://example.test/"));
            request.WithExpectContinue();

            Assert.AreEqual("100-continue", request.Headers.Get("Expect"));
        }
    }
}
