// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
#nullable enable
namespace DotKt.Bir;

public sealed partial record TypeParameterDeclaration(string Name, bool? Reified = null, string? Variance = null, TypeNode[]? Constraints = null, TypeNode[]? ErasedInnerConstraints = null, string[]? SpecialConstraints = null);
