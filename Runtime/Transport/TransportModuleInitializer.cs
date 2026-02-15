using System.Runtime.CompilerServices;
using TurboHTTP.Core;

namespace TurboHTTP.Transport
{
    /// <summary>
    /// Auto-registers <see cref="RawSocketTransport"/> with <see cref="HttpTransportFactory"/>
    /// when the TurboHTTP.Transport assembly is loaded.
    /// <para>
    /// Under IL2CPP, module initializer timing relative to other assemblies is
    /// implementation-defined. If <see cref="HttpTransportFactory.Default"/> throws
    /// <see cref="System.InvalidOperationException"/>, call
    /// <see cref="RawSocketTransport.EnsureRegistered"/> at startup as a fallback.
    /// </para>
    /// </summary>
    internal static class TransportModuleInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            HttpTransportFactory.Register(
                () => new RawSocketTransport(),
                tlsBackend => new RawSocketTransport(tlsBackend: tlsBackend),
                (tlsBackend, http2MaxDecodedHeaderBytes) =>
                    new RawSocketTransport(
                        pool: null,
                        tlsBackend: tlsBackend,
                        http2MaxDecodedHeaderBytes: http2MaxDecodedHeaderBytes));
        }
    }
}
