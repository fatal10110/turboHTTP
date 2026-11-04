using System;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// #85: the shared cleartext decision (AllowCleartext and the injected platform policy) and
    /// the Proxy-Authenticate scheme reader (RFC 9110 section 11.6.1 / 11.7.1).
    /// </summary>
    [TestFixture]
    public class CleartextTrafficPolicyTests
    {
        [Test]
        public void AllowCleartextFalse_RefusesWithoutAskingThePlatform()
        {
            bool asked = false;
            Assert.IsFalse(CleartextTrafficPolicy.IsPermitted("example.com", false, _ => asked = true));
            Assert.IsFalse(asked);
        }

        [Test]
        public void AllowCleartextTrue_NoPlatformPolicy_Permits()
        {
            Assert.IsTrue(CleartextTrafficPolicy.IsPermitted("example.com", true, null));
        }

        [Test]
        public void AllowCleartextTrue_AsksInjectedPlatformPolicyForTheHost()
        {
            string askedHost = null;
            Func<string, bool> policy = host =>
            {
                askedHost = host;
                return host == "allowed.example";
            };

            Assert.IsTrue(CleartextTrafficPolicy.IsPermitted("allowed.example", true, policy));
            Assert.AreEqual("allowed.example", askedHost);
            Assert.IsFalse(CleartextTrafficPolicy.IsPermitted("denied.example", true, policy));
            Assert.AreEqual("denied.example", askedHost);
        }

        [Test]
        public void ThrowingPlatformPolicy_FailsClosed()
        {
            Assert.IsFalse(CleartextTrafficPolicy.IsPermitted(
                "example.com", true, _ => throw new InvalidOperationException("JNI unavailable")));
        }

        [TestCase("Basic realm=\"proxy\"", true)]
        [TestCase("basic", true)]
        [TestCase("Digest realm=\"a, b\", nonce=\"n\", Basic realm=\"p\"", true)]
        [TestCase("Negotiate abc==, BASIC realm=x", true)]
        [TestCase("Digest realm=\"proxy\", nonce=\"abc\"", false)]
        [TestCase("Negotiate", false)]
        [TestCase("NTLM, Negotiate abc==", false)]
        [TestCase("Digest realm=\"Basic, realm=x\", basic=1", false)]
        [TestCase("Digest realm=x, basic = \"x\"", false)]
        [TestCase("Basically realm=x", false)]
        [TestCase("Digest realm=\"unterminated\\", false)]
        [TestCase("", false)]
        public void OffersScheme_ReadsChallengeList(string fieldValue, bool expected)
        {
            Assert.AreEqual(expected, AuthChallengeParser.OffersScheme(new[] { fieldValue }, "Basic"));
        }

        [Test]
        public void OffersScheme_ChecksEveryFieldLine()
        {
            Assert.IsTrue(AuthChallengeParser.OffersScheme(new[] { "Negotiate", "Basic realm=\"p\"" }, "Basic"));
            Assert.IsFalse(AuthChallengeParser.OffersScheme(Array.Empty<string>(), "Basic"));
            Assert.IsFalse(AuthChallengeParser.OffersScheme(null, "Basic"));
        }
    }
}
