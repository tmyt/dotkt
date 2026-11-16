using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinMapStorageSynthesis
{
    internal static void SourceSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed map storage: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var key = new TypeNode.Fqn("System.String");
        var value = new TypeNode.Fqn("System.Int32");
        BirClassDefinition Map(string name) => new() { Name = name, Kind = "class",
            Interfaces = new TypeNode[] { F(KotlinCollectionSlotSynthesis.MapSlots), F(Dictionary, key, value) },
            Methods = Array.Empty<BirFunctionDeclaration>() };
        var hidden = Map("Map.Current");
        var current = Map("Map.Current");
        var plain = new BirClassDefinition { Name = "Map.PlainDictionary", Kind = "class",
            Interfaces = new TypeNode[] { F(Dictionary, key, value) } };
        var first = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { hidden, plain } };
        var second = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { current } };
        var callbacks = 0;
        var names = ApplyAll(new[] { first, second }, refs, false, type => type, (_, _) => callbacks++);
        Check(names.Count == 0 && callbacks == 0 && first.Types.Value.Length == 2 && second.Types.Value.Length == 1,
            "application face admission manufactured a canonical definition or birth notice");
        Check(hidden.Interfaces.Value.Length == 2 && plain.Interfaces.Value.Length == 1
            && current.Interfaces.Value.Length == 3 && current.Interfaces.Value[2] is TypeNode.Fqn { Name: CarrierName, Args: [var k, var v] }
            && k == key && v == value, "current last-winner or exact Kotlin-map gate/constructed dictionary arguments changed");
        ApplyAll(new[] { first, second }, refs, false, type => type, (_, _) => callbacks++);
        Check(current.Interfaces.Value.Length == 3 && callbacks == 0, "an existing exact storage face was duplicated");
        var before = first.Types.Value;
        var reference = EmitReferenceSurface(new[] { first, second }, (file, appended) => {
            callbacks++;
            Check(ReferenceEquals(file, first) && appended.Count == 1
                && ReferenceEquals(file.Types.Value[^1], appended[0]) && file.Types.Value.Length == before.Length + 1,
                "reference carrier was published before attachment or to a later root");
        });
        var carrier = (BirInterfaceDefinition)first.Types.Value[^1];
        Check(reference == CarrierName && callbacks == 1 && carrier.Name.Value == CarrierName
            && carrier.Kind.Value == "interface" && carrier.Vis.Value == "public"
            && carrier.Base.IsNull && carrier.TypeParams.Value.Select(parameter => parameter.Name.Value).SequenceEqual(new[] { "K", "V" })
            && !carrier.CapturedTypeParams.IsPresent && carrier.Ctors.Value.Length == 0 && carrier.Fields.Value.Length == 0,
            "reference physical carrier identity, presence or generic frame changed");
        var methods = carrier.Methods.Value;
        Check(methods.Length == 38 && methods.All(method => method.Generated.Value && method.ClrInterfaceSlotBridge.Value
            && !method.Static.Value && method.Virtual.Value && !method.Abstract.Value && !method.Override.Value && method.Vis.Value == "private"
            && method.Body.Value is [BirThrow { Value: { HasValue: true } }]
            && ((BirNew)((BirThrow)method.Body.Value[0]).Value.Value).Type.Value is TypeNode.Fqn { Name: "System.NotSupportedException" }),
            "reference surface lost exact default slots or executable throwing stubs");
        Check(methods[0].ClrInterfaceImpls.Value[0].Member.Value == "get_Item"
            && methods[0].ClrInterfaceImpls.Value[0].Owner.Value is TypeNode.Fqn { Name: Dictionary }
            && methods[^1].ClrInterfaceImpls.Value[0].Owner.Value is TypeNode.Fqn { Name: "System.Collections.ICollection" }
            && methods[^1].ClrInterfaceImpls.Value[0].Member.Value == "CopyTo", "slot order or exact generic/raw owner changed");
        foreach (var method in methods)
        {
            var descriptor = method.ClrInterfaceImpls.Value.Single();
            Check(descriptor.Arity.Value == 0 && descriptor.Ret.Value == method.Ret.Value
                && descriptor.Params.Value.SequenceEqual(method.Params.Value.Select(parameter => parameter.Type.Value)),
                "descriptor does not carry the exact physical method signature");
        }
        var copy = methods.Single(method => method.ClrInterfaceImpls.Value[0].Member.Value == "CopyTo"
            && method.ClrInterfaceImpls.Value[0].Owner.Value is TypeNode.Fqn { Name: Collection });
        var parameterPair = (TypeNode.Fqn)((TypeNode.Array)copy.Params.Value[0].Type.Value).Elem;
        var descriptorPair = (TypeNode.Fqn)((TypeNode.Array)copy.ClrInterfaceImpls.Value[0].Params.Value[0]).Elem;
        Check(!ReferenceEquals(parameterPair.Args, descriptorPair.Args), "descriptor shares a mutable parameter type vector");
        parameterPair.Args[0] = new TypeNode.Fqn("System.String");
        Check(descriptorPair.Args[0] is TypeNode.Tv { Scope: "type", I: 0 }, "parameter mutation changed its selected declaration descriptor");
        Check(methods.Where(method => method.ClrInterfaceImpls.Value[0].Member.Value == "TryGetValue").All(method =>
                method.Params.Value[1].Type.Value is TypeNode.ByRef { Of: TypeNode.Tv { Scope: "type", I: 1 } })
            && methods.Count(method => method.ClrInterfaceImpls.Value[0].Member.Value == "TryGetValue") == 2,
            "generic dictionary/read-only out slot lost physical V frame");
        Check(ReferenceEquals(first.Types.Value[0], hidden) && ReferenceEquals(first.Types.Value[1], plain)
            && second.Types.Value.Length == 1, "reference publication disturbed existing owner occurrences");
        Console.WriteLine("[typed map storage] self-test OK (current physical faces, no app births, reference slots and attached publication)");
    }
}
