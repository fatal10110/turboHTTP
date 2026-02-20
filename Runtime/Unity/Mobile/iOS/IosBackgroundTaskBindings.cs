using System;
using System.Runtime.InteropServices;

namespace TurboHTTP.Unity.Mobile.iOS
{
    internal static class IosBackgroundTaskBindings
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int turbohttp_begin_background_task(string taskName);

        [DllImport("__Internal")]
        private static extern void turbohttp_end_background_task(int taskId);

        [DllImport("__Internal")]
        private static extern double turbohttp_background_time_remaining();
#endif

        public static int BeginBackgroundTask(string taskName)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                return turbohttp_begin_background_task(taskName);
            }
            catch
            {
                return -1;
            }
#else
            return -1;
#endif
        }

        public static void EndBackgroundTask(int taskId)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (taskId < 0)
                return;

            try
            {
                turbohttp_end_background_task(taskId);
            }
            catch
            {
            }
#endif
        }

        public static TimeSpan GetRemainingTime()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                var seconds = turbohttp_background_time_remaining();
                if (seconds < 0)
                    seconds = 0;
                return TimeSpan.FromSeconds(seconds);
            }
            catch
            {
                return TimeSpan.Zero;
            }
#else
            return TimeSpan.Zero;
#endif
        }
    }
}
