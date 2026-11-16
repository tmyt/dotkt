using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void TypedGenericParameterFactsSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed selected native parameter facts: " + message); }
        static void Refused(Action query)
        {
            var refused = false;
            try { query(); } catch (InvalidOperationException) { refused = true; }
            Require(refused, "selection inferred a MethodDef without the exact descriptor/staticness/arity");
        }
        var previous = _refs;
        try
        {
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var owner = new TypeNode.Fqn("System.Runtime.CompilerServices.Unsafe");
            var descriptor = new TypeNode[] { new TypeNode.Fqn("System.Object") };
            var reflected = typeof(System.Runtime.CompilerServices.Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "As" && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 1
                    && method.GetParameters() is [var parameter] && parameter.ParameterType == typeof(object));
            var selected = SelectedGenericProofDeclaration(owner, "As", 1, false, descriptor, refs);
            Require(selected.MetadataToken == reflected.MetadataToken && selected.Module.ModuleVersionId == reflected.Module.ModuleVersionId,
                "physical descriptor selected a different actual framework MethodDef");
            var call = new BirGenericStatic { Type = owner, Method = "As", ResolvedMemberParams = descriptor,
                TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) }, Args = Array.Empty<IBirExpression>() };
            var arguments = call.TypeArgs.Value;
            var values = call.Args.Value;
            var facts = SelectedMethodTypeParametersInPhysicalFrame(call, refs);
            Require(facts is [var parameterFacts] && parameterFacts.Name.Value == reflected.GetGenericArguments()[0].Name
                && parameterFacts.SpecialConstraints.Value.Contains("class") && !parameterFacts.Constraints.IsPresent
                && !parameterFacts.Variance.IsPresent && !parameterFacts.Reified.IsPresent && !parameterFacts.ErasedInnerConstraints.IsPresent
                && ReferenceEquals(call.ResolvedMemberParams.Value, descriptor) && ReferenceEquals(call.TypeArgs.Value, arguments)
                && ReferenceEquals(call.Args.Value, values) && !call.MemberRef.IsPresent && !call._resolvedMethodTypeParams.IsPresent,
                "native flags/presence changed or a read-only fact query mutated its call");
            facts[0].Name = "mutated"; facts[0].SpecialConstraints.Value[0] = "mutated";
            var independent = SelectedMethodTypeParametersInPhysicalFrame(call, refs);
            Require(independent[0].Name.Value == reflected.GetGenericArguments()[0].Name
                && independent[0].SpecialConstraints.Value.Contains("class")
                && !ReferenceEquals(independent[0], facts[0]) && !ReferenceEquals(independent[0].SpecialConstraints.Value, facts[0].SpecialConstraints.Value),
                "independent queries shared mutable declaration facts");

            var empty = new BirGenericStatic { Type = new TypeNode.Fqn("System.Array"), Method = "Empty",
                TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) }, ResolvedMemberParams = Array.Empty<TypeNode>() };
            var unconstrained = SelectedMethodTypeParametersInPhysicalFrame(empty, refs);
            Require(unconstrained.Length == 1 && !unconstrained[0].Constraints.IsPresent && !unconstrained[0].SpecialConstraints.IsPresent,
                "an explicit zero-parameter descriptor was confused with absence or invented unconstrained fields");
            Refused(() => SelectedMethodTypeParametersInPhysicalFrame(empty with { ResolvedMemberParams = default }, refs));
            Refused(() => SelectedMethodTypeParametersInPhysicalFrame(empty with { ResolvedMemberParams = OptionalField<TypeNode[]>.Null }, refs));
            Refused(() => SelectedMethodTypeParametersInPhysicalFrame(empty with { TypeArgs = OptionalField<TypeNode[]>.Null }, refs));
            Refused(() => SelectedMethodTypeParametersInPhysicalFrame(owner, "As", 1, true, descriptor, refs));

            // Static native constraints retain method-frame variables beneath
            // constructed interfaces; struct/new and allows-ref-struct are CLR
            // metadata obligations, not reconstructed Kotlin constraints.
            var search = typeof(MemoryExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "BinarySearch" && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 2 && method.GetParameters() is [var span, var comparable]
                    && span.ParameterType.IsGenericType && span.ParameterType.GetGenericTypeDefinition() == typeof(Span<>)
                    && comparable.ParameterType.IsGenericParameter);
            var searchSignature = search.GetParameters().Select(parameter => RefTypeOf(parameter.ParameterType)).ToArray();
            var searchFacts = SelectedMethodTypeParametersInPhysicalFrame(new TypeNode.Fqn("System.MemoryExtensions"),
                search.Name, 2, false, searchSignature, refs);
            Require(searchFacts.Length == 2 && searchFacts[1].Constraints.Value.Any(type => type is TypeNode.Fqn {
                    Name: "System.IComparable", Args: [TypeNode.Tv { Scope: "method", I: 0 }],
                }), "selected native constraints lost their recursive method declaration frame");
            for (var index = 0; index < searchFacts.Length; index++)
                Require(searchFacts[index].SpecialConstraints.OrEmpty().Contains("allowsRefStruct")
                    == ((search.GetGenericArguments()[index].GenericParameterAttributes & GenericParameterAttributes.AllowByRefLike) != 0),
                    "selected allows-ref-struct flag changed from the actual MethodDef");
            var read = typeof(System.Runtime.InteropServices.MemoryMarshal).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "Read" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1);
            var readFacts = SelectedMethodTypeParametersInPhysicalFrame(new TypeNode.Fqn("System.Runtime.InteropServices.MemoryMarshal"),
                read.Name, 1, false, read.GetParameters().Select(parameter => RefTypeOf(parameter.ParameterType)).ToArray(), refs);
            Require(readFacts.Length == 1 && readFacts[0].SpecialConstraints.Value.Contains("struct")
                && readFacts[0].SpecialConstraints.Value.Contains("new")
                && readFacts[0].Constraints.Value.Any(type => type is TypeNode.Fqn { Name: "System.ValueType" }),
                "physical struct/new/type constraints were weakened to a Kotlin-only bound");

            // A call descriptor is open in the selected owner's declaration
            // frame. Override descriptors instead carry explicit owner args.
            var ownerVariable = new TypeNode.Tv("type", 1);
            var constructedOwner = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { ownerVariable });
            TypeNode[] Convert(TypeNode ownerArgument) => new TypeNode[] {
                new TypeNode.Fqn("System.Converter`2", new[] { ownerArgument, new TypeNode.Tv("method", 0) }),
            };
            var instance = new BirGenericInstance { Type = constructedOwner, Method = "ConvertAll",
                TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 1) }, ResolvedMemberParams = Convert(new TypeNode.Tv("type", 0)) };
            var open = SelectedMethodTypeParametersInPhysicalFrame(instance, refs);
            var implemented = SelectedMethodTypeParametersInPhysicalFrame(constructedOwner, "ConvertAll", 1, true,
                Convert(ownerVariable), refs, new TypeNode[] { ownerVariable });
            Require(open.Length == 1 && implemented.Length == 1 && open[0].StructurallyEquals(implemented[0])
                && !open[0].Constraints.IsPresent && !open[0].SpecialConstraints.IsPresent
                && instance.ResolvedMemberParams.Value[0] is TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }, TypeNode.Tv { Scope: "method", I: 0 }] },
                "method facts or selected open descriptor were substituted into the caller's owner/method frame");
            Refused(() => SelectedMethodTypeParametersInPhysicalFrame(constructedOwner, "ConvertAll", 1, true,
                Convert(new TypeNode.Tv("type", 0)), refs, new TypeNode[] { ownerVariable }));
        }
        finally { _refs = previous; }
        Console.WriteLine("[typed native generic parameter facts] self-test OK (exact framework selection, physical constraints, frames, presence, independent ownership)");
    }
}
