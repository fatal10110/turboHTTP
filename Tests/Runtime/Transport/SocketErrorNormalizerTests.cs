using System.Net.Sockets;
using System.Runtime.InteropServices;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Connection;
using TurboHTTP.Transport.Tcp;
using Table = TurboHTTP.Core.Internal.SocketErrorNormalizer.ErrnoTable;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #1290: IL2CPP players report some socket errors as the raw errno (Android 111, Darwin 61 for a
    /// refused connect). The mapping tables are checked with an explicit table, so the result does not
    /// depend on the OS that runs the test.
    /// </summary>
    [TestFixture]
    public class SocketErrorNormalizerTests
    {
        // ErrnoTable is internal and NUnit test methods are public, so cases name the table by number.
        private const int None = (int)Table.None;
        private const int Linux = (int)Table.Linux;
        private const int Darwin = (int)Table.Darwin;

        // Linux and Android: include/uapi/asm-generic/errno-base.h and errno.h.
        [TestCase(4, SocketError.Interrupted)]
        [TestCase(11, SocketError.WouldBlock)]
        [TestCase(90, SocketError.MessageSize)]
        [TestCase(97, SocketError.AddressFamilyNotSupported)]
        [TestCase(98, SocketError.AddressAlreadyInUse)]
        [TestCase(99, SocketError.AddressNotAvailable)]
        [TestCase(100, SocketError.NetworkDown)]
        [TestCase(101, SocketError.NetworkUnreachable)]
        [TestCase(102, SocketError.NetworkReset)]
        [TestCase(103, SocketError.ConnectionAborted)]
        [TestCase(104, SocketError.ConnectionReset)]
        [TestCase(105, SocketError.NoBufferSpaceAvailable)]
        [TestCase(106, SocketError.IsConnected)]
        [TestCase(107, SocketError.NotConnected)]
        [TestCase(108, SocketError.Shutdown)]
        [TestCase(110, SocketError.TimedOut)]
        [TestCase(111, SocketError.ConnectionRefused)]
        [TestCase(112, SocketError.HostDown)]
        [TestCase(113, SocketError.HostUnreachable)]
        [TestCase(114, SocketError.AlreadyInProgress)]
        [TestCase(115, SocketError.InProgress)]
        [TestCase(125, SocketError.OperationAborted)]
        public void Linux_RawErrno_IsMapped(int errno, SocketError expected)
        {
            Assert.AreEqual(expected, SocketErrorNormalizer.Normalize((SocketError)errno, Table.Linux));
        }

        // Darwin (macOS, iOS): xnu bsd/sys/errno.h.
        [TestCase(4, SocketError.Interrupted)]
        [TestCase(35, SocketError.WouldBlock)]
        [TestCase(36, SocketError.InProgress)]
        [TestCase(37, SocketError.AlreadyInProgress)]
        [TestCase(40, SocketError.MessageSize)]
        [TestCase(47, SocketError.AddressFamilyNotSupported)]
        [TestCase(48, SocketError.AddressAlreadyInUse)]
        [TestCase(49, SocketError.AddressNotAvailable)]
        [TestCase(50, SocketError.NetworkDown)]
        [TestCase(51, SocketError.NetworkUnreachable)]
        [TestCase(52, SocketError.NetworkReset)]
        [TestCase(53, SocketError.ConnectionAborted)]
        [TestCase(54, SocketError.ConnectionReset)]
        [TestCase(55, SocketError.NoBufferSpaceAvailable)]
        [TestCase(56, SocketError.IsConnected)]
        [TestCase(57, SocketError.NotConnected)]
        [TestCase(58, SocketError.Shutdown)]
        [TestCase(60, SocketError.TimedOut)]
        [TestCase(61, SocketError.ConnectionRefused)]
        [TestCase(64, SocketError.HostDown)]
        [TestCase(65, SocketError.HostUnreachable)]
        [TestCase(89, SocketError.OperationAborted)]
        public void Darwin_RawErrno_IsMapped(int errno, SocketError expected)
        {
            Assert.AreEqual(expected, SocketErrorNormalizer.Normalize((SocketError)errno, Table.Darwin));
        }

        // The numberings collide: Linux 60/61 are ENOSTR/ENODATA, Darwin 101/111 are not socket errors.
        [TestCase(61, Linux)]
        [TestCase(60, Linux)]
        [TestCase(35, Linux)]
        [TestCase(111, Darwin)]
        [TestCase(101, Darwin)]
        [TestCase(11, Darwin)]
        public void OtherTablesErrno_IsUnchanged(int value, int table)
        {
            Assert.AreEqual((SocketError)value, SocketErrorNormalizer.Normalize((SocketError)value, (Table)table));
        }

        [Test]
        public void DefinedValues_AreKept_InEveryTable(
            [Values(SocketError.Success, SocketError.SocketError, SocketError.IOPending, SocketError.OperationAborted,
                SocketError.Interrupted, SocketError.WouldBlock, SocketError.ConnectionRefused,
                SocketError.ConnectionReset, SocketError.TimedOut, SocketError.NetworkUnreachable,
                SocketError.HostUnreachable, SocketError.HostNotFound, SocketError.TryAgain, SocketError.NoRecovery,
                SocketError.NoData)] SocketError defined,
            [Values(None, Linux, Darwin)] int table)
        {
            Assert.AreEqual(defined, SocketErrorNormalizer.Normalize(defined, (Table)table));
        }

        [TestCase(111)]
        [TestCase(61)]
        [TestCase(4)]
        public void NoTable_RawValue_IsUnchanged(int value)
        {
            Assert.AreEqual((SocketError)value, SocketErrorNormalizer.Normalize((SocketError)value, Table.None));
        }

        [TestCase(1, Linux)]       // EPERM: not a socket error
        [TestCase(22, Linux)]      // EINVAL: generic, left alone
        [TestCase(13, Darwin)]     // EACCES: generic, left alone
        [TestCase(2, Darwin)]      // ENOENT, also EAI_AGAIN: resolver codes are not mapped
        [TestCase(8, Darwin)]      // ENOEXEC, also EAI_NONAME
        [TestCase(12345, Linux)]
        [TestCase(-2, Linux)]      // glibc EAI_NONAME
        public void UnknownValues_PassThrough(int value, int table)
        {
            Assert.AreEqual((SocketError)value, SocketErrorNormalizer.Normalize((SocketError)value, (Table)table));
        }

        [Test]
        public void Normalize_Exception_UsesItsSocketErrorCode()
        {
            var refused = new SocketException((int)SocketError.ConnectionRefused);

            Assert.AreEqual(SocketError.ConnectionRefused, SocketErrorNormalizer.Normalize(refused));
        }

        [Test]
        public void CurrentTable_FollowsBuildPlatform()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var expected = Table.None;
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            var expected = Table.Darwin;
#elif UNITY_ANDROID || UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
            var expected = Table.Linux;
#else
            // The .NET harness: this assembly has no platform define, but the shadow-built Core it loads
            // is the editor variant (UNITY_EDITOR_OSX), so only stability is checked here.
            var expected = SocketErrorNormalizer.PlatformTable();
#endif
            Assert.AreEqual(expected, SocketErrorNormalizer.CurrentTable);
            Assert.AreEqual(expected, SocketErrorNormalizer.PlatformTable());
        }

