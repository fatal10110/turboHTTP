using NUnit.Framework;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// The SAEA close order per operating system and scripting runtime (#843): decided from what
    /// was measured on each, and the Windows diagnostic label no longer claims I/O completion ports.
    /// </summary>
    /// <remarks>The enums are internal, so the cases pass them as ints.</remarks>
    [TestFixture]
    public class SaeaPlatformDetectionTests
    {
        // Selector platforms: shutdown ends a pending receive with the descriptor open (#384, #410).
        [TestCase((int)SocketOsFamily.Apple, (int)SocketRuntimeKind.Mono, false)]
        [TestCase((int)SocketOsFamily.Apple, (int)SocketRuntimeKind.Il2Cpp, false)]
        [TestCase((int)SocketOsFamily.Linux, (int)SocketRuntimeKind.Mono, false)]
        [TestCase((int)SocketOsFamily.Linux, (int)SocketRuntimeKind.Il2Cpp, false)]
        [TestCase((int)SocketOsFamily.Unknown, (int)SocketRuntimeKind.Il2Cpp, false)]
        // Windows Editor (Mono), measured in #405: shutdown left the receive pending.
        [TestCase((int)SocketOsFamily.Windows, (int)SocketRuntimeKind.Mono, true)]
        // Windows IL2CPP: unmeasured (#843), keeps its previous close-first order.
        [TestCase((int)SocketOsFamily.Windows, (int)SocketRuntimeKind.Il2Cpp, true)]
        [TestCase((int)SocketOsFamily.Windows, (int)SocketRuntimeKind.DotNet, true)]
        public void CloseAbortsPendingIoFor_FollowsTheMeasuredOrder(int os, int runtime, bool closeFirst)
        {
            Assert.AreEqual(closeFirst, SaeaPlatformDetection.CloseAbortsPendingIoFor((SocketOsFamily)os, (SocketRuntimeKind)runtime));
        }

        [TestCase((int)SocketOsFamily.Windows, (int)SocketRuntimeKind.Il2Cpp, false)]
        [TestCase((int)SocketOsFamily.Unknown, (int)SocketRuntimeKind.Mono, false)]
        [TestCase((int)SocketOsFamily.Windows, (int)SocketRuntimeKind.Mono, true)]
        [TestCase((int)SocketOsFamily.Apple, (int)SocketRuntimeKind.Il2Cpp, true)]
        [TestCase((int)SocketOsFamily.Linux, (int)SocketRuntimeKind.Il2Cpp, true)]
        public void IsCloseOrderMeasured_FlagsTheUnmeasuredRuntimes(int os, int runtime, bool measured)
        {
            Assert.AreEqual(measured, SaeaPlatformDetection.IsCloseOrderMeasured((SocketOsFamily)os, (SocketRuntimeKind)runtime));
        }

        [TestCase((int)SocketOsFamily.Windows, "Winsock")]
        [TestCase((int)SocketOsFamily.Apple, "kqueue")]
        [TestCase((int)SocketOsFamily.Linux, "epoll")]
        [TestCase((int)SocketOsFamily.Unknown, "unknown")]
        public void CompletionModelNameFor_DoesNotLabelWindowsAsIocp(int os, string expected)
        {
            Assert.AreEqual(expected, SaeaPlatformDetection.CompletionModelNameFor((SocketOsFamily)os));
        }

        [Test]
        public void CurrentPlatform_UsesThePerRuntimeDecision()
        {
            Assert.AreEqual(
                SaeaPlatformDetection.CloseAbortsPendingIoFor(SaeaPlatformDetection.CurrentOs, SaeaPlatformDetection.CurrentRuntime),
                SaeaPlatformDetection.CloseAbortsPendingIo);
        }
    }
}
