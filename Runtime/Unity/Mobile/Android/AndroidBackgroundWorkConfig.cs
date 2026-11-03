using System;

namespace TurboHTTP.Unity.Mobile.Android
{
    public sealed class AndroidBackgroundWorkConfig
    {
        public string PluginClassName { get; set; } = "com.turbohttp.background.TurboHttpBackgroundPlugin";
        public int GuardGraceMilliseconds { get; set; } = 2000;
        public bool EnableDeferredWork { get; set; } = true;
        public int MaxQueuedRequests { get; set; } = 256;

        /// <summary>
        /// How long, in milliseconds, requests may keep running after the app is paused before their
        /// background scopes expire (default 5000). On expiry
        /// <see cref="BackgroundNetworkingInterceptor"/> cancels the in-flight requests, queues the
        /// replayable ones and hands them to WorkManager; the others fail as they would in the
        /// foreground. Zero or less expires the scopes as soon as the app pauses. Android has no
        /// <c>backgroundTimeRemaining</c>, so this is an app-chosen budget: keep it short enough to run
        /// before the OS freezes or kills the paused process.
        /// </summary>
        public int BackgroundBudgetMilliseconds { get; set; } = 5000;

        public AndroidBackgroundWorkConfig Clone()
        {
            return new AndroidBackgroundWorkConfig
            {
                PluginClassName = PluginClassName,
                GuardGraceMilliseconds = GuardGraceMilliseconds,
                EnableDeferredWork = EnableDeferredWork,
                MaxQueuedRequests = MaxQueuedRequests,
                BackgroundBudgetMilliseconds = BackgroundBudgetMilliseconds
            };
        }
    }
}
