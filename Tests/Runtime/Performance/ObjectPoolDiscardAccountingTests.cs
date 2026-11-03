using System;
using System.Collections.Generic;
using NUnit.Framework;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// The leak oracle of the soak harness (#114): <see cref="ObjectPoolDiagnostics.ActiveCount"/> counts
    /// items rented and not handed back, so a return that the full pool drops is not an active item.
    /// </summary>
    public class ObjectPoolDiscardAccountingTests
    {
        [Test]
        public void ReturnAll_MoreThanCapacityOutstanding_ActiveCountBackToZero()
        {
            var pool = new ObjectPool<object>(() => new object(), capacity: 2);
            var rented = new List<object>();
            for (int i = 0; i < 5; i++)
                rented.Add(pool.Rent());

            Assert.AreEqual(5, pool.GetDiagnostics().ActiveCount);

            foreach (var item in rented)
                pool.Return(item);

            var diagnostics = pool.GetDiagnostics();
            Assert.AreEqual(5, diagnostics.RentCount);
            Assert.AreEqual(2, diagnostics.ReturnCount, "two items fit the pool");
            Assert.AreEqual(3, diagnostics.DiscardCount, "three returns found the pool full");
            Assert.AreEqual(0, diagnostics.ActiveCount, "every rented item came back");
            Assert.AreEqual(2, pool.Count);
        }

        [Test]
        public void Return_FullPool_RunsResetAndCountsDiscard()
        {
            int resets = 0;
            var pool = new ObjectPool<object>(() => new object(), capacity: 1, reset: _ => resets++);
            var first = pool.Rent();
            var second = pool.Rent();

            pool.Return(first);
            pool.Return(second);

            Assert.AreEqual(2, resets, "a discarded item is reset too, so it keeps no request state");
            Assert.AreEqual(1, pool.GetDiagnostics().DiscardCount);
            Assert.AreEqual(0, pool.GetDiagnostics().ActiveCount);
        }

        [Test]
        public void Return_ResetThrows_ItemStaysActive()
        {
            var pool = new ObjectPool<object>(() => new object(), capacity: 4, reset: _ => throw new InvalidOperationException("reset"));
            var item = pool.Rent();

            Assert.Throws<InvalidOperationException>(() => pool.Return(item));

            var diagnostics = pool.GetDiagnostics();
            Assert.AreEqual(0, diagnostics.ReturnCount);
            Assert.AreEqual(0, diagnostics.DiscardCount);
            Assert.AreEqual(1, diagnostics.ActiveCount, "an item whose reset failed was never handed back");
        }

        [Test]
        public void ResetDiagnostics_ClearsDiscardCount()
        {
            var pool = new ObjectPool<object>(() => new object(), capacity: 1);
            pool.Return(new object());
            pool.Return(new object());
            Assert.AreEqual(1, pool.GetDiagnostics().DiscardCount);

            pool.ResetDiagnostics();

            Assert.AreEqual(0, pool.GetDiagnostics().DiscardCount);
        }
    }
}
