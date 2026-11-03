using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>Releases Unity objects the handlers created but could not hand over (#94).</summary>
    internal static class UnityObjects
    {
        /// <summary>
        /// Destroys <paramref name="unityObject"/> (deferred to the end of the frame in play mode,
        /// immediate in edit mode). Main thread only.
        /// </summary>
        internal static void Destroy(Object unityObject)
        {
            if (unityObject == null)
                return;

            if (Application.isPlaying)
                Object.Destroy(unityObject);
            else
                Object.DestroyImmediate(unityObject);
        }
    }
}