#if !UNITY_5_3_OR_NEWER
        // The runtime fallback, on the .NET host that runs the harness (Linux CI, Windows or macOS dev boxes).
        [Test]
        public void DetectTable_MatchesHostOs()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.AreEqual(Table.None, SocketErrorNormalizer.DetectTable());
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Assert.AreEqual(Table.Darwin, SocketErrorNormalizer.DetectTable());
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Assert.AreEqual(Table.Linux, SocketErrorNormalizer.DetectTable());
            else
                TestRequirements.IgnoreEnvironment("host OS has no errno table");
        }
#endif

        // SAEA completions (SocketIoMode.Saea): a raw EINTR or ECANCELED must cancel like the mapped value.
        [TestCase(4, Linux)]       // EINTR
        [TestCase(125, Linux)]     // ECANCELED
        [TestCase(4, Darwin)]      // EINTR
        [TestCase(89, Darwin)]     // ECANCELED
        [TestCase((int)SocketError.OperationAborted, None)]
        [TestCase((int)SocketError.Interrupted, Linux)]
        public void SaeaCompletion_RawCancelErrno_Cancels(int value, int table)
        {
            var task = Resolve((SocketError)value, (Table)table);

            Assert.IsTrue(task.IsCanceled, "status " + task.Status);
        }

        [TestCase(111, Linux, SocketError.ConnectionRefused)]
        [TestCase(54, Darwin, SocketError.ConnectionReset)]
        [TestCase((int)SocketError.ConnectionReset, None, SocketError.ConnectionReset)]
        public void SaeaCompletion_RawFailureErrno_FaultsWithNormalizedError(int value, int table, SocketError expected)
        {
            var task = Resolve((SocketError)value, (Table)table);

            Assert.IsTrue(task.IsFaulted, "status " + task.Status);
            var socketException = task.Exception.InnerException as SocketException;
            Assert.IsNotNull(socketException, "inner " + task.Exception.InnerException);
            Assert.AreEqual(expected, socketException.SocketErrorCode);
        }

        [Test]
        public void SaeaCompletion_Success_ReturnsBytesTransferred()
        {
            var operation = ResolveValueTask(SocketError.Success, Table.Linux, bytesTransferred: 42);

            Assert.IsTrue(operation.IsCompletedSuccessfully);
            Assert.AreEqual(42, operation.Result); // already completed: reads the result, never blocks
        }

        private static System.Threading.Tasks.Task<int> Resolve(SocketError error, Table table)
            => ResolveValueTask(error, table, 0).AsTask();

        // The source has already ended when this returns, so its status is read without waiting.
        private static System.Threading.Tasks.ValueTask<int> ResolveValueTask(SocketError error, Table table, int bytesTransferred)
        {
            var source = new SaeaCompletionSource();
            source.Reset();
            SaeaSocketChannel.ResolveFromSaea(error, bytesTransferred, source, table);
            return source.AsValueTask();
        }

        // NAT64 fallback (#167): an IPv6-only network must be recognized from the raw errno too, or the
        // IPv4-literal fallback never runs on an IL2CPP player.
        [TestCase(101, Linux)]     // ENETUNREACH
        [TestCase(113, Linux)]     // EHOSTUNREACH
        [TestCase(97, Linux)]      // EAFNOSUPPORT
        [TestCase(99, Linux)]      // EADDRNOTAVAIL
        [TestCase(51, Darwin)]     // ENETUNREACH
        [TestCase(65, Darwin)]     // EHOSTUNREACH
        [TestCase(47, Darwin)]     // EAFNOSUPPORT
        [TestCase(49, Darwin)]     // EADDRNOTAVAIL
        [TestCase((int)SocketError.NetworkUnreachable, None)]
        [TestCase((int)SocketError.HostUnreachable, Linux)]
        public void Nat64_NoIpv4RouteError_RecognizesRawErrno(int value, int table)
        {
            Assert.IsTrue(Nat64AddressSynthesizer.IsNoIpv4RouteError((SocketError)value, (Table)table));
        }

        [TestCase(111, Linux)]     // ECONNREFUSED: the peer answered
        [TestCase(110, Linux)]     // ETIMEDOUT
        [TestCase(61, Darwin)]
        [TestCase(101, Darwin)]    // Linux ENETUNREACH means nothing on Darwin
        [TestCase((int)SocketError.ConnectionRefused, None)]
        public void Nat64_RefusedOrTimedOut_IsNotNoIpv4Route(int value, int table)
        {
            Assert.IsFalse(Nat64AddressSynthesizer.IsNoIpv4RouteError((SocketError)value, (Table)table));
        }
    }
}
