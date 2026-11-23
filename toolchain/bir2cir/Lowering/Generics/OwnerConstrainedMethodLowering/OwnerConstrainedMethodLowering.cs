using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Kotlin owner-dependent bounds describe the source view, not the receiver's exact CLR construction.
// Preserve the complete Kotlin constraint list in metadata and retain only representable physical rows.
// Calls through the existential slot then use ordinary typed forwarding, without reflective generic binding.
static partial class OwnerConstrainedMethodLowering
{
    internal const string DispatchBoundsKey = "_ownerConstraintDispatchBounds";
    internal const string OverrideBoundsKey = "_ownerConstraintOverrideBounds";
    static readonly List<MethodRegistration> Methods = new();
    static ReferenceMetadataIndex References;
    internal static void Reset(ReferenceMetadataIndex references)
    {
        Methods.Clear();
        MethodBindings = null;
        PreparedMethods = false;
        References = references;
    }

    internal static NullableRepresentationFrame ReadMethodFrame(JsonObject method) =>
        method[NullableRepresentationTypes.MethodFrameKey] is JsonValue encoded
            ? NullableRepresentationFrame.Read(JsonNode.Parse(encoded.GetValue<string>())) : null;

    internal static bool ProjectsBound(JsonObject method, int index, TypeNode bound,
        Func<TypeNode, bool> dependsOnOwner = null) =>
        ProjectsBound(index, bound, ReadMethodFrame(method), ReadOverrideBounds(method), References, dependsOnOwner);

    internal static bool ProjectsBound(int index, TypeNode bound, NullableRepresentationFrame frame,
        OwnerConstraintOverrideBounds overrides, ReferenceMetadataIndex references,
        Func<TypeNode, bool> dependsOnOwner = null) =>
        (dependsOnOwner ?? FBoundStarProjectionErasure.ContainsOwnerTv)(bound)
        || HasConstructedDependency(bound, "method", index, frame, references)
        || overrides?.Rows.Any(row => row.Index == index && row.Bound.Equals(bound)) == true;

    static bool SameSourceParameter(int first, int second, NullableRepresentationFrame frame) =>
        frame == null ? first == second
            : frame.PhysicalSlot(first).SourceIndex == frame.PhysicalSlot(second).SourceIndex;

    // A self F-bound keeps the exact same generic argument. A constructed bound depending on another parameter
    // can instead be satisfied through Kotlin variance without satisfying that exact closed CLR construction.
    internal static bool HasConstructedDependency(TypeNode bound, string scope, int self,
        NullableRepresentationFrame frame = null)
        => HasConstructedDependency(bound, scope, self, frame, References);

    internal static bool HasConstructedDependency(TypeNode bound, string scope, int self,
        NullableRepresentationFrame frame, ReferenceMetadataIndex references)
    {
        if (bound is TypeNode.Tv) return false;
        // An invariant foreign construction is already an exact CLR contract. Foreign constrained methods
        // and delegate types require the same physical row.
        var nominal = bound;
        while (nominal is TypeNode.Nullable or TypeNode.Oblivious)
            nominal = nominal is TypeNode.Nullable nullable ? nullable.Of : ((TypeNode.Oblivious)nominal).Of;
        // A binding that discards the entire generic application has no physical argument dependency.
        // In particular, representation companions must not turn its source self-bound into a
        // dependent bound and discard the non-generic CLR constraint that remains after lowering.
        if (nominal is TypeNode.Fqn erased
            && BirTypeLowering.ErasesGenericApplicationToNonGenericClassifier(erased.Name))
            return false;
        if (nominal is TypeNode.Fqn named && references.IsInvariantForeignConstruction(named))
            return false;
        var changed = false;
        MapVariables(bound, tv => {
            // Representation companions are views of the same source parameter, not
            // independent Kotlin variables that make a self-bound variant-dependent.
            if (tv.Scope == scope && !SameSourceParameter(tv.I, self, frame)) changed = true;
            return tv;
        });
        return changed;
    }

