#if UNITY_ANDROID && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
using UnityEngine;

namespace TurboHTTP.Unity.Mobile.Android
{
    /// <summary>
    /// Reports <c>OnApplicationPause</c> to <see cref="AndroidAppPauseSource"/> in opted-in Android
    /// players, where the background bridge starts its expiry budget from it (#267).
    /// </summary>
    internal sealed class AndroidAppPauseListener : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("[TurboHTTP AndroidAppPauseListener]");
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            go.AddComponent<AndroidAppPauseListener>();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            AndroidAppPauseSource.Report(pauseStatus);
        }
    }
}
#endif
