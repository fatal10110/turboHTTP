using NUnit.Framework;
using System.Collections.Generic;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class HttpHeadersTests
    {
        [Test]
        public void Get_IsCaseInsensitive()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "application/json");

            Assert.AreEqual("application/json", headers.Get("content-type"));
            Assert.AreEqual("application/json", headers.Get("CONTENT-TYPE"));
            Assert.AreEqual("application/json", headers.Get("Content-Type"));
        }

        [Test]
        public void Set_OverwritesExistingValue()
        {
            var headers = new HttpHeaders();
            headers.Set("Accept", "text/html");
            headers.Set("Accept", "application/json");

            Assert.AreEqual("application/json", headers.Get("Accept"));
        }

        [Test]
        public void Clone_CreatesDeepCopy()
        {
            var headers = new HttpHeaders();
            headers.Set("Authorization", "Bearer token123");

            var clone = headers.Clone();
            clone.Set("Authorization", "Bearer newtoken");

            Assert.AreEqual("Bearer token123", headers.Get("Authorization"));
            Assert.AreEqual("Bearer newtoken", clone.Get("Authorization"));
        }

        [Test]
        public void Enumerator_YieldsAllValuesForMultiValueHeaders()
        {
            var headers = new HttpHeaders();
            headers.Add("Set-Cookie", "a=1");
            headers.Add("Set-Cookie", "b=2");

            var enumerated = new List<KeyValuePair<string, string>>(headers);

            Assert.AreEqual(2, enumerated.Count);
            Assert.AreEqual("Set-Cookie", enumerated[0].Key);
            Assert.AreEqual("a=1", enumerated[0].Value);
            Assert.AreEqual("Set-Cookie", enumerated[1].Key);
            Assert.AreEqual("b=2", enumerated[1].Value);
        }
    }
}
