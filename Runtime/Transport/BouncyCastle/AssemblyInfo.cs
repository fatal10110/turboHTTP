using UnityEngine.Scripting;

// Nothing in a player references TurboHTTP.Transport.BouncyCastle (it is opt-in through the
// TURBOHTTP_BOUNCYCASTLE define and registers itself), so without this UnityLinker may skip the
// assembly and BouncyCastleModuleRegistration never runs (#196).
[assembly: AlwaysLinkAssembly]
