using TurboHTTP.Transport.Tls;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// Registers the BouncyCastle TLS provider with <see cref="TlsProviderSelector"/>, so
    /// <see cref="TlsBackend.BouncyCastle"/> resolves without a by-name type lookup that managed code
    /// stripping can break.
    /// </summary>
    /// <remarks>
    /// This assembly is opt-in: it compiles only when the project defines
    /// <c>TURBOHTTP_BOUNCYCASTLE</c> (Player Settings &gt; Scripting Define Symbols). It registers
    /// itself at startup, so no startup code is needed normally. Registration never changes
    /// <see cref="TlsBackend.Auto"/>, which always uses the platform TLS stack.
    /// </remarks>
    public static class BouncyCastleModuleRegistration
    {
        /// <summary>
        /// Registers the BouncyCastle provider with <see cref="TlsProviderSelector"/>. Idempotent.
        /// Runs automatically at <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/> (and
        /// after each Editor domain reload), before any transport reads its capabilities.
        /// </summary>
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        public static void EnsureRegistered()
        {
            TlsProviderSelector.RegisterBouncyCastleProvider(BouncyCastleTlsProvider.Instance);
        }
    }
}
