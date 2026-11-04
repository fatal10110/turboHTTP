using TurboHTTP.Core;
using TurboHTTP.Unity.Mobile.Android;
using TurboHTTP.Unity.Mobile.iOS;

namespace TurboHTTP.Unity.Mobile
{
    internal static class BackgroundExecutionBridgeFactory
    {
        public static IBackgroundExecutionBridge CreateDefault()
        {
            // Native bridges are experimental and opt-in (#106): without the define the plugins are not
            // built in, so requests run with foreground semantics and no native code is called.
#if UNITY_IOS && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
            return new IosBackgroundTaskBridge();
#elif UNITY_ANDROID && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
            return new AndroidBackgroundWorkBridge();
#else
            return null;
#endif
        }
    }
}
