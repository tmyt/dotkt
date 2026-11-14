using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ReferenceMetadataIndex
{
    internal static void PhysicalParameterFactsSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed shipped parameter facts: " + message); }
        var index = Build(new[] { typeof(object).Assembly.Location });
        var enumerable = index.PhysicalOwnerTypeParametersInPhysicalFrame("System.Collections.Generic.IEnumerable`1");
        // Current CLR IEnumerable<T> is covariant AND permits byref-like type
        // arguments. Its absence of type bounds does not mean no special flags.
        var enumerableAttributes = typeof(IEnumerable<>).GetGenericArguments().Single().GenericParameterAttributes;
        var enumerableSpecial = (enumerableAttributes & GenericParameterAttributes.AllowByRefLike) != 0
            ? new[] { "allowsRefStruct" } : Array.Empty<string>();
        Require(enumerable is [var item] && item.Variance.Value == "out" && !item.Constraints.IsPresent
            && item.SpecialConstraints.IsPresent == (enumerableSpecial.Length != 0)
            && item.SpecialConstraints.OrEmpty().SequenceEqual(enumerableSpecial),
            "physical variance or actual CLR-only flag presence changed");
        Require(index.PhysicalOwnerTypeParametersInPhysicalFrame(null) == null
            && index.PhysicalOwnerTypeParametersInPhysicalFrame("Missing.Reference.Owner") == null
            && index.PhysicalOwnerTypeParametersInPhysicalFrame("System.String") is { Length: 0 }
            && index.PhysicalMethodTypeParametersInPhysicalFrame(null) == null
            && index.PhysicalMethodTypeParametersInPhysicalFrame("missing-declaration") == null,
            "missing selection was confused with an existing nongeneric physical declaration");

        // The canonical source leaf and correspondence are separate metadata
        // authorities. Physical rows must retain the complete shipped frame,
        // not select only the logical source parameter or its metadata names.
        const string logicalOwner = "Source.Pair";
        var frame = new NullableRepresentationFrame(1, new[] { 0 });
        var sourceRows = new JsonArray(new BirTypeParameter { Name = "Source", Variance = "out" }.ToDom(),
            new BirTypeParameter { Name = "Nullable" }.ToDom());
        index._dotKtOwners.Add(logicalOwner);
        index._physicalTypeBySemanticName[logicalOwner] = "System.Collections.Generic.Dictionary`2";
        index._ownerArity[logicalOwner] = frame.PhysicalArity;
        index._ownerTypeParamDeclarations[logicalOwner] = sourceRows.ToJsonString();
        index._ownerNullableFrames[logicalOwner] = frame;
        var source = index.OwnerTypeParametersInPhysicalFrame(logicalOwner);
        var physical = index.PhysicalOwnerTypeParametersInPhysicalFrame(logicalOwner);
        var reflectedOwner = typeof(Dictionary<,>).GetGenericArguments();
        Require(source.Count == 2 && source[0].Name.Value == "Source" && source[0].Variance.Value == "out"
            && physical.Length == frame.PhysicalArity && physical.Length != frame.SourceArity
            && physical.Select(parameter => parameter.Name.Value).SequenceEqual(reflectedOwner.Select(parameter => parameter.Name))
            && physical.All(parameter => !parameter.Variance.IsPresent)
            && ReferenceEquals(index.NullableTypeFrames[logicalOwner], frame),
            "source rows or nullable correspondence replaced/rewrote physical GenericParam rows");
        physical[0].Name = "mutated";
        var repeated = index.PhysicalOwnerTypeParametersInPhysicalFrame(logicalOwner);
        Require(repeated[0].Name.Value == reflectedOwner[0].Name && !ReferenceEquals(repeated[0], physical[0])
            && source[0].Name.Value == "Source", "physical owner queries share mutable records or source facts");
        index._dotKtOwners.Add("System.Collections.Generic.List");
        Require(index.PhysicalOwnerTypeParametersInPhysicalFrame("System.Collections.Generic.List`1") == null,
            "an unadmitted Kotlin implementation acquired reflected proof rows");
        index.SetLocalEmittedTypes(new HashSet<string> { logicalOwner });
        Require(index.PhysicalOwnerTypeParametersInPhysicalFrame(logicalOwner) == null,
            "a previously queried reference owner overrode a current local declaration");

        // Framework MethodDefs do not carry Kotlin IDs. Associate an actual
        // parsed reference MemberBinding with an authored identity, keeping its
        // exact token/physical owner, but a distinct source metadata surface.
        var reflectedMethod = typeof(MemoryExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "BinarySearch" && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 2 && method.GetParameters() is [var span, var comparable]
                && span.ParameterType.IsGenericType && span.ParameterType.GetGenericTypeDefinition() == typeof(Span<>)
                && comparable.ParameterType.IsGenericParameter);
        var native = index._membersByOwner["System.MemoryExtensions"].Single(binding => binding.MetadataToken == reflectedMethod.MetadataToken);
        const string identity = "physical-parameter-test";
        index._declarationById[identity] = native with {
            Owner = "Source.Search", DeclarationPhysicalOwner = "System.MemoryExtensions", DeclarationId = identity,
            MethodTypeParams = sourceRows, SemanticMethodTypeParams = sourceRows, NullableFrame = frame,
        };
        var methodRows = index.PhysicalMethodTypeParametersInPhysicalFrame(identity);
        Require(methodRows.Length == 2
            && methodRows.Select(parameter => parameter.Name.Value).SequenceEqual(reflectedMethod.GetGenericArguments().Select(parameter => parameter.Name))
            && methodRows[1].Constraints.Value.Any(type => type is TypeNode.Fqn {
                Name: "System.IComparable", Args: [TypeNode.Tv { Scope: "method", I: 0 }],
            }) && ReferenceEquals(index.NullableMethodFrame(identity), frame)
            && ReferenceEquals(index._declarationById[identity].SemanticMethodTypeParams, sourceRows),
            "exact physical MethodDef rows were replaced by the source surface or caller frame");
        for (var position = 0; position < methodRows.Length; position++)
            Require(methodRows[position].SpecialConstraints.OrEmpty().Contains("allowsRefStruct")
                == ((reflectedMethod.GetGenericArguments()[position].GenericParameterAttributes & GenericParameterAttributes.AllowByRefLike) != 0),
                "shipped method lost an actual CLR-only constraint flag");
        methodRows[1].Constraints.Value[0] = new TypeNode.Fqn("object");
        var freshMethod = index.PhysicalMethodTypeParametersInPhysicalFrame(identity);
        Require(!ReferenceEquals(freshMethod[1], methodRows[1])
            && freshMethod[1].Constraints.Value.Any(type => type is TypeNode.Fqn { Name: "System.IComparable" }),
            "shipped MethodDef facts exposed shared mutable constraint arrays");
        var plain = typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(object), typeof(object) });
        var plainBinding = index._membersByOwner["System.String"].Single(binding => binding.MetadataToken == plain.MetadataToken);
        index._declarationById["physical-nongeneric-test"] = plainBinding;
        Require(index.PhysicalMethodTypeParametersInPhysicalFrame("physical-nongeneric-test") is { Length: 0 },
            "an existing nongeneric physical MethodDef was treated as a missing identity");
        Console.WriteLine("[typed shipped parameter facts] self-test OK (physical/source separation, exact MethodDef, frames, presence, independence)");
    }
}
