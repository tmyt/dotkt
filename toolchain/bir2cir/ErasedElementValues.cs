using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// Open Kotlin arrays and iterators preserve their actual elements, not the caller's concrete T.
// Their elements are erased values until an actual consuming slot asks for T.
// Project only body-local value storage here; signatures, allocation witnesses, reified
// operations and native locations retain their independently owned representations.
static partial class ErasedElementValues
{
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");
    static TypeNode.Tv Variable(TypeNode type) => type switch {
        TypeNode.Tv variable => variable,
        TypeNode.Projection projection => Variable(projection.Of),
        _ => null,
    };

    // Source and physical readers supply the same declaration-owned bounds. The
    // choice of representative bound and its frame mapping lives only here.
    internal sealed record BoundDeclaration(NullableRepresentationFrame Frame, IReadOnlyList<TypeNode[]> Constraints,
        IReadOnlyDictionary<int, TypeNode[]> SavedBounds);

    // A non-reified parameter promises its declaration-owned upper bound,
    // not the caller's closed CLR instantiation.
    internal static TypeNode ErasedBound(TypeNode.Tv variable, BoundDeclaration method, BoundDeclaration owner,
        Func<TypeNode, bool> isInterface, IReadOnlyDictionary<string, NullableRepresentationFrame> frames = null,
        ValueTypeOracle isValue = null, GenericRepresentationPolicy policy = null, HashSet<TypeNode.Tv> visited = null)
    {
        visited ??= new HashSet<TypeNode.Tv>();
        if (!visited.Add(variable)) throw new InvalidOperationException("Cyclic erased element bound");
        var declaration = variable.Scope == "method" ? method : owner;
        var frame = declaration?.Frame;
        var index = frame?.PhysicalSlot(variable.I).SourceIndex ?? variable.I;
        var position = frame?.SourcePosition(index) ?? variable.I;
        var source = declaration?.SavedBounds?.GetValueOrDefault(index);
        var bounds = source ?? (position < (declaration?.Constraints.Count ?? 0)
            ? declaration.Constraints[position] : Array.Empty<TypeNode>());
        if (source != null)
        {
            var mapping = new NullableRepresentationTypes(
                owner?.Frame, method?.Frame,
                frames ?? new Dictionary<string, NullableRepresentationFrame>(), isValue ?? (_ => false), policy: policy);
            bounds = bounds.Select(mapping.Slot).ToArray();
        }
        var bound = bounds.FirstOrDefault(type => !isInterface(type)) ?? bounds.FirstOrDefault() ?? ObjectType;
        return bound is TypeNode.Tv parent
            ? ErasedBound(parent, method, owner, isInterface, frames, isValue, policy, visited) : bound;
    }

    static bool IsObject(TypeNode type) => type is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any" }
        || type is TypeNode.Nullable nullable && IsObject(nullable.Of);

    internal static void SelfTest()
    {
        SourceScalarSelfTest();
        SourceElementSelfTest();
    }
}
