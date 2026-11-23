using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Normalizes a socket error to a defined <see cref="SocketError"/> value (#1290). Some runtimes put
    /// the raw POSIX errno into <see cref="SocketException.SocketErrorCode"/> instead of the mapped value:
    /// IL2CPP on Android reports a refused connect as 111 (Linux ECONNREFUSED), IL2CPP on Darwin as 61
    /// (#1287). Every product comparison of a socket error goes through <see cref="Normalize(SocketError)"/>
    /// so a raw errno is classified like the value it stands for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A defined value (the WSA codes 10004 and up, the resolver codes 11001-11004, Success, IOPending,
    /// OperationAborted) is kept: every errno in the tables is below 200, so no defined value is ever
    /// remapped. A value in neither table passes through unchanged.
    /// </para>
    /// <para>
    /// Linux and Darwin number their errno values differently (Linux 61 is ENODATA, Darwin 61 is
    /// ECONNREFUSED), so the table is chosen by the OS the process runs on; Windows reports WSA codes and
    /// uses no table. Only the socket errors whose meaning .NET's own errno mapping (SocketErrorPal in
    /// System.Native) gives a <see cref="SocketError"/> are mapped; generic errnos such as EINVAL or
    /// EACCES are left alone.
    /// </para>
    /// <para>
    /// Resolver failures are not mapped: getaddrinfo's EAI_* codes are positive and collide with errno
    /// values on Darwin and Android bionic (EAI_AGAIN 2, EAI_NODATA 7, EAI_NONAME 8), so a raw EAI code
    /// cannot be told apart from an errno here. The resolver failure is compared as reported.
    /// </para>
    /// </remarks>
    internal static class SocketErrorNormalizer
    {
        /// <summary>The errno numbering a raw value is read in.</summary>
        internal enum ErrnoTable
        {
            /// <summary>No errno mapping (Windows, or an OS that was not recognized).</summary>
            None,

            /// <summary>Linux and Android: include/uapi/asm-generic/errno-base.h and errno.h.</summary>
            Linux,

            /// <summary>macOS and iOS: xnu bsd/sys/errno.h.</summary>
            Darwin,
        }

        /// <summary>The table of the OS this process runs on, chosen once.</summary>
        internal static readonly ErrnoTable CurrentTable = PlatformTable();

        /// <summary>
        /// The table of the build platform. Unity builds take it from the platform defines, the same
        /// split as SaeaPlatformDetection.CurrentOs, because a runtime on iOS need not describe itself
        /// as Darwin; other hosts (the .NET test runner, unknown Unity platforms) use <see cref="DetectTable"/>.
        /// </summary>
        internal static ErrnoTable PlatformTable()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return ErrnoTable.None;
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            return ErrnoTable.Darwin;
#elif UNITY_ANDROID || UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
            return ErrnoTable.Linux;
#else
            return DetectTable();
#endif
        }

        /// <summary>The normalized <see cref="SocketException.SocketErrorCode"/> of <paramref name="exception"/>.</summary>
        internal static SocketError Normalize(SocketException exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            return Normalize(exception.SocketErrorCode, CurrentTable);
        }

        /// <summary><paramref name="error"/> read in the errno table of the OS this process runs on.</summary>
        internal static SocketError Normalize(SocketError error) => Normalize(error, CurrentTable);

        /// <summary>
        /// <paramref name="error"/> read in <paramref name="table"/>: a raw errno of that table becomes the
        /// <see cref="SocketError"/> it stands for; any other value is returned unchanged.
        /// </summary>
        internal static SocketError Normalize(SocketError error, ErrnoTable table)
        {
            switch (table)
            {
                case ErrnoTable.Linux:
                    return FromLinuxErrno((int)error, error);
                case ErrnoTable.Darwin:
                    return FromDarwinErrno((int)error, error);
                default:
                    return error;
            }
        }

        // Linux (and Android bionic, which uses the kernel numbers): include/uapi/asm-generic/errno-base.h
        // (1-34) and include/uapi/asm-generic/errno.h (35 and up), the numbering of x86, x86_64, ARM and ARM64.
        private static SocketError FromLinuxErrno(int errno, SocketError unchanged)
        {
            switch (errno)
            {
                case 4: return SocketError.Interrupted;                 // EINTR
                case 11: return SocketError.WouldBlock;                 // EAGAIN, EWOULDBLOCK
                case 90: return SocketError.MessageSize;                // EMSGSIZE
                case 97: return SocketError.AddressFamilyNotSupported;  // EAFNOSUPPORT
                case 98: return SocketError.AddressAlreadyInUse;        // EADDRINUSE
                case 99: return SocketError.AddressNotAvailable;        // EADDRNOTAVAIL
                case 100: return SocketError.NetworkDown;               // ENETDOWN
                case 101: return SocketError.NetworkUnreachable;        // ENETUNREACH
                case 102: return SocketError.NetworkReset;              // ENETRESET
                case 103: return SocketError.ConnectionAborted;         // ECONNABORTED
                case 104: return SocketError.ConnectionReset;           // ECONNRESET
                case 105: return SocketError.NoBufferSpaceAvailable;    // ENOBUFS
                case 106: return SocketError.IsConnected;               // EISCONN
                case 107: return SocketError.NotConnected;              // ENOTCONN
                case 108: return SocketError.Shutdown;                  // ESHUTDOWN
                case 110: return SocketError.TimedOut;                  // ETIMEDOUT
                case 111: return SocketError.ConnectionRefused;         // ECONNREFUSED
                case 112: return SocketError.HostDown;                  // EHOSTDOWN
                case 113: return SocketError.HostUnreachable;           // EHOSTUNREACH
                case 114: return SocketError.AlreadyInProgress;         // EALREADY
                case 115: return SocketError.InProgress;                // EINPROGRESS
                case 125: return SocketError.OperationAborted;          // ECANCELED
                default: return unchanged;
            }
        }

        // Darwin (macOS, iOS): xnu bsd/sys/errno.h.
        private static SocketError FromDarwinErrno(int errno, SocketError unchanged)
        {
            switch (errno)
            {
                case 4: return SocketError.Interrupted;                 // EINTR
                case 35: return SocketError.WouldBlock;                 // EAGAIN, EWOULDBLOCK
                case 36: return SocketError.InProgress;                 // EINPROGRESS
                case 37: return SocketError.AlreadyInProgress;          // EALREADY
                case 40: return SocketError.MessageSize;                // EMSGSIZE
                case 47: return SocketError.AddressFamilyNotSupported;  // EAFNOSUPPORT
                case 48: return SocketError.AddressAlreadyInUse;        // EADDRINUSE
                case 49: return SocketError.AddressNotAvailable;        // EADDRNOTAVAIL
                case 50: return SocketError.NetworkDown;                // ENETDOWN
                case 51: return SocketError.NetworkUnreachable;         // ENETUNREACH
                case 52: return SocketError.NetworkReset;               // ENETRESET
                case 53: return SocketError.ConnectionAborted;          // ECONNABORTED
                case 54: return SocketError.ConnectionReset;            // ECONNRESET
                case 55: return SocketError.NoBufferSpaceAvailable;     // ENOBUFS
                case 56: return SocketError.IsConnected;                // EISCONN
                case 57: return SocketError.NotConnected;               // ENOTCONN
                case 58: return SocketError.Shutdown;                   // ESHUTDOWN
                case 60: return SocketError.TimedOut;                   // ETIMEDOUT
                case 61: return SocketError.ConnectionRefused;          // ECONNREFUSED
                case 64: return SocketError.HostDown;                   // EHOSTDOWN
                case 65: return SocketError.HostUnreachable;            // EHOSTUNREACH
                case 89: return SocketError.OperationAborted;           // ECANCELED
                default: return unchanged;
            }
        }

        /// <summary>
        /// The errno table of the running OS. Darwin is also recognized by its kernel name in
        /// <see cref="RuntimeInformation.OSDescription"/>, because a runtime on iOS need not report
        /// <see cref="OSPlatform.OSX"/>; Android reports Linux.
        /// </summary>
        internal static ErrnoTable DetectTable()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return ErrnoTable.None;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return ErrnoTable.Darwin;

                var description = RuntimeInformation.OSDescription;
                if (description != null && description.IndexOf("Darwin", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ErrnoTable.Darwin;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return ErrnoTable.Linux;
            }
            catch (Exception)
            {
                // An OS the runtime cannot describe: compare errors as reported.
            }

            return ErrnoTable.None;
        }
    }
}
