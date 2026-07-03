// * Polyfills for C# 9 (init) and C# 11 (required) on netstandard2.1.
// *   These symbols ship with net5+/net7+ respectively — on older targets the
// *   compiler still accepts the syntax as long as the attributes exist in
// *   *some* referenced assembly. Internal visibility keeps them out of the
// *   public API surface.

#if !NET8_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct |
                           System.AttributeTargets.Field | System.AttributeTargets.Property,
                           AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : System.Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) { FeatureName = featureName; }
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [System.AttributeUsage(System.AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : System.Attribute { }
}
#endif
