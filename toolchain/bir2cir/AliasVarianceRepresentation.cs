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
        JsonArray sourceParameters = null, string binding = null)
    {
        if (application.Args is not { Length: > 0 } arguments) return false;
        if (binding == null && !refs.Aliases.TryGetValue(application.Name, out binding)) return false;
        sourceParameters ??= refs.OwnerTypeParamDeclarations(application.Name);
        if (sourceParameters?.Count != arguments.Length) return false;
        var physical = refs.ResolveNetType(binding, arguments.Length);
        if (physical == null || !physical.IsGenericType || physical.IsValueType) return false;
        return HasVarianceMismatch(sourceParameters, physical);
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
        Console.WriteLine("[alias variance] self-test OK (invariant target, covariant/contravariant agreement)");
    }
}
