// Polyfill for .NET Standard 2.1 — Unity 2021.3 BCL does not include this attribute.
// The C# compiler recognizes this attribute for module initializer codegen regardless of
// where it is defined. Using `internal` prevents conflicts if Unity adds it in future versions.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif
