using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Open Kotlin arrays and iterators preserve their actual elements, not the caller's concrete T.
// Their elements are erased values until an actual consuming slot asks for T.
// Project only body-local value storage here; signatures, allocation witnesses, reified
// operations and native locations retain their independently owned representations.
static partial class ErasedElementValues
{
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");
    const string SourceVariableKey = "_erasedValueVariable";

    internal static TypeNode.Tv RetainedVariable(JsonObject value) =>
        TypeJson.Read(value[SourceVariableKey]) as TypeNode.Tv;

    static TypeNode.Tv Variable(TypeNode type) => type switch {
        TypeNode.Tv variable => variable,
        TypeNode.Projection projection => Variable(projection.Of),
        _ => null,
    };

    // Shared by erased reads and generic query entry slots. A non-reified parameter promises its declared
    // upper bound, not the caller's closed CLR instantiation. Representation-frame metadata owns that bound.
    internal static TypeNode ErasedBound(TypeNode.Tv variable, JsonObject method, JsonObject owner,
        Func<TypeNode, bool> isInterface, IReadOnlyDictionary<string, NullableRepresentationFrame> frames = null,
        ValueTypeOracle isValue = null, GenericRepresentationPolicy policy = null, HashSet<TypeNode.Tv> visited = null)
        => ErasedBound(variable, MethodBounds(method), OwnerBounds(owner), isInterface, frames, isValue, policy, visited);

    // Source and physical readers supply the same declaration-owned bounds. The
    // choice of representative bound and its frame mapping lives only here.
    internal sealed record BoundDeclaration(NullableRepresentationFrame Frame, IReadOnlyList<TypeNode[]> Constraints,
        IReadOnlyDictionary<int, TypeNode[]> SavedBounds);

    internal static BoundDeclaration MethodBounds(JsonObject method) => new(
        method == null ? null : NullableRepresentationTypes.DeclarationMethodFrame(method),
        DeclarationConstraints((method?["typeParams"] as JsonArray ?? new JsonArray())),
        Text(method?[NullableGenericErasure.MethodTypeParameterBoundsPre]) is string saved
            ? SourceBoundsSnapshot.ReadFrozenText(saved).Bounds : null);

    internal static BoundDeclaration OwnerBounds(JsonObject owner) => new(
        owner == null ? null : KotlinSupertypesRecord.ReadNullableFrame(owner),
        DeclarationConstraints(TypeParameterFrame.Declarations(owner)),
        owner == null ? null : KotlinSupertypesRecord.ReadBounds(owner));

    static TypeNode[][] DeclarationConstraints(IEnumerable<JsonNode> parameters) => parameters.Select(parameter =>
        parameter is JsonObject declaration && declaration["constraints"] is JsonArray constraints
            ? constraints.Select(TypeJson.Read).Where(type => type != null).ToArray()
            : Array.Empty<TypeNode>()).ToArray();

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

    static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
