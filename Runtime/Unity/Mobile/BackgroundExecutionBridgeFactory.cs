using TurboHTTP.Core;
using TurboHTTP.Unity.Mobile.Android;
using TurboHTTP.Unity.Mobile.iOS;

namespace TurboHTTP.Unity.Mobile
{
    internal static class BackgroundExecutionBridgeFactory
    {
        public static IBackgroundExecutionBridge CreateDefault()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new IosBackgroundTaskBridge();
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidBackgroundWorkBridge();
#else
            return null;
#endif
        }
    }
}
