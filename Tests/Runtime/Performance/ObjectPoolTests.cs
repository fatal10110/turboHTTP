using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Performance;

namespace TurboHTTP.Tests.Performance
{
    public class ObjectPoolTests
    {
        [Test]
        public void Rent_CreatesViaFactory_WhenPoolEmpty()
        {
            int created = 0;
            var pool = new ObjectPool<object>(() => { created++; return new object(); }, capacity: 4);

            var item = pool.Rent();

            Assert.IsNotNull(item);
            Assert.AreEqual(1, created);
        }

        [Test]
        public void Return_And_Rent_ReusesItem()
        {
            var pool = new ObjectPool<object>(() => new object(), capacity: 4);

            var item1 = pool.Rent();
            pool.Return(item1);
            var item2 = pool.Rent();

            Assert.AreSame(item1, item2);
        }

        [Test]
        public void Return_InvokesResetCallback()
        {
            bool resetCalled = false;
            var pool = new ObjectPool<List<int>>(
                () => new List<int>(),
                capacity: 4,
                reset: list => { list.Clear(); resetCalled = true; });

            var item = pool.Rent();
            item.Add(42);
            pool.Return(item);

            Assert.IsTrue(resetCalled);

            var reused = pool.Rent();
            Assert.AreEqual(0, reused.Count);
        }

        [Test]
        public void Return_DiscardsItem_WhenPoolFull()
        {
            var pool = new ObjectPool<object>(() => new object(), capacity: 2);

            var item1 = new object();
            var item2 = new object();
            var item3 = new object();

            pool.Return(item1);
            pool.Return(item2);
            pool.Return(item3); // Should be discarded

            Assert.AreEqual(2, pool.Count);
        }

        [Test]
        public void Return_NullItem_IsSilentlyIgnored()
        {
            var pool = new ObjectPool<object>(() => new object(), capacity: 4);

            Assert.DoesNotThrow(() => pool.Return(null));
            Assert.AreEqual(0, pool.Count);
        }

        [Test]
        public void Constructor_ThrowsOnNullFactory()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ObjectPool<object>(null, capacity: 4));
        }

        [Test]
        public void Constructor_ThrowsOnZeroCapacity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ObjectPool<object>(() => new object(), capacity: 0));
        }

        [Test]
        public void Pool_IsThreadSafe_UnderContention()
        {
            Task.Run(async () =>
            {
                var pool = new ObjectPool<object>(() => new object(), capacity: 16);
                var tasks = new List<Task>();

                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        for (int j = 0; j < 100; j++)
                        {
                            var item = pool.Rent();
                            Thread.SpinWait(10);
                            pool.Return(item);
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                Assert.LessOrEqual(pool.Count, 16, "Pool should not exceed capacity");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pool_EnforcesCapacity_UnderHighContention()
        {
            Task.Run(async () =>
            {
                const int capacity = 8;
                var pool = new ObjectPool<object>(() => new object(), capacity: capacity);
                var barrier = new ManualResetEventSlim(false);
                var tasks = new List<Task>();

                // Many threads simultaneously returning items
                for (int i = 0; i < 50; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        var item = new object();
                        barrier.Wait();
                        pool.Return(item);
                    }));
                }

                barrier.Set();
                await Task.WhenAll(tasks);

                Assert.LessOrEqual(pool.Count, capacity,
                    "Pool count must never exceed capacity");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pool_DoesNotStrandItemsBeyondCount_UnderContention()
        {
            Task.Run(async () =>
            {
                var pool = new ObjectPool<object>(() => new object(), capacity: 32);
                var tasks = new List<Task>();

                for (int i = 0; i < 32; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        for (int j = 0; j < 5000; j++)
                        {
                            var item = pool.Rent();
                            Thread.SpinWait(20);
                            pool.Return(item);
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                var countField = typeof(ObjectPool<object>).GetField("_count",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var itemsField = typeof(ObjectPool<object>).GetField("_items",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.IsNotNull(countField);
                Assert.IsNotNull(itemsField);

                var count = (int)countField.GetValue(pool);
                var items = (object[])itemsField.GetValue(pool);

                for (int i = count; i < items.Length; i++)
                {
                    Assert.IsNull(items[i],
                        $"Found stranded pooled item in slot {i} beyond count {count}.");
                }
            }).GetAwaiter().GetResult();
        }
    }
}
