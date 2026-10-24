using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin declaration-site variance includes value-type arguments; CLR interface variance does not.
// Kotlin collection interface casts also erase their element arguments, even for invariant mutable interfaces.
// These aliases therefore cannot use any one reified CLR construction as their ordinary value ABI,
// even when its CLR declaration has matching variance flags.
// Keep the source application in metadata, but carry the original reference through an opaque value slot.
// Exact CLR declarations (constructors, inheritance and bound member descriptors) remain reified.
static class AliasVarianceRepresentation
{
    internal static bool RequiresErasure(TypeNode.Fqn application, ReferenceMetadataIndex refs,
        JsonArray sourceParameters = null, string binding = null, NullableRepresentationFrame sourceFrame = null)
    {
        if (application.Args is not { Length: > 0 } arguments) return false;
        if (binding == null && !refs.Aliases.TryGetValue(application.Name, out binding)) return false;
        // Ordinary values must admit every Kotlin-variant view of the generic declaration.
        // A collapsed native head does not make those values implement its non-generic face.
        if (sourceParameters == null)
        {
            sourceParameters = refs.OwnerTypeParamDeclarations(application.Name);
            sourceFrame ??= refs.NullableTypeFrames.GetValueOrDefault(application.Name);
        }
        if (sourceParameters?.Count != arguments.Length) return false;
        var ordinary = OrdinaryParameters(sourceParameters, sourceFrame);
        var physical = refs.ResolveNetType(binding, ordinary.Count);
        if (physical == null || !physical.IsGenericType || physical.IsValueType) return false;
        return NeedsOpaqueValue(application, ordinary, physical);
    }

    static bool NeedsOpaqueValue(TypeNode.Fqn application, JsonArray parameters, Type physical) =>
        HasUnrepresentableVariance(parameters, physical) || (KotlinTypeWitness.Flags(application) & ~1) != 0;

    internal static bool HasSourceVariance(TypeNode.Fqn application, ReferenceMetadataIndex refs) =>
        OrdinaryParameters(refs.OwnerTypeParamDeclarations(application.Name),
            refs.NullableTypeFrames.GetValueOrDefault(application.Name))?.OfType<JsonObject>()
            .Any(parameter => parameter["variance"]?.GetValue<string>() is "in" or "out") == true;

    // Companions describe representations of source arguments, not new CLR
    // alias parameters. The explicit declaration frame owns this correspondence.
    static JsonArray OrdinaryParameters(JsonArray parameters, NullableRepresentationFrame frame)
    {
        if (frame == null) return parameters;
        if (parameters.Count != frame.PhysicalArity)
            throw new InvalidOperationException("Alias variance declaration disagrees with its representation frame");
        return new JsonArray(Enumerable.Range(0, frame.SourceArity)
            .Select(index => parameters[frame.SourcePosition(index)]?.DeepClone()).ToArray());
    }

    static bool HasUnrepresentableVariance(JsonArray sourceParameters, Type physical)
    {
        var parameters = physical.GetGenericArguments();
        if (parameters.Length != sourceParameters.Count) return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            var source = (sourceParameters[index] as JsonObject)?["variance"]?.GetValue<string>();
            if (source is "out" or "in") return true;
        }
        return false;
    }

    // This frame describes readable results, not a construction witness. A Kotlin covariant application may
    // have arrived from a different value-element CLR closure, even when its source argument is not a star.
    internal static TypeNode[] ResultOwnerArguments(TypeNode.Fqn application, ReferenceMetadataIndex refs)
    {
        if (!RequiresErasure(application, refs)) return application.Args;
        var parameters = OrdinaryParameters(refs.OwnerTypeParamDeclarations(application.Name),
            refs.NullableTypeFrames.GetValueOrDefault(application.Name));
        var arguments = refs.NullableTypeFrames.GetValueOrDefault(application.Name)
            ?.OrdinaryArguments(application.Args) ?? application.Args;
        return ProjectVariantArguments(arguments, parameters);
    }

    static TypeNode[] ProjectVariantArguments(TypeNode[] arguments, JsonArray parameters) =>
        arguments.Select((argument, index) => {
            var variance = (parameters[index] as JsonObject)?["variance"]?.GetValue<string>();
            return argument is not (TypeNode.Star or TypeNode.Projection) && variance is "out" or "in"
                ? new TypeNode.Projection(variance, argument) : argument;
        }).ToArray();

    internal static void SelfTest()
    {
        var covariant = JsonNode.Parse("""[{"name":"T","variance":"out"}]""").AsArray();
        var contravariant = JsonNode.Parse("""[{"name":"T","variance":"in"}]""").AsArray();
        var invariant = JsonNode.Parse("""["T"]""").AsArray();
        var map = JsonNode.Parse("""["K",{"name":"V","variance":"out"}]""").AsArray();
        foreach (var source in new[] { "kotlin.collections.MutableCollection", "kotlin.collections.MutableList", "kotlin.collections.MutableSet" })
            if (!NeedsOpaqueValue(new TypeNode.Fqn(source, new[] { new TypeNode.Fqn("kotlin.Any") }), invariant,
                typeof(System.Collections.Generic.ICollection<>)))
                throw new InvalidOperationException("Kotlin mutable collection casts must not require an invariant CLR construction");
        if (!NeedsOpaqueValue(new TypeNode.Fqn("kotlin.collections.MutableMap", new[] {
                new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.Any") }),
            JsonNode.Parse("""["K","V"]""").AsArray(), typeof(System.Collections.Generic.IDictionary<,>)))
            throw new InvalidOperationException("Kotlin mutable map casts must not require an invariant CLR construction");
        if (NeedsOpaqueValue(new TypeNode.Fqn("System.Collections.Generic.ICollection", new[] { new TypeNode.Fqn("kotlin.Any") }),
            invariant, typeof(System.Collections.Generic.ICollection<>)))
            throw new InvalidOperationException("A native CLR interface lost its exact construction contract");
        if (!HasUnrepresentableVariance(map, typeof(System.Collections.Generic.IDictionary<,>))
            || !HasUnrepresentableVariance(covariant, typeof(System.Collections.Generic.IList<>))
            || !HasUnrepresentableVariance(covariant, typeof(System.Collections.Generic.IReadOnlyList<>))
            || !HasUnrepresentableVariance(contravariant, typeof(IComparable<>))
            || HasUnrepresentableVariance(invariant, typeof(System.Collections.Generic.IList<>)))
            throw new InvalidOperationException("Alias variance projection confused Kotlin and CLR variance");
        var framedMap = JsonNode.Parse("""["$storage0", "K", "$storage1", {"name":"V","variance":"out"}]""").AsArray();
        var mapFrame = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 2, 0, 3, 1 }, new[] { 0, 1 });
        if (!HasUnrepresentableVariance(OrdinaryParameters(framedMap, mapFrame), typeof(System.Collections.Generic.IDictionary<,>)))
            throw new InvalidOperationException("Alias variance treated storage companions as CLR alias parameters");
        if (ProjectVariantArguments(new TypeNode[] { new TypeNode.Tv("type", 0) }, covariant)[0]
            is not TypeNode.Projection { Variance: "out", Of: TypeNode.Tv { I: 0 } })
            throw new InvalidOperationException("A variant readable owner argument became an exact CLR witness");
        Console.WriteLine("[alias variance] self-test OK (value-type variance, invariant storage, declaration frames)");
    }
}
