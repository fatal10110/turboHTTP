using System;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class DetachedBufferedBodyTests
    {
        [Test]
        public void DetachOwner_OnReadonlyField_TransfersOwnerOnce()
        {
            var owner = new ProbeOwner();
            var holder = new DetachedBodyHolder(new DetachedBufferedBody(Encoding.UTF8.GetBytes("payload"), owner));

            var detachedOwner = holder.Body.DetachOwner();

            Assert.AreSame(owner, detachedOwner);
            Assert.IsNull(holder.Body.DetachOwner());

            detachedOwner.Dispose();
            Assert.AreEqual(1, owner.DisposeCalls);
        }

        [Test]
        public void DisposeOwnedResources_OnReadonlyField_DisposesOwnerOnce()
        {
            var owner = new ProbeOwner();
            var holder = new DetachedBodyHolder(new DetachedBufferedBody(Encoding.UTF8.GetBytes("payload"), owner));

            holder.Body.DisposeOwnedResources();
            holder.Body.DisposeOwnedResources();

            Assert.AreEqual(1, owner.DisposeCalls);
        }

        private readonly struct DetachedBodyHolder
        {
            public DetachedBodyHolder(DetachedBufferedBody body)
            {
                Body = body;
            }

            public DetachedBufferedBody Body { get; }
        }

        private sealed class ProbeOwner : IDisposable
        {
            public int DisposeCalls { get; private set; }

            public void Dispose()
            {
                DisposeCalls++;
            }
        }
    }
}
