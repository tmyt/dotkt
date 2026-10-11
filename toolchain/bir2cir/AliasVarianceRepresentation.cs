using System;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A Kotlin declaration-site variant alias cannot use an invariant CLR construction as its value ABI.
// Keep the source application in metadata, but carry the original reference through an opaque value slot.
// Exact CLR declarations (constructors, inheritance and bound member descriptors) remain reified.
static class AliasVarianceRepresentation
{
    internal static bool RequiresErasure(TypeNode.Fqn application, ReferenceMetadataIndex refs,
        JsonArray sourceParameters = null, string binding = null, NullableRepresentationFrame sourceFrame = null)
    {
        if (application.Args is not { Length: > 0 } arguments) return false;
        if (binding == null && !refs.Aliases.TryGetValue(application.Name, out binding)) return false;
        if (sourceParameters == null)
        {
            sourceParameters = refs.OwnerTypeParamDeclarations(application.Name);
            sourceFrame ??= refs.NullableTypeFrames.GetValueOrDefault(application.Name);
        }
        if (sourceParameters?.Count != arguments.Length) return false;
        var ordinary = OrdinaryParameters(sourceParameters, sourceFrame);
        var physical = refs.ResolveNetType(binding, ordinary.Count);
        if (physical == null || !physical.IsGenericType || physical.IsValueType) return false;
        return HasVarianceMismatch(ordinary, physical);
    }

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

    static bool HasVarianceMismatch(JsonArray sourceParameters, Type physical)
    {
        var parameters = physical.GetGenericArguments();
        if (parameters.Length != sourceParameters.Count) return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            var source = (sourceParameters[index] as JsonObject)?["variance"]?.GetValue<string>();
            var required = source switch {
                "out" => GenericParameterAttributes.Covariant,
                "in" => GenericParameterAttributes.Contravariant,
                _ => GenericParameterAttributes.None,
            };
            if (required != GenericParameterAttributes.None
                && (parameters[index].GenericParameterAttributes & GenericParameterAttributes.VarianceMask) != required)
                return true;
        }
        return false;
    }

    internal static void SelfTest()
    {
        var covariant = JsonNode.Parse("""[{"name":"T","variance":"out"}]""").AsArray();
        var contravariant = JsonNode.Parse("""[{"name":"T","variance":"in"}]""").AsArray();
        var invariant = JsonNode.Parse("""["T"]""").AsArray();
        var map = JsonNode.Parse("""["K",{"name":"V","variance":"out"}]""").AsArray();
        if (!HasVarianceMismatch(map, typeof(System.Collections.Generic.IDictionary<,>))
            || !HasVarianceMismatch(covariant, typeof(System.Collections.Generic.IList<>))
            || HasVarianceMismatch(covariant, typeof(System.Collections.Generic.IReadOnlyList<>))
            || HasVarianceMismatch(contravariant, typeof(IComparable<>))
            || HasVarianceMismatch(invariant, typeof(System.Collections.Generic.IList<>)))
            throw new InvalidOperationException("Alias variance projection confused Kotlin and CLR variance");
        var framedMap = JsonNode.Parse("""["$storage0", "K", "$storage1", {"name":"V","variance":"out"}]""").AsArray();
        var mapFrame = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 2, 0, 3, 1 }, new[] { 0, 1 });
        if (!HasVarianceMismatch(OrdinaryParameters(framedMap, mapFrame), typeof(System.Collections.Generic.IDictionary<,>)))
            throw new InvalidOperationException("Alias variance treated storage companions as CLR alias parameters");
        Console.WriteLine("[alias variance] self-test OK (invariant target, covariant/contravariant agreement)");
    }
}
