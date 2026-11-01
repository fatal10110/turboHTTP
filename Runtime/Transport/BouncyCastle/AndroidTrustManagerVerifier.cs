#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading;
using TurboHTTP.Transport.Tls;
using UnityEngine;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// Server certificate path validation through the Android framework (#239):
    /// <c>android.net.http.X509TrustManagerExtensions.checkServerTrusted(chain, authType, host)</c>
    /// on the default <c>X509TrustManager</c>, which applies the app's network security
    /// configuration (trust anchors, pins, Certificate Transparency) and returns the built path.
    /// A <see cref="PlatformCertificateVerifier"/>, registered into the engine-free Transport seam
    /// because JNI needs UnityEngine.
    /// </summary>
    /// <remarks>
    /// <para>Framework classes only, through raw <see cref="AndroidJNI"/> (system class loader:
    /// safe from a non-main thread). Runs on the dedicated worker thread of
    /// <see cref="PlatformVerifierRunner"/>, which bounds it with a timeout; this class attaches
    /// that thread to the JVM and detaches it before it exits. Local references live in one local
    /// frame per call; the trust manager is created once and kept as a global reference.</para>
    /// <para>The host is used by the platform for per-domain configuration only; it does not
    /// match the host name (Android leaves that to <c>HostnameVerifier</c>), so
    /// <see cref="TlsCertificateNameMatcher"/> stays the host check. The platform evaluates at the
    /// current time. authType is "RSA", as OkHttp's chain cleaner passes: the platform trust
    /// manager only requires it to be non-empty. Any JNI failure is a rejection. Needs device
    /// validation (#99).</para>
    /// </remarks>
    internal static class AndroidTrustManagerVerifier
    {
        private const string AuthType = "RSA";
        private const int LocalFrameCapacity = 128;
        private const int MaxCauseDepth = 8;

        private static readonly object Gate = new object();
        private static IntPtr s_extensions;  // global reference; written once under Gate
        private static int s_registered;

        /// <summary>Registers the bridge into <see cref="ServerCertificateVerifier"/> once.</summary>
        internal static void EnsureRegistered()
        {
            if (Volatile.Read(ref s_registered) != 0)
                return;
            ServerCertificateVerifier.RegisterPlatformVerifier(Verify);
            Volatile.Write(ref s_registered, 1);
        }

        private static CertificateVerificationResult Verify(
            byte[][] chainDer, string host, DateTime verificationTimeUtc)
        {
            if (AndroidJNI.AttachCurrentThread() != 0)
                return Fail(CertificateVerificationStatus.Invalid, "could not attach to the Java VM");
            try
            {
                if (AndroidJNI.PushLocalFrame(LocalFrameCapacity) != 0)
                {
                    AndroidJNI.ExceptionClear();
                    return Fail(CertificateVerificationStatus.Invalid, "Java VM out of local references");
                }

                try
                {
                    return Evaluate(chainDer, host);
                }
                finally
                {
                    AndroidJNI.ExceptionClear();
                    AndroidJNI.PopLocalFrame(IntPtr.Zero);
                }
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }
        }

        private static CertificateVerificationResult Evaluate(byte[][] chainDer, string host)
        {
            IntPtr extensions = GetExtensions();

            IntPtr factoryClass = FindClass("java/security/cert/CertificateFactory");
            IntPtr factory = AndroidJNI.CallStaticObjectMethod(factoryClass,
                StaticMethod(factoryClass, "getInstance", "(Ljava/lang/String;)Ljava/security/cert/CertificateFactory;"),
                Args(NewString("X.509")));
            Check("CertificateFactory.getInstance");
            IntPtr generate = Method(factoryClass, "generateCertificate",
                "(Ljava/io/InputStream;)Ljava/security/cert/Certificate;");

            IntPtr streamClass = FindClass("java/io/ByteArrayInputStream");
            IntPtr streamCtor = Method(streamClass, "<init>", "([B)V");
            IntPtr x509Class = FindClass("java/security/cert/X509Certificate");

            IntPtr chain = AndroidJNI.NewObjectArray(chainDer.Length, x509Class, IntPtr.Zero);
            Check("NewObjectArray");
            for (int i = 0; i < chainDer.Length; i++)
            {
                var signed = new sbyte[chainDer[i].Length];
                Buffer.BlockCopy(chainDer[i], 0, signed, 0, signed.Length);
                IntPtr bytes = AndroidJNI.ToSByteArray(signed);
                Check("ToSByteArray");
                IntPtr stream = AndroidJNI.NewObject(streamClass, streamCtor, Args(bytes));
                Check("ByteArrayInputStream");
                IntPtr cert = AndroidJNI.CallObjectMethod(factory, generate, Args(stream));
                if (TakePendingException() != IntPtr.Zero || cert == IntPtr.Zero
                    || !AndroidJNI.IsInstanceOf(cert, x509Class))
                {
                    return Fail(CertificateVerificationStatus.Invalid, "malformed certificate in chain");
                }

                AndroidJNI.SetObjectArrayElement(chain, i, cert);
                Check("SetObjectArrayElement");
                AndroidJNI.DeleteLocalRef(bytes);
                AndroidJNI.DeleteLocalRef(stream);
                AndroidJNI.DeleteLocalRef(cert);
            }

            IntPtr extensionsClass = FindClass("android/net/http/X509TrustManagerExtensions");
            IntPtr checkServerTrusted = Method(extensionsClass, "checkServerTrusted",
                "([Ljava/security/cert/X509Certificate;Ljava/lang/String;Ljava/lang/String;)Ljava/util/List;");
            IntPtr built = AndroidJNI.CallObjectMethod(extensions, checkServerTrusted,
                Args(chain, NewString(AuthType), NewString(host)));
            IntPtr rejection = TakePendingException();
            if (rejection != IntPtr.Zero)
                return MapRejection(rejection);
            if (built == IntPtr.Zero)
                return Fail(CertificateVerificationStatus.Invalid, "Android trust manager returned no path");

            return ReadPath(built);
        }

        private static CertificateVerificationResult ReadPath(IntPtr list)
        {
            IntPtr listClass = FindClass("java/util/List");
            IntPtr size = Method(listClass, "size", "()I");
            IntPtr get = Method(listClass, "get", "(I)Ljava/lang/Object;");
            IntPtr certificateClass = FindClass("java/security/cert/Certificate");
            IntPtr getEncoded = Method(certificateClass, "getEncoded", "()[B");

            int count = AndroidJNI.CallIntMethod(list, size, Array.Empty<jvalue>());
            Check("List.size");
            if (count <= 0 || count > PlatformVerifierRunner.MaxPathLength)
                return Fail(CertificateVerificationStatus.Invalid, "Android trust manager path length out of range");

            var path = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                IntPtr item = AndroidJNI.CallObjectMethod(list, get, new[] { new jvalue { i = i } });
                Check("List.get");
                if (item == IntPtr.Zero || !AndroidJNI.IsInstanceOf(item, certificateClass))
                    return Fail(CertificateVerificationStatus.Invalid, "Android trust manager path holds a non-certificate");

                IntPtr encoded = AndroidJNI.CallObjectMethod(item, getEncoded, Array.Empty<jvalue>());
                Check("Certificate.getEncoded");
                if (encoded == IntPtr.Zero)
                    return Fail(CertificateVerificationStatus.Invalid, "Android trust manager path holds an empty certificate");

                var signedDer = AndroidJNI.FromSByteArray(encoded);
                Check("FromSByteArray");
                if (signedDer == null || signedDer.Length == 0)
                    return Fail(CertificateVerificationStatus.Invalid, "Android trust manager path holds an empty certificate");
                path[i] = new byte[signedDer.Length];
                Buffer.BlockCopy(signedDer, 0, path[i], 0, signedDer.Length);
                AndroidJNI.DeleteLocalRef(encoded);
                AndroidJNI.DeleteLocalRef(item);
            }

            return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, path);
        }

        /// <summary>
        /// A <c>CertificateException</c> is a platform rejection: time when an expiry exception is
        /// in its cause chain, else an untrusted path. Anything else is an invalid result. The
        /// detail carries no exception message (it can name certificates).
        /// </summary>
        private static CertificateVerificationResult MapRejection(IntPtr exception)
        {
            if (!AndroidJNI.IsInstanceOf(exception, FindClass("java/security/cert/CertificateException")))
                return Fail(CertificateVerificationStatus.Invalid, "Android trust manager failed");

            IntPtr expired = FindClass("java/security/cert/CertificateExpiredException");
            IntPtr notYetValid = FindClass("java/security/cert/CertificateNotYetValidException");
            IntPtr throwableClass = FindClass("java/lang/Throwable");
            IntPtr getCause = Method(throwableClass, "getCause", "()Ljava/lang/Throwable;");

            IntPtr current = exception;
            for (int depth = 0; depth < MaxCauseDepth && current != IntPtr.Zero; depth++)
            {
                if (AndroidJNI.IsInstanceOf(current, expired) || AndroidJNI.IsInstanceOf(current, notYetValid))
                {
                    return Fail(CertificateVerificationStatus.NotTimeValid,
                        "Android trust manager: certificate outside its validity period");
                }

                current = AndroidJNI.CallObjectMethod(current, getCause, Array.Empty<jvalue>());
                if (TakePendingException() != IntPtr.Zero)
                    break;
            }

            return Fail(CertificateVerificationStatus.UntrustedRoot, "Android trust manager rejected the chain");
        }

        /// <summary>
        /// The default trust manager wrapped in <c>X509TrustManagerExtensions</c>, created once:
        /// <c>TrustManagerFactory.getInstance(getDefaultAlgorithm()).init((KeyStore) null)</c>.
        /// </summary>
        private static IntPtr GetExtensions()
        {
            lock (Gate)
            {
                if (s_extensions != IntPtr.Zero)
                    return s_extensions;

                IntPtr factoryClass = FindClass("javax/net/ssl/TrustManagerFactory");
                IntPtr algorithm = AndroidJNI.CallStaticObjectMethod(factoryClass,
                    StaticMethod(factoryClass, "getDefaultAlgorithm", "()Ljava/lang/String;"), Array.Empty<jvalue>());
                Check("TrustManagerFactory.getDefaultAlgorithm");
                IntPtr factory = AndroidJNI.CallStaticObjectMethod(factoryClass,
                    StaticMethod(factoryClass, "getInstance", "(Ljava/lang/String;)Ljavax/net/ssl/TrustManagerFactory;"),
                    Args(algorithm));
                Check("TrustManagerFactory.getInstance");
                AndroidJNI.CallVoidMethod(factory, Method(factoryClass, "init", "(Ljava/security/KeyStore;)V"),
                    Args(IntPtr.Zero));
                Check("TrustManagerFactory.init");
                IntPtr managers = AndroidJNI.CallObjectMethod(factory,
                    Method(factoryClass, "getTrustManagers", "()[Ljavax/net/ssl/TrustManager;"), Array.Empty<jvalue>());
                Check("TrustManagerFactory.getTrustManagers");
                if (managers == IntPtr.Zero)
                    throw new InvalidOperationException("no trust managers");

                IntPtr x509ManagerClass = FindClass("javax/net/ssl/X509TrustManager");
                IntPtr x509Manager = IntPtr.Zero;
                int count = AndroidJNI.GetArrayLength(managers);
                for (int i = 0; i < count && x509Manager == IntPtr.Zero; i++)
                {
                    IntPtr manager = AndroidJNI.GetObjectArrayElement(managers, i);
                    Check("GetObjectArrayElement");
                    if (manager != IntPtr.Zero && AndroidJNI.IsInstanceOf(manager, x509ManagerClass))
                        x509Manager = manager;
                }

                if (x509Manager == IntPtr.Zero)
                    throw new InvalidOperationException("no X509TrustManager");

                IntPtr extensionsClass = FindClass("android/net/http/X509TrustManagerExtensions");
                IntPtr extensions = AndroidJNI.NewObject(extensionsClass,
                    Method(extensionsClass, "<init>", "(Ljavax/net/ssl/X509TrustManager;)V"), Args(x509Manager));
                Check("X509TrustManagerExtensions");

                IntPtr global = AndroidJNI.NewGlobalRef(extensions);
                if (global == IntPtr.Zero)
                    throw new InvalidOperationException("NewGlobalRef failed");
                s_extensions = global;
                return global;
            }
        }

        private static IntPtr FindClass(string name)
        {
            IntPtr cls = AndroidJNI.FindClass(name);
            Check(name);
            if (cls == IntPtr.Zero)
                throw new InvalidOperationException("JNI class not found");
            return cls;
        }

        private static IntPtr Method(IntPtr cls, string name, string signature)
        {
            IntPtr id = AndroidJNI.GetMethodID(cls, name, signature);
            Check(name);
            if (id == IntPtr.Zero)
                throw new InvalidOperationException("JNI method not found");
            return id;
        }

        private static IntPtr StaticMethod(IntPtr cls, string name, string signature)
        {
            IntPtr id = AndroidJNI.GetStaticMethodID(cls, name, signature);
            Check(name);
            if (id == IntPtr.Zero)
                throw new InvalidOperationException("JNI method not found");
            return id;
        }

        private static IntPtr NewString(string value)
        {
            IntPtr s = AndroidJNI.NewStringUTF(value);
            Check("NewStringUTF");
            return s;
        }

        private static jvalue[] Args(params IntPtr[] references)
        {
            var args = new jvalue[references.Length];
            for (int i = 0; i < references.Length; i++)
                args[i].l = references[i];
            return args;
        }

        /// <summary>Clears and returns the pending Java exception (a local reference), if any.</summary>
        private static IntPtr TakePendingException()
        {
            IntPtr exception = AndroidJNI.ExceptionOccurred();
            if (exception != IntPtr.Zero)
                AndroidJNI.ExceptionClear();
            return exception;
        }

        private static void Check(string step)
        {
            if (TakePendingException() != IntPtr.Zero)
                throw new InvalidOperationException("JNI call failed: " + step);
        }

        private static CertificateVerificationResult Fail(CertificateVerificationStatus status, string detail)
            => new CertificateVerificationResult(status, detail, null);
    }
}
#endif
