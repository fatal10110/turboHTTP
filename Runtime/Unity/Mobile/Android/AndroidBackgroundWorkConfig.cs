using System;

namespace TurboHTTP.Unity.Mobile.Android
{
    public sealed class AndroidBackgroundWorkConfig
    {
        public string PluginClassName { get; set; } = "com.turbohttp.background.TurboHttpBackgroundPlugin";
        public int GuardGraceMilliseconds { get; set; } = 2000;
        public bool EnableDeferredWork { get; set; } = true;
        public int MaxQueuedRequests { get; set; } = 256;

        public AndroidBackgroundWorkConfig Clone()
        {
            return new AndroidBackgroundWorkConfig
            {
                PluginClassName = PluginClassName,
                GuardGraceMilliseconds = GuardGraceMilliseconds,
                EnableDeferredWork = EnableDeferredWork,
                MaxQueuedRequests = MaxQueuedRequests
            };
        }
    }
}
