// Polyfill for .NET Standard 2.1 — Unity 2021.3 BCL does not include this attribute.
#if !NET5_0_OR_GREATER
using System;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif
