using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Factory for creating HTTP transport instances.
    /// Allows dependency injection and testing.
    /// </summary>
    public static class HttpTransportFactory
    {
        private static volatile IHttpTransport _defaultTransport;

        /// <summary>
        /// Get or set the default transport.
        /// Must be set before first use (Phase 3 provides RawSocketTransport).
        /// </summary>
        public static IHttpTransport Default
        {
            get
            {
                if (_defaultTransport == null)
                {
                    throw new InvalidOperationException(
                        "No default transport configured. " +
                        "Set HttpTransportFactory.Default to a transport instance, " +
                        "or ensure TurboHTTP.Transport is included in your project.");
                }
                return _defaultTransport;
            }
            set => _defaultTransport = value;
        }

        /// <summary>
        /// Create a new transport instance using the registered factory.
        /// </summary>
        public static IHttpTransport Create()
        {
            // Returns the default transport instance.
            // Phase 3 will register the concrete RawSocketTransport.
            return Default;
        }

        /// <summary>
        /// Reset the factory to its initial state.
        /// Primarily used for testing.
        /// </summary>
        public static void Reset()
        {
            _defaultTransport = null;
        }
    }
}
