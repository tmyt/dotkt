using System;
using System.Collections.Generic;
using System.Linq;
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
        => RequiresErasureFromSource(application, refs, Variances(sourceParameters), binding, sourceFrame);

    internal static bool RequiresErasureFromSource(TypeNode.Fqn application, ReferenceMetadataIndex refs,
        IReadOnlyList<string> sourceVariances, string binding = null, NullableRepresentationFrame sourceFrame = null)
    {
        if (application.Args is not { Length: > 0 } arguments) return false;
        if (binding == null && !refs.Aliases.TryGetValue(application.Name, out binding)) return false;
        // Ordinary values must admit every Kotlin-variant view of the generic declaration.
        // A collapsed native head does not make those values implement its non-generic face.
        sourceVariances = ResolveSourceVariances(application, refs, sourceVariances, ref sourceFrame);
        if (!HasApplicationArity(arguments.Length, sourceVariances, sourceFrame)) return false;
        var ordinary = OrdinaryParameters(sourceVariances, sourceFrame);
        var physical = refs.ResolveNetType(binding, ordinary.Count);
        if (physical == null || !physical.IsGenericType || physical.IsValueType) return false;
        return NeedsOpaqueValue(application, ordinary, physical);
    }

    static bool NeedsOpaqueValue(TypeNode.Fqn application, IReadOnlyList<string> parameters, Type physical) =>
        HasUnrepresentableVariance(parameters, physical) || (KotlinTypeWitness.Flags(application) & ~1) != 0;

    // Referenced declaration slots retain source alias arguments, while materialized value applications carry
    // the explicit representation frame. Both spell the same Kotlin alias and therefore choose the same value ABI.
    static bool HasApplicationArity(int arity, IReadOnlyList<string> parameters, NullableRepresentationFrame frame) =>
        parameters != null && (frame == null ? arity == parameters.Count
            : arity == frame.SourceArity || arity == frame.PhysicalArity);

    internal static bool HasSourceVariance(TypeNode.Fqn application, ReferenceMetadataIndex refs) =>
        OrdinaryParameters(ReferencedVariances(application, refs),
            refs.NullableTypeFrames.GetValueOrDefault(application.Name))?.Any(variance => variance is "in" or "out") == true;

    static IReadOnlyList<string> Variances(JsonArray parameters) => parameters?.Select(parameter =>
        (parameter as JsonObject)?["variance"]?.GetValue<string>()).ToArray();

    static IReadOnlyList<string> ReferencedVariances(TypeNode.Fqn application, ReferenceMetadataIndex refs) =>
        refs.OwnerTypeParametersInPhysicalFrame(application.Name)?.Select(parameter => parameter.Variance.OrDefault()).ToArray();

    static IReadOnlyList<string> ResolveSourceVariances(TypeNode.Fqn application, ReferenceMetadataIndex refs,
        IReadOnlyList<string> sourceVariances, ref NullableRepresentationFrame sourceFrame)
    {
        // Null is absent declaration facts; an explicit empty declaration must not
        // acquire the referenced declaration's parameters or frame.
        if (sourceVariances != null) return sourceVariances;
        sourceFrame ??= refs.NullableTypeFrames.GetValueOrDefault(application.Name);
        return ReferencedVariances(application, refs);
    }

    // Companions describe representations of source arguments, not new CLR
    // alias parameters. The explicit declaration frame owns this correspondence.
    static IReadOnlyList<string> OrdinaryParameters(IReadOnlyList<string> parameters, NullableRepresentationFrame frame)
    {
        if (frame == null) return parameters;
        if (parameters.Count != frame.PhysicalArity)
            throw new InvalidOperationException("Alias variance declaration disagrees with its representation frame");
        return Enumerable.Range(0, frame.SourceArity).Select(index => parameters[frame.SourcePosition(index)]).ToArray();
    }

    static bool HasUnrepresentableVariance(IReadOnlyList<string> sourceParameters, Type physical)
    {
        var parameters = physical.GetGenericArguments();
        if (parameters.Length != sourceParameters.Count) return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            var source = sourceParameters[index];
            if (source is "out" or "in") return true;
        }
        return false;
    }

    // This frame describes readable results, not a construction witness. A Kotlin covariant application may
    // have arrived from a different value-element CLR closure, even when its source argument is not a star.
    internal static TypeNode[] ResultOwnerArguments(TypeNode.Fqn application, ReferenceMetadataIndex refs)
    {
        if (!RequiresErasure(application, refs)) return application.Args;
        var parameters = OrdinaryParameters(ReferencedVariances(application, refs),
            refs.NullableTypeFrames.GetValueOrDefault(application.Name));
        var arguments = SourceArguments(application.Args, refs.NullableTypeFrames.GetValueOrDefault(application.Name));
        return ProjectVariantArguments(arguments, parameters);
    }

    static TypeNode[] SourceArguments(TypeNode[] arguments, NullableRepresentationFrame frame) =>
        frame == null || arguments.Length == frame.SourceArity && frame.SourceArity != frame.PhysicalArity
            ? arguments : frame.OrdinaryArguments(arguments);

    static TypeNode[] ProjectVariantArguments(TypeNode[] arguments, IReadOnlyList<string> parameters) =>
        arguments.Select((argument, index) => {
            var variance = parameters[index];
            return argument is not (TypeNode.Star or TypeNode.Projection) && variance is "out" or "in"
                ? new TypeNode.Projection(variance, argument) : argument;
        }).ToArray();

    internal static void SelfTest()
    {
        var covariant = new[] { "out" };
        var contravariant = new[] { "in" };
        var invariant = new string[] { null };
        var map = new string[] { null, "out" };
        foreach (var source in new[] { "kotlin.collections.MutableCollection", "kotlin.collections.MutableList", "kotlin.collections.MutableSet" })
            if (!NeedsOpaqueValue(new TypeNode.Fqn(source, new[] { new TypeNode.Fqn("kotlin.Any") }), invariant,
                typeof(System.Collections.Generic.ICollection<>)))
                throw new InvalidOperationException("Kotlin mutable collection casts must not require an invariant CLR construction");
        if (!NeedsOpaqueValue(new TypeNode.Fqn("kotlin.collections.MutableMap", new[] {
                new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.Any") }),
            new string[] { null, null }, typeof(System.Collections.Generic.IDictionary<,>)))
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
        var framedMap = new string[] { null, null, null, "out" };
        var mapFrame = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 2, 0, 3, 1 }, new[] { 0, 1 });
        if (!HasUnrepresentableVariance(OrdinaryParameters(framedMap, mapFrame), typeof(System.Collections.Generic.IDictionary<,>)))
            throw new InvalidOperationException("Alias variance treated storage companions as CLR alias parameters");
        var sourceArguments = new TypeNode[] { new TypeNode.Fqn("Key"), new TypeNode.Fqn("Value") };
        var physicalArguments = new TypeNode[] { new TypeNode.Fqn("KeyStorage"), sourceArguments[0],
            new TypeNode.Fqn("ValueStorage"), sourceArguments[1] };
        if (!HasApplicationArity(2, framedMap, mapFrame) || !HasApplicationArity(4, framedMap, mapFrame)
            || HasApplicationArity(3, framedMap, mapFrame)
            || !SourceArguments(sourceArguments, mapFrame).SequenceEqual(sourceArguments)
            || !SourceArguments(physicalArguments, mapFrame).SequenceEqual(sourceArguments))
            throw new InvalidOperationException("Alias variance confused source and materialized application frames");
        var reorderedIdentity = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 1, 0 });
        if (!SourceArguments(sourceArguments, reorderedIdentity).SequenceEqual(sourceArguments.Reverse()))
            throw new InvalidOperationException("An equal-arity alias frame lost its physical permutation");
        if (ProjectVariantArguments(new TypeNode[] { new TypeNode.Tv("type", 0) }, covariant)[0]
            is not TypeNode.Projection { Variance: "out", Of: TypeNode.Tv { I: 0 } })
            throw new InvalidOperationException("A variant readable owner argument became an exact CLR witness");
        var sourceIndex = ReferenceMetadataIndex.SourceCaptureTestIndex();
        var referenced = new TypeNode.Fqn("external.Frame", sourceArguments);
        NullableRepresentationFrame absentFrame = null;
        var absent = ResolveSourceVariances(referenced, sourceIndex, null, ref absentFrame);
        NullableRepresentationFrame emptyFrame = null;
        var empty = ResolveSourceVariances(referenced, sourceIndex, Array.Empty<string>(), ref emptyFrame);
        if (absent.Count != 3 || absentFrame == null || empty.Count != 0 || emptyFrame != null
            || !OrdinaryParameters(absent, absentFrame).SequenceEqual(new string[] { "out", null })
            || !HasSourceVariance(referenced, sourceIndex))
            throw new InvalidOperationException("Alias variance confused absent and empty source facts or lost the referenced physical frame");
        var rejectedFrame = false;
        try { OrdinaryParameters(covariant, mapFrame); }
        catch (InvalidOperationException) { rejectedFrame = true; }
        if (!rejectedFrame)
            throw new InvalidOperationException("Alias variance accepted a declaration outside its exact physical frame");
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var alias = new TypeNode.Fqn("external.Alias", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        const string list = "System.Collections.Generic.IList";
        if (!RequiresErasureFromSource(alias, references, covariant, list)
            || !RequiresErasure(alias, references, JsonNode.Parse("""[{"name":"T","variance":"out"}]""").AsArray(), list)
            || RequiresErasureFromSource(alias, references, invariant, list)
            || RequiresErasureFromSource(alias, references, Array.Empty<string>(), list)
            || RequiresErasureFromSource(alias, references, covariant, "System.Nullable")
            || RequiresErasureFromSource(alias, references, covariant, "System.String"))
            throw new InvalidOperationException("Alias variance source and metadata readers disagreed on native invariant/value/non-generic contracts");
        Console.WriteLine("[alias variance] self-test OK (value-type variance, invariant storage, declaration frames)");
    }
}
