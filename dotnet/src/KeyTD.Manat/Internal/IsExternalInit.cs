// Lets netstandard2.0 use init-only setters (the compiler only needs the type to exist).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
