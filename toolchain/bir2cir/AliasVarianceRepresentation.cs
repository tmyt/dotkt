using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin generic aliases cannot use a reified CLR construction as their general value ABI.
// Invariant aliases also need this boundary: a source argument's existential carrier is not
// the concrete argument of an inherited native construction. CLR variance additionally
// only relates reference-type arguments, and has no Kotlin Nothing bottom type.
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
        return RequiresOpaqueReferenceValues(sourceParameters, physical);
    }

    static bool RequiresOpaqueReferenceValues(JsonArray sourceParameters, Type physical)
    {
        var parameters = physical.GetGenericArguments();
        return parameters.Length != 0 && parameters.Length == sourceParameters.Count;
    }

    internal static void SelfTest()
    {
        var covariant = JsonNode.Parse("""[{"name":"T","variance":"out"}]""").AsArray();
        var contravariant = JsonNode.Parse("""[{"name":"T","variance":"in"}]""").AsArray();
        var invariant = JsonNode.Parse("""["T"]""").AsArray();
        var map = JsonNode.Parse("""["K",{"name":"V","variance":"out"}]""").AsArray();
        if (!RequiresOpaqueReferenceValues(map, typeof(System.Collections.Generic.IDictionary<,>))
            || !RequiresOpaqueReferenceValues(covariant, typeof(System.Collections.Generic.IList<>))
            || !RequiresOpaqueReferenceValues(covariant, typeof(System.Collections.Generic.IReadOnlyList<>))
            || !RequiresOpaqueReferenceValues(contravariant, typeof(IComparable<>))
            || !RequiresOpaqueReferenceValues(invariant, typeof(System.Collections.Generic.IList<>)))
            throw new InvalidOperationException("Generic alias storage retained a potentially different CLR construction");
        if (typeof(System.Collections.Generic.IReadOnlyList<object>).IsAssignableFrom(
                typeof(System.Collections.Generic.IReadOnlyList<int>)))
            throw new InvalidOperationException("CLR value-element variance unexpectedly became assignable");
        Console.WriteLine("[alias variance] self-test OK (Kotlin variance requires identity-preserving opaque value ABI)");
    }
}
