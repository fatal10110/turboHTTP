using System.Runtime.CompilerServices;
using UnityEngine.Scripting;

[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]

// Nothing in a player references TurboHTTP.Complete, so without this UnityLinker may skip the
// assembly entirely and the [Preserve]/[RuntimeInitializeOnLoadMethod] bootstrap never runs.
[assembly: AlwaysLinkAssembly]
