#if TURBOHTTP_UNITASK
using System.Threading;
using Cysharp.Threading.Tasks;

namespace TurboHTTP.UniTask
{
    /// <summary>
    /// Global configuration for TurboHTTP UniTask adapters.
    /// </summary>
    public static class TurboHttpUniTaskOptions
    {
        private static int _defaultPlayerLoopTiming = (int)PlayerLoopTiming.Update;

        public static PlayerLoopTiming DefaultPlayerLoopTiming
        {
            get => (PlayerLoopTiming)Volatile.Read(ref _defaultPlayerLoopTiming);
            set => Interlocked.Exchange(ref _defaultPlayerLoopTiming, (int)value);
        }
    }
}
#endif
