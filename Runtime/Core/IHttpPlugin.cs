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
        /// <summary>Plugin name, used in diagnostics and in <see cref="PluginException.PluginName"/>.</summary>
        string Name { get; }
        /// <summary>Plugin version string, for diagnostics.</summary>
        string Version { get; }
        /// <summary>The capabilities the plugin requests; <see cref="PluginContext"/> enforces them when the plugin registers interceptors or logs.</summary>
        PluginCapabilities Capabilities { get; }

        /// <summary>Called once when the client is created, before the first request. Register interceptors through <paramref name="context"/>; a failure faults the plugin.</summary>
        ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken);
        /// <summary>Called once when the client is disposed. Release what <see cref="InitializeAsync"/> acquired.</summary>
        ValueTask ShutdownAsync(CancellationToken cancellationToken);
    }

    /// <summary>Capabilities a plugin may request. Interceptors a plugin registers are restricted to the capabilities it holds.</summary>
    [Flags]
    public enum PluginCapabilities
    {
        /// <summary>No capabilities.</summary>
        None = 0,
        /// <summary>May register an interceptor that observes requests.</summary>
        ObserveRequests = 1 << 0,
        /// <summary>May observe requests and responses and write diagnostics, without changing them.</summary>
        ReadOnlyMonitoring = 1 << 1,
        /// <summary>May change requests before they are sent.</summary>
        MutateRequests = 1 << 2,
        /// <summary>May change responses before they reach the caller.</summary>
        MutateResponses = 1 << 3,
        /// <summary>May turn a failure into a response or a different failure.</summary>
        HandleErrors = 1 << 4,
        /// <summary>May write to the client diagnostics log (<see cref="PluginContext.LogDiagnostic"/>).</summary>
        Diagnostics = 1 << 5,
        /// <summary>May send a request again through the rest of the pipeline.</summary>
        AllowRedispatch = 1 << 6
    }

    /// <summary>Lifecycle of a plugin registered on a client.</summary>
    public enum PluginLifecycleState
    {
        /// <summary>Registered, not yet initialized.</summary>
        Created,
        /// <summary><see cref="IHttpPlugin.InitializeAsync"/> is running.</summary>
        Initializing,
        /// <summary>Initialized and active.</summary>
        Initialized,
        /// <summary>Initialization or shutdown failed.</summary>
        Faulted,
        /// <summary><see cref="IHttpPlugin.ShutdownAsync"/> is running.</summary>
        ShuttingDown,
        /// <summary>Shut down; the plugin is no longer used.</summary>
        Disposed
    }

    /// <summary>Read-only description of a registered plugin and its lifecycle state.</summary>
    public sealed class PluginDescriptor
    {
        /// <summary>Plugin name; empty when none was given.</summary>
        public string Name { get; }
        /// <summary>Plugin version; empty when none was given.</summary>
        public string Version { get; }
        /// <summary>Capabilities the plugin requested.</summary>
        public PluginCapabilities Capabilities { get; }
        /// <summary>Lifecycle state when the descriptor was created.</summary>
        public PluginLifecycleState State { get; }

        /// <summary>Creates a descriptor; null strings become empty.</summary>
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

    /// <summary>A plugin failed or used a capability it does not hold.</summary>
    public sealed class PluginException : Exception
    {
        /// <summary>Name of the plugin; empty when unknown.</summary>
        public string PluginName { get; }
        /// <summary>Where it failed, for example <c>initialize</c> or <c>diagnostics</c>; empty when unknown.</summary>
        public string Phase { get; }

        /// <summary>Creates the exception; null name or phase become empty.</summary>
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
