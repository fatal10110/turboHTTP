using System;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class HpackStaticTableTests
    {
        [Test]
        public void Get_Index1_ReturnsAuthority()
        {
            var (name, value) = HpackStaticTable.Get(1);
            Assert.AreEqual(":authority", name);
            Assert.AreEqual("", value);
        }

        [Test]
        public void Get_Index2_ReturnsMethodGet()
        {
            var (name, value) = HpackStaticTable.Get(2);
            Assert.AreEqual(":method", name);
            Assert.AreEqual("GET", value);
        }

        [Test]
        public void Get_Index7_ReturnsSchemeHttps()
        {
            var (name, value) = HpackStaticTable.Get(7);
            Assert.AreEqual(":scheme", name);
            Assert.AreEqual("https", value);
        }

        [Test]
        public void Get_Index61_ReturnsWwwAuthenticate()
        {
            var (name, value) = HpackStaticTable.Get(61);
            Assert.AreEqual("www-authenticate", name);
            Assert.AreEqual("", value);
        }

        [Test]
        public void Get_Index0_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => HpackStaticTable.Get(0));
        }

        [Test]
        public void Get_Index62_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => HpackStaticTable.Get(62));
        }

        [Test]
        public void FindMatch_MethodGet_FullMatch_Index2()
        {
            var (index, match) = HpackStaticTable.FindMatch(":method", "GET");
            Assert.AreEqual(2, index);
            Assert.AreEqual(HpackMatchType.FullMatch, match);
        }

        [Test]
        public void FindMatch_MethodPut_NameMatch_Index2()
        {
            var (index, match) = HpackStaticTable.FindMatch(":method", "PUT");
            Assert.AreEqual(2, index);
            Assert.AreEqual(HpackMatchType.NameMatch, match);
        }

        [Test]
        public void FindMatch_Status200_FullMatch_Index8()
        {
            var (index, match) = HpackStaticTable.FindMatch(":status", "200");
            Assert.AreEqual(8, index);
            Assert.AreEqual(HpackMatchType.FullMatch, match);
        }

        [Test]
        public void FindMatch_CustomHeader_None()
        {
            var (index, match) = HpackStaticTable.FindMatch("x-custom", "foo");
            Assert.AreEqual(0, index);
            Assert.AreEqual(HpackMatchType.None, match);
        }

        [Test]
        public void FindMatch_AuthorityEmpty_FullMatch_Index1()
        {
            var (index, match) = HpackStaticTable.FindMatch(":authority", "");
            Assert.AreEqual(1, index);
            Assert.AreEqual(HpackMatchType.FullMatch, match);
        }

        [Test]
        public void TableHas61Entries()
        {
            Assert.AreEqual(61, HpackStaticTable.Length);
        }
    }
}
