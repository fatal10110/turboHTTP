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
        string Name { get; }
        string Version { get; }
        PluginCapabilities Capabilities { get; }

        ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken);
        ValueTask ShutdownAsync(CancellationToken cancellationToken);
    }

    [Flags]
    public enum PluginCapabilities
    {
        None = 0,
        ObserveRequests = 1 << 0,
        ReadOnlyMonitoring = 1 << 1,
        MutateRequests = 1 << 2,
        MutateResponses = 1 << 3,
        HandleErrors = 1 << 4,
        Diagnostics = 1 << 5
    }

    public enum PluginLifecycleState
    {
        Created,
        Initializing,
        Initialized,
        Faulted,
        ShuttingDown,
        Disposed
    }

    public sealed class PluginDescriptor
    {
        public string Name { get; }
        public string Version { get; }
        public PluginCapabilities Capabilities { get; }
        public PluginLifecycleState State { get; }

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

    public sealed class PluginException : Exception
    {
        public string PluginName { get; }
        public string Phase { get; }

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
