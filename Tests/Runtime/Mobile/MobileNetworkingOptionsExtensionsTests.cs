using System;
using System.Collections.Generic;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity.Mobile;

namespace TurboHTTP.Tests.Mobile
{
    public sealed class MobileNetworkingOptionsExtensionsTests
    {
        [Test]
        public void UseBoth_InsertsAdaptiveThenBackground_BeforeUserInterceptors_InEitherCallOrder()
        {
            var user = new PassThroughInterceptor();

            var backgroundFirst = new UHttpClientOptions { Interceptors = new List<IHttpInterceptor> { user } };
            var background = backgroundFirst.UseBackgroundNetworking();
            var adaptive = backgroundFirst.UseAdaptiveNetworking();
            CollectionAssert.AreEqual(new IHttpInterceptor[] { adaptive, background, user }, backgroundFirst.Interceptors);

            var adaptiveFirst = new UHttpClientOptions { Interceptors = new List<IHttpInterceptor> { user } };
            adaptive = adaptiveFirst.UseAdaptiveNetworking();
            background = adaptiveFirst.UseBackgroundNetworking();
            CollectionAssert.AreEqual(new IHttpInterceptor[] { adaptive, background, user }, adaptiveFirst.Interceptors);
        }

        [Test]
        public void UseTwice_ReturnsExistingInterceptor_WithoutInsertingAgain()
        {
            var options = new UHttpClientOptions();

            var adaptive = options.UseAdaptiveNetworking();
            var background = options.UseBackgroundNetworking();

            Assert.AreSame(adaptive, options.UseAdaptiveNetworking(new AdaptivePolicy { Enable = true }));
            Assert.AreSame(background, options.UseBackgroundNetworking(new BackgroundNetworkingPolicy { Enable = true }));
            Assert.AreEqual(2, options.Interceptors.Count);
        }

        [Test]
        public void Use_WithNullInterceptorList_CreatesIt()
        {
            var options = new UHttpClientOptions { Interceptors = null };

            var adaptive = options.UseAdaptiveNetworking();

            Assert.IsNotNull(options.Interceptors);
            Assert.AreSame(adaptive, options.Interceptors[0]);
        }

        [Test]
        public void Use_WithNullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => MobileNetworkingOptionsExtensions.UseAdaptiveNetworking(null));
            Assert.Throws<ArgumentNullException>(() => MobileNetworkingOptionsExtensions.UseBackgroundNetworking(null));
        }

        private sealed class PassThroughInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next) => next;
        }
    }
}
