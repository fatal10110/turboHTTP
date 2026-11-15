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

        /// <summary>
        /// Player-loop timing the adapters yield to before returning a response when the call passes none.
        /// The default, <see cref="PlayerLoopTiming.Update"/>, adds no yield: the result is returned where the
        /// response await resumed, which is the main thread only when the call started on the main thread.
        /// Any other value resumes on the main thread in that player-loop phase, from any calling thread.
        /// </summary>
        public static PlayerLoopTiming DefaultPlayerLoopTiming
        {
            get => (PlayerLoopTiming)Volatile.Read(ref _defaultPlayerLoopTiming);
            set => Interlocked.Exchange(ref _defaultPlayerLoopTiming, (int)value);
        }
    }
}
#endif
