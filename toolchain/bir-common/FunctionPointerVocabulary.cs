// Kotlin-visible, compile-time interop vocabulary. These names are not CLR TypeDefs.
// Generic arguments carry every type dependency, so ordinary Kotlin substitution also
// substitutes variables nested in a native signature. No serialized type-name template.
namespace DotKt.Bir;

public static class FunctionPointerVocabulary
{
    public const string Pointer = "kotlin.clr.ClrFunctionPointer";
    public const string Required = "kotlin.clr.signature.Required";
    public const string Optional = "kotlin.clr.signature.Optional";
    public const string Value = "kotlin.clr.signature.Value";
    public static readonly IReadOnlyDictionary<int, string> Conventions = new Dictionary<int, string> {
        [0] = "kotlin.clr.signature.Managed",
        [1] = "kotlin.clr.signature.Cdecl",
        [2] = "kotlin.clr.signature.Stdcall",
        [3] = "kotlin.clr.signature.Thiscall",
        [4] = "kotlin.clr.signature.Fastcall",
        [9] = "kotlin.clr.signature.Unmanaged",
    };
    // A non-vector array has no ordinary Kotlin array spelling. Rank is part of
    // its type identity, including rank one (T[*], not T[]).
    public static readonly IReadOnlyDictionary<int, string> Arrays = Enumerable.Range(1, TypeNode.MaxArrayRank)
        .ToDictionary(rank => rank, rank => $"kotlin.clr.signature.Array{rank}");
    public static readonly IReadOnlyDictionary<string, int> ArrayRanks = Arrays
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
}
