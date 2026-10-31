using System.Runtime.CompilerServices;
using TurboHTTP.Core;

namespace TurboHTTP.Transport
{
    /// <summary>
    /// Auto-registers <see cref="RawSocketTransport"/> with <see cref="HttpTransportFactory"/>
    /// when the TurboHTTP.Transport assembly is loaded, where the runtime runs module initializers.
    /// <para>
    /// Secondary path only: Unity 2021.3 does not run <c>[ModuleInitializer]</c> reliably, so the
    /// primary registration is the RuntimeInitializeOnLoadMethod bootstrap in TurboHTTP.Complete.
    /// Without either, call <see cref="RawSocketTransport.EnsureRegistered"/> at startup.
    /// </para>
    /// </summary>
    internal static class TransportModuleInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            RawSocketTransport.EnsureRegistered();
        }
    }
}
