using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Extends a client instance through capability-scoped registrations.
    /// </summary>
    public interface IHttpPlugin
    {
        /// <summary>
        /// Gets the plugin name, used in diagnostics and in <see cref="PluginException.PluginName"/>.
        /// </summary>
        string Name { get; }
        /// <summary>
        /// Gets the plugin version string, reported through <see cref="PluginDescriptor.Version"/>.
        /// </summary>
        string Version { get; }
        /// <summary>
        /// Gets the capabilities the plugin declares. <see cref="PluginContext"/> enforces them: a registration
        /// or interceptor behavior outside the declared flags fails with a <see cref="PluginException"/>.
        /// <see cref="PluginCapabilities.None"/> cannot register interceptors.
        /// </summary>
        PluginCapabilities Capabilities { get; }

        /// <summary>
        /// Called once when the plugin is registered with <see cref="UHttpClient.RegisterPluginAsync"/>. Register interceptors
        /// through <paramref name="context"/>. A failure faults the plugin and is surfaced as a <see cref="PluginException"/>.
        /// </summary>
        /// <param name="context">The capability-scoped registration surface for this plugin.</param>
        /// <param name="cancellationToken">Cancels initialization.</param>
        ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken);
        /// <summary>
        /// Called when the client shuts the plugin down (client disposal). Release anything acquired in <see cref="InitializeAsync"/>.
        /// </summary>
        /// <param name="cancellationToken">Cancels shutdown.</param>
        ValueTask ShutdownAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Capabilities a plugin declares through <see cref="IHttpPlugin.Capabilities"/>. Flags combine; the client enforces them on every
    /// interceptor the plugin registers (<see cref="PluginContext.RegisterInterceptor"/>).
    /// </summary>
    [Flags]
    public enum PluginCapabilities
    {
        /// <summary>
        /// No capability. The plugin cannot register interceptors or log diagnostics.
        /// </summary>
        None = 0,
        /// <summary>
        /// Register interceptors that only observe requests, responses and errors. They must not replace the request or response.
        /// </summary>
        ObserveRequests = 1 << 0,
        /// <summary>
        /// Observe like <see cref="ObserveRequests"/> and additionally call <see cref="PluginContext.LogDiagnostic"/>.
        /// </summary>
        ReadOnlyMonitoring = 1 << 1,
        /// <summary>
        /// Registered interceptors may change the request (headers, URI, content, metadata) before dispatch.
        /// </summary>
        MutateRequests = 1 << 2,
        /// <summary>
        /// Registered interceptors may replace or change the response handed back to the caller.
        /// </summary>
        MutateResponses = 1 << 3,
        /// <summary>
        /// Registered interceptors may translate or suppress errors raised by later stages.
        /// </summary>
        HandleErrors = 1 << 4,
        /// <summary>
        /// The plugin may call <see cref="PluginContext.LogDiagnostic"/>.
        /// </summary>
        Diagnostics = 1 << 5,
        /// <summary>
        /// Registered interceptors may dispatch the request again (retry, redirect, fallback) instead of calling the next stage once.
        /// Without it a second dispatch fails with a <see cref="PluginException"/>.
        /// </summary>
        AllowRedispatch = 1 << 6
    }

    /// <summary>
    /// Lifecycle of a registered plugin, reported through <see cref="PluginDescriptor.State"/>.
    /// </summary>
    public enum PluginLifecycleState
    {
        /// <summary>
        /// Constructed, not yet registered.
        /// </summary>
        Created,
        /// <summary>
        /// <see cref="IHttpPlugin.InitializeAsync"/> is running.
        /// </summary>
        Initializing,
        /// <summary>
        /// Initialization completed; the plugin is active.
        /// </summary>
        Initialized,
        /// <summary>
        /// Initialization or shutdown failed.
        /// </summary>
        Faulted,
        /// <summary>
        /// <see cref="IHttpPlugin.ShutdownAsync"/> is running.
        /// </summary>
        ShuttingDown,
        /// <summary>
        /// Shut down; the plugin no longer participates.
        /// </summary>
        Disposed
    }

    /// <summary>
    /// Read-only snapshot of a registered plugin.
    /// </summary>
    public sealed class PluginDescriptor
    {
        /// <summary>
        /// Gets the plugin name.
        /// </summary>
        public string Name { get; }
        /// <summary>
        /// Gets the plugin version.
        /// </summary>
        public string Version { get; }
        /// <summary>
        /// Gets the declared capabilities.
        /// </summary>
        public PluginCapabilities Capabilities { get; }
        /// <summary>
        /// Gets the lifecycle state at snapshot time.
        /// </summary>
        public PluginLifecycleState State { get; }

        /// <summary>
        /// Creates a descriptor. A null <paramref name="name"/> or <paramref name="version"/> becomes an empty string.
        /// </summary>
        public PluginDescriptor(
            string name,
            string version,
            PluginCapabilities capabilities,
            PluginLifecycleState state)
        {
            Name = name ?? string.Empty;
            Version = version ?? string.Empty;
            Capabilities = capabilities;
            State = state;
        }
    }

    /// <summary>
    /// Raised when a plugin fails or exceeds its declared <see cref="PluginCapabilities"/>.
    /// </summary>
    public sealed class PluginException : Exception
    {
        /// <summary>
        /// Gets the name of the plugin that failed.
        /// </summary>
        public string PluginName { get; }
        /// <summary>
        /// Gets the phase in which the failure occurred, for example <c>initialize</c> or <c>diagnostics</c>.
        /// </summary>
        public string Phase { get; }

        /// <summary>
        /// Creates the exception.
        /// </summary>
        public PluginException(
            string pluginName,
            string phase,
            string message,
            Exception innerException = null)
            : base(message, innerException)
        {
            PluginName = pluginName ?? string.Empty;
            Phase = phase ?? string.Empty;
        }
    }
}
