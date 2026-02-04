using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class HpackDynamicTableTests
    {
        [Test]
        public void Add_SingleEntry_AtIndex62()
        {
            var table = new HpackDynamicTable();
            table.Add("custom-key", "custom-value");

            var (name, value) = table.Get(62);
            Assert.AreEqual("custom-key", name);
            Assert.AreEqual("custom-value", value);
        }

        [Test]
        public void Add_TwoEntries_NewestAtIndex62()
        {
            var table = new HpackDynamicTable();
            table.Add("first-key", "first-value");
            table.Add("second-key", "second-value");

            var (name62, _) = table.Get(62);
            var (name63, _) = table.Get(63);
            Assert.AreEqual("second-key", name62); // Newest at 62
            Assert.AreEqual("first-key", name63);  // Older at 63
        }

        [Test]
        public void Add_Eviction_OldestRemoved()
        {
            // Small table: 64 bytes max
            // Entry size = name.Length + value.Length + 32
            // "a" + "b" + 32 = 34 bytes
            var table = new HpackDynamicTable(68); // fits 2 entries of size 34
            table.Add("a", "b"); // 34 bytes
            table.Add("c", "d"); // 34 bytes, total 68
            Assert.AreEqual(2, table.Count);

            table.Add("e", "f"); // 34 bytes, evicts oldest ("a","b")
            Assert.AreEqual(2, table.Count);

            var (name62, _) = table.Get(62);
            Assert.AreEqual("e", name62);
            var (name63, _) = table.Get(63);
            Assert.AreEqual("c", name63);
        }

        [Test]
        public void Add_EntrySizeExceedsMax_ClearsTable()
        {
            var table = new HpackDynamicTable(40);
            table.Add("a", "b"); // 34 bytes, fits

            // Entry: name(50) + value(50) + 32 = 132, exceeds 40
            table.Add(new string('x', 50), new string('y', 50));
            Assert.AreEqual(0, table.Count);
            Assert.AreEqual(0, table.CurrentSize);
        }

        [Test]
        public void Get_StaticRange_DelegatesToStaticTable()
        {
            var table = new HpackDynamicTable();
            var (name, value) = table.Get(2);
            Assert.AreEqual(":method", name);
            Assert.AreEqual("GET", value);
        }

        [Test]
        public void Get_DynamicRange_ReturnsCorrectEntry()
        {
            var table = new HpackDynamicTable();
            table.Add("my-header", "my-value");
            var (name, value) = table.Get(62);
            Assert.AreEqual("my-header", name);
            Assert.AreEqual("my-value", value);
        }

        [Test]
        public void Get_OutOfRange_Throws()
        {
            var table = new HpackDynamicTable();
            Assert.Throws<HpackDecodingException>(() => table.Get(62)); // Empty dynamic table
        }

        [Test]
        public void Get_Index0_Throws()
        {
            var table = new HpackDynamicTable();
            Assert.Throws<HpackDecodingException>(() => table.Get(0));
        }

        [Test]
        public void FindMatch_FullMatch_DynamicTable()
        {
            var table = new HpackDynamicTable();
            table.Add("x-custom", "hello");

            var (index, match) = table.FindMatch("x-custom", "hello");
            Assert.AreEqual(62, index);
            Assert.AreEqual(HpackMatchType.FullMatch, match);
        }

        [Test]
        public void FindMatch_NameMatch_DynamicTable()
        {
            var table = new HpackDynamicTable();
            table.Add("x-custom", "hello");

            var (index, match) = table.FindMatch("x-custom", "world");
            Assert.AreEqual(62, index);
            Assert.AreEqual(HpackMatchType.NameMatch, match);
        }

        [Test]
        public void FindMatch_PrefersStaticFullMatch()
        {
            var table = new HpackDynamicTable();
            table.Add(":method", "GET"); // Also in static table at index 2

            var (index, match) = table.FindMatch(":method", "GET");
            Assert.AreEqual(2, index); // Static table preferred
            Assert.AreEqual(HpackMatchType.FullMatch, match);
        }

        [Test]
        public void SetMaxSize_Zero_ClearsAll()
        {
            var table = new HpackDynamicTable();
            table.Add("a", "b");
            table.Add("c", "d");
            Assert.AreEqual(2, table.Count);

            table.SetMaxSize(0);
            Assert.AreEqual(0, table.Count);
            Assert.AreEqual(0, table.CurrentSize);
        }

        [Test]
        public void SetMaxSize_Reduced_Evicts()
        {
            var table = new HpackDynamicTable(200);
            table.Add("a", "b"); // 34 bytes
            table.Add("c", "d"); // 34 bytes
            Assert.AreEqual(2, table.Count);

            table.SetMaxSize(34); // Only room for 1 entry
            Assert.AreEqual(1, table.Count);

            var (name, _) = table.Get(62);
            Assert.AreEqual("c", name); // Newest survives
        }

        [Test]
        public void EntrySize_NamePlusValuePlus32()
        {
            var table = new HpackDynamicTable(100);
            table.Add("abc", "def"); // 3 + 3 + 32 = 38
            Assert.AreEqual(38, table.CurrentSize);
        }

        [Test]
        public void CurrentSize_TracksCorrectly()
        {
            var table = new HpackDynamicTable(200);
            Assert.AreEqual(0, table.CurrentSize);

            table.Add("a", "b"); // 34
            Assert.AreEqual(34, table.CurrentSize);

            table.Add("c", "d"); // 34
            Assert.AreEqual(68, table.CurrentSize);

            table.SetMaxSize(0);
            Assert.AreEqual(0, table.CurrentSize);
        }
    }
}