    internal static void SelfTest()
    {
        BodyOwnerViewSelfTest();
        OverrideRowsSelfTest();
        var invariantReferences = References;
        try
        {
            References = ReferenceMetadataIndex.Build(Array.Empty<string>());
            var argument = new TypeNode.Tv("method", 0);
            var constructed = new TypeNode.Fqn("Constructed", new TypeNode[] { argument });
            if (!HasConstructedDependency(constructed, "method", 1)
                || HasConstructedDependency(constructed, "method", 0)
                || !HasConstructedDependency(new TypeNode.Fqn("Constructed", new TypeNode[] {
                    new TypeNode.Projection("out", argument) }), "method", 1))
                throw new InvalidOperationException("A constructed bound lost its independent source-parameter dependency");
        }
        finally { References = invariantReferences; }
        var bound = new TypeNode.Fqn("kotlin.Enum", new TypeNode[] {
            new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 1),
        });
        if (HasConstructedDependency(bound, "method", 0)
            || HasConstructedDependency(new TypeNode.Nullable(bound), "method", 0))
            throw new InvalidOperationException("Non-generic physical bound acquired a companion dependency");
        if (!HasConstructedDependency(new TypeNode.Array(new TypeNode.Tv("method", 1)), "method", 0))
            throw new InvalidOperationException("Constructed bound lost its physical argument dependency");
        var frame = new NullableRepresentationFrame(2, new[] { 0, 1 }, new[] { 2, 1, 0, 3, 4, 5 },
            storageIndices: new[] { 0 }, nullableStorageIndices: new[] { 0 });
        foreach (var companion in new[] { 0, 4, 5 })
            if (HasConstructedDependency(new TypeNode.Array(new TypeNode.Tv("method", companion)), "method", 2, frame))
                throw new InvalidOperationException("Self-bound companion became an independent source parameter");
        foreach (var other in new[] { 1, 3 })
            if (!HasConstructedDependency(new TypeNode.Array(new TypeNode.Tv("method", other)), "method", 2, frame))
                throw new InvalidOperationException("Different source parameter lost its constructed dependency");
        ConstraintRewritePlanSelfTest(bound);
        CapturedBoundsSourceSelfTest();
        Console.WriteLine("[method bound dependencies] self-test OK (non-generic binding, storage companion, retained constraint, named/static capture)");
    }

    static TypeNode MapVariables(TypeNode type, Func<TypeNode.Tv, TypeNode> map) => type switch
    {
        TypeNode.Tv tv => map(tv),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(arg => MapVariables(arg, map)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(MapVariables(n.Of, map)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(MapVariables(o.Of, map)),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, MapVariables(p.Of, map)),
        TypeNode.Array a => new TypeNode.Array(MapVariables(a.Elem, map), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(MapVariables(b.Of, map)),
        TypeNode.Ptr p => new TypeNode.Ptr(MapVariables(p.Of, map)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, MapVariables(m.M, map), MapVariables(m.Of, map)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, MapVariables(fn.Ret, map),
            fn.Params.Select(arg => MapVariables(arg, map)).ToArray(),
            fn.Recv == null ? null : MapVariables(fn.Recv, map), fn.Clr,
            fn.Ctx?.Select(arg => MapVariables(arg, map)).ToArray()),
        _ => type,
    };

    static TypeNode ProjectMethodVariables(TypeNode bound, int self, NullableRepresentationFrame frame) =>
        MapVariables(bound, tv => tv.Scope == "method" && !SameSourceParameter(tv.I, self, frame)
            ? new TypeNode.Star() : tv);

    // Called only after the Kotlin override resolver has selected the exact source slot. In particular,
    // substituting Base<T> with Base<Animal> must not hide that its method bound originally depended on T.
    internal static void RecordOverride(JsonObject implementation, JsonArray slotParameters, TypeNode[] ownerArgs,
        JsonArray ownerParameters, JsonArray implementationOwnerParameters = null,
        Func<TypeNode.Fqn, TypeNode> storageConstraint = null, bool sourceParameters = false)
    {
        if (slotParameters == null) return;
        var rows = (ReadOverrideBounds(implementation) ?? new OwnerConstraintOverrideBounds(Array.Empty<OwnerConstraintOverrideBounds.Row>()))
            .Append(BuildOverrideRows(ReadMethodFrame(implementation),
                ConstraintVectors(implementation["typeParams"] as JsonArray), ConstraintVectors(slotParameters), ownerArgs,
                ConstraintVectors(ownerParameters), ConstraintVectors(implementationOwnerParameters), storageConstraint, sourceParameters));
        if (rows.Rows.Count > 0) implementation[OverrideBoundsKey] = OwnerConstraintOverrideBounds.ToDom(rows);
    }

    static OwnerConstraintOverrideBounds ReadOverrideBounds(JsonObject method) => method[OverrideBoundsKey] is JsonValue encoded
        ? OwnerConstraintOverrideBounds.ReadFrozenText(encoded.GetValue<string>()) : null;

    internal static void PreserveDispatchBounds(JsonObject declaration, JsonArray parameters)
    {
        var bounds = new JsonObject();
        for (var index = 0; index < parameters.Count; index++)
            if (parameters[index] is JsonObject parameter
                && parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey] is JsonArray removed)
                bounds[index.ToString()] = removed.DeepClone();
        // These are dispatch plans, not value/storage types. Preserve their projection masks across ordinary
        // type lowering until the constrained-receiver pass consumes the exact planned bound.
        declaration[DispatchBoundsKey] = bounds.ToJsonString();
    }

    internal static void PreserveDispatchBounds(BirFunctionDeclaration declaration, BirTypeParameter[] parameters)
        => declaration._ownerConstraintDispatchBounds = DispatchBoundsFromParameters(parameters);

    internal static void PreserveDispatchBounds(IBirTypeDeclaration declaration, BirTypeParameter[] parameters)
    {
        var bounds = DispatchBoundsFromParameters(parameters);
        switch (declaration)
        {
            case BirClassDefinition owner: owner._ownerConstraintDispatchBounds = bounds; break;
            case BirInterfaceDefinition owner: owner._ownerConstraintDispatchBounds = bounds; break;
            default: throw new InvalidOperationException("Unexpected dispatch-bound declaration owner");
        }
    }

    static OwnerConstraintDispatchBounds DispatchBoundsFromParameters(BirTypeParameter[] parameters)
    {
        var bounds = new Dictionary<int, TypeNode[]>();
        for (var index = 0; index < parameters.Length; index++)
            if (parameters[index].ErasedInnerConstraints.HasValue)
                bounds.Add(index, parameters[index].ErasedInnerConstraints.Value);
        return new OwnerConstraintDispatchBounds(bounds);
    }

    internal static IEnumerable<TypeNode> IndependentBounds(TypeNode bound, JsonArray ownerParameters) =>
        IndependentBounds(bound, ownerParameters?.Count ?? 0, index =>
            ownerParameters[index] is JsonObject parameter && parameter["constraints"] is JsonArray constraints
                ? constraints.Select(TypeJson.Read) : Enumerable.Empty<TypeNode>());

    internal static IEnumerable<TypeNode> IndependentBounds(TypeNode bound, IReadOnlyList<BirTypeParameter> ownerParameters) =>
        IndependentBounds(bound, ownerParameters?.Count ?? 0, index => ownerParameters[index].Constraints.OrEmpty());

    static IEnumerable<TypeNode> IndependentBounds(TypeNode bound, int arity, Func<int, IEnumerable<TypeNode>> constraints)
    {
        var visited = new HashSet<int>();
        IEnumerable<TypeNode> Visit(TypeNode current)
        {
            if (current is TypeNode.Tv { Scope: "type" } tv)
            {
                if (!visited.Add(tv.I) || tv.I < 0 || tv.I >= arity)
                    yield break;
                foreach (var constraint in constraints(tv.I))
                    foreach (var result in Visit(constraint)) yield return result;
            }
            else if (!FBoundStarProjectionErasure.ContainsOwnerTv(current)) yield return current;
        }
        return Visit(bound).Distinct();
    }
}
