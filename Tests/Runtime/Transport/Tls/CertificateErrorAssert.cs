using System;
using System.Security.Authentication;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #451: the assertion of every TLS negative row. <c>RawSocketTransport</c> maps every
    /// <see cref="AuthenticationException"/> to <see cref="UHttpErrorType.CertificateError"/>, so a TLS
    /// stack that cannot handshake at all (a type initializer that fails on a stripped player, a missing
    /// native entry point or library) also surfaces as <c>CertificateError</c>. A row asserting only the
    /// error type then passes on a player where no certificate was ever validated. This helper
    /// additionally requires the <see cref="AuthenticationException"/> cause and rejects any platform
    /// failure (<see cref="TlsProviderSelector.IsPlatformTlsException"/>) anywhere in its cause chain.
    /// </summary>
    internal static class CertificateErrorAssert
    {
        // Cause chains are a handful of levels deep; the bound only stops a pathological cycle.
        private const int MaxCauseDepth = 32;

        /// <summary>
        /// Fails unless <paramref name="failure"/> is a <see cref="UHttpException"/> of type
        /// <see cref="UHttpErrorType.CertificateError"/> caused by a certificate rejection rather than by
        /// a broken TLS stack.
        /// </summary>
        internal static UHttpException IsCertificateRejection(Exception failure, string context)
        {
            string violation = Violation(failure);
            if (violation != null)
                Assert.Fail(context + ": " + violation);

            return (UHttpException)failure;
        }

        /// <summary>
        /// Fails when the cause chain of a raw provider-level <paramref name="failure"/> (an
        /// <see cref="AuthenticationException"/> from <see cref="ITlsProvider.WrapAsync"/>) holds a
        /// platform failure.
        /// </summary>
        internal static void IsNotBrokenTlsStack(Exception failure, string context)
        {
            var broken = FindBrokenTlsStackCause(failure);
            if (broken != null)
                Assert.Fail(context + ": " + BrokenStackMessage(broken, failure));
        }

        /// <summary>
        /// Null when <paramref name="failure"/> is a certificate rejection; otherwise why it is not.
        /// Separate from the asserting methods so the rule itself is unit-tested.
        /// </summary>
        internal static string Violation(Exception failure)
        {
            if (!(failure is UHttpException ex))
                return "expected UHttpException(CertificateError), got " + Describe(failure);

            if (ex.HttpError == null || ex.HttpError.Type != UHttpErrorType.CertificateError)
                return "expected CertificateError, got " + (ex.HttpError?.Type.ToString() ?? "no error") + ": " + ex;

            if (!(ex.HttpError.InnerException is AuthenticationException))
            {
                return "expected the CertificateError to carry the AuthenticationException of the handshake, got " +
                       Describe(ex.HttpError.InnerException) + ": " + ex;
            }

            var broken = FindBrokenTlsStackCause(ex.HttpError.InnerException);
            return broken != null ? BrokenStackMessage(broken, ex) : null;
        }

        /// <summary>
        /// The first exception in the cause chain of <paramref name="failure"/> (itself included) that
        /// reports a platform-level TLS failure, or null.
        /// </summary>
        internal static Exception FindBrokenTlsStackCause(Exception failure)
        {
            var current = failure;
            for (int depth = 0; current != null && depth < MaxCauseDepth; depth++)
            {
                if (TlsProviderSelector.IsPlatformTlsException(current))
                    return current;

                if (current is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        var found = FindBrokenTlsStackCause(inner);
                        if (found != null)
                            return found;
                    }

                    return null;
                }

                current = current.InnerException;
            }

            return null;
        }

        private static string BrokenStackMessage(Exception broken, Exception failure)
        {
            return "the handshake failed with " + broken.GetType().FullName + " (" + broken.Message +
                   "): the TLS stack itself is broken, so this row proves nothing about certificate validation. " +
                   failure;
        }

        private static string Describe(Exception ex) =>
            ex == null ? "no exception" : ex.GetType().FullName + ": " + ex.Message;
    }
}
