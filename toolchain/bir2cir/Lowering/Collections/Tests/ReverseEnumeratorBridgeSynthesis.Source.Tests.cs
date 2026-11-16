using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReverseEnumeratorBridgeSynthesis
{
    internal static void SourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning reverse iterator: " + message); }
        var refs = ReferenceMetadataIndex.CreateIteratorProtocolSourceTestIndex();
        var carrier = KotlinIteratorPhysicalProtocol.Carrier(refs);
        var integer = new TypeNode.Fqn("System.Int32"); var text = new TypeNode.Fqn("System.String");
        var ownerT = new TypeNode.Tv("type", 0); var ownerU = new TypeNode.Tv("type", 1);
        BirFunctionDeclaration Iterator(string name, TypeNode semantic, bool abstractMethod = false) => new() {
            Name = name, DeclarationSourceName = IteratorMember, Static = false, Params = Array.Empty<BirParameter>(),
            Ret = BirGraphValue.CopyType(carrier), RetKotlinType = semantic, Vis = "public", Abstract = abstractMethod,
            Body = abstractMethod ? default : OptionalField<IBirStatement[]>.Of(Array.Empty<IBirStatement>()),
        };
        var cursor = new BirClassDefinition { Kind = "class", Name = "Cursor",
            TypeParams = new[] { new BirTypeParameter { Name = "Unrelated" }, new BirTypeParameter { Name = "Element" } },
            Interfaces = new TypeNode[] { Constructed(KotlinIterator, ownerU) }, Methods = Array.Empty<BirFunctionDeclaration>() };
        var empty = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { cursor } };
        var selectedIterator = Iterator("physicalIterator", new TypeNode.Fqn("Cursor", new TypeNode[] { ownerT, ownerU }));
        var occupied = new BirFunctionDeclaration { Name = GetEnumeratorName, Ret = integer, Params = Array.Empty<BirParameter>(),
            Body = Array.Empty<IBirStatement>(), Vis = "public" };
        var bag = new BirClassDefinition { Kind = "class", Name = "Bag", CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } },
            TypeParams = new[] { new BirTypeParameter { Name = "Own" } },
            Interfaces = new TypeNode[] { Constructed(IEnumerableT, ownerU) }, Methods = new[] { selectedIterator, occupied } };
        var defaultIterator = Iterator("defaultIterator", Constructed(KotlinIterator, ownerT));
        var defaultOwner = new BirInterfaceDefinition { Kind = "interface", Name = "IteratorDefault",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { defaultIterator } };
        var fromDefault = new BirClassDefinition { Kind = "class", Name = "DefaultBag",
            Interfaces = new TypeNode[] { Constructed("IteratorDefault", text), Constructed(IEnumerableT, text) },
            Methods = Array.Empty<BirFunctionDeclaration>() };
        var firstBridge = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { defaultOwner, bag } };
        var laterBridge = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { fromDefault } };
        var roots = new[] { empty, firstBridge, laterBridge };
        var originalMethods = bag.Methods.Value;
        var notifications = new List<string>();
        void Methods(BirFileDeclaration file, IBirTypeDeclaration owner, IReadOnlyList<BirFunctionDeclaration> appended)
        {
            Need(file.Types.Value.Any(type => ReferenceEquals(type, owner))
                && appended.Count == 2 && owner.Methods.Value.TakeLast(2).Zip(appended).All(pair => ReferenceEquals(pair.First, pair.Second))
                && !roots.SelectMany(root => root.Types.OrEmpty()).Any(type => type.Name.OrDefault() == AdapterName),
                "method publication preceded attachment or adapter birth preceded the complete method pass");
            notifications.Add(owner.Name.Value);
        }
        void Types(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> appended)
        {
            Need(ReferenceEquals(file, firstBridge) && appended.Count == 1 && ReferenceEquals(file.Types.Value.Last(), appended[0])
                && notifications.SequenceEqual(new[] { "Bag", "DefaultBag" }),
                "adapter was published before attachment or outside the first file actually owing a bridge");
            notifications.Add("adapter");
        }
        var emitted = ApplyAll(roots, refs, Methods, Types);
        Need(emitted.Count == 1 && ReferenceEquals(firstBridge.Types.Value.Last(), emitted[0])
            && empty.Types.Value.Length == 1 && laterBridge.Types.Value.Length == 1,
            "adapter publication/return did not report the actual sole attached type");
        Need(bag.Methods.Value.Take(2).Zip(originalMethods).All(pair => ReferenceEquals(pair.First, pair.Second))
            && ReferenceEquals(bag.Methods.Value[0], selectedIterator) && selectedIterator.RetKotlinType.Value is TypeNode.Fqn { Name: "Cursor" },
            "synthesis replaced original declarations or confused logical iterator ancestry with its carrier return");
        var generic = bag.Methods.Value[2]; var raw = bag.Methods.Value[3];
        Need(generic.Name.Value == AliasGetEnumeratorName && generic.Vis.Value == "private" && generic.Generated.Value
            && raw.Name.Value == NonGenericBridgeName && raw.Generated.Value
            && generic.ClrInterfaceImpls.Value.Single().Owner.Value == Constructed(IEnumerableT, ownerU),
            "nullary physical name collision or generic/non-generic MethodImpl append order changed");
        var construction = (BirNew)((BirReturn)generic.Body.Value.Single()).Value.Value;
        var iteratorCall = (BirCallInstance)construction.Args.Value.Single();
        Need(construction.LocalCtorIndex.Value == 0 && construction.Type.Value == Constructed(AdapterName, ownerU)
            && construction.ArgTypes.Value.Single() == carrier && iteratorCall.Method.Value == "physicalIterator"
            && iteratorCall.Ret.Value == carrier && iteratorCall.OwnerType.Value is TypeNode.Fqn { Args.Length: 2 },
            "iterator's unrelated first generic argument replaced its selected element or declared physical call return");
        var rawCall = (BirCallInstance)((BirReturn)raw.Body.Value.Single()).Value.Value;
        Need(rawCall.OwnerType.Value == new TypeNode.Fqn("Bag", new TypeNode[] { ownerT, ownerU })
            && rawCall.Method.Value == AliasGetEnumeratorName
            && ((BirCallInstance)((BirNew)((BirReturn)fromDefault.Methods.Value[0].Body.Value.Single()).Value.Value).Args.Value.Single())
                .OwnerType.Value == Constructed("IteratorDefault", text),
            "complete captured+own self frame or exact selected default-provider owner was lost");

        var adapter = emitted[0];
        Need(adapter.Interfaces.Value.SequenceEqual(new TypeNode[] { Constructed(IEnumeratorT, ownerT),
                new TypeNode.Fqn(IEnumerator), new TypeNode.Fqn(IDisposable) })
            && adapter.Fields.Value.Select(field => field.Name.Value).SequenceEqual(new[] { "_it", "_cur" })
            && adapter.Fields.Value[0].Type.Value == carrier && adapter.Fields.Value[0].InitOnly.Value
            && adapter.Ctors.Value.Single().BaseArgs.IsNull && adapter.Ctors.Value.Single().ThisArgs.IsNull
            && adapter.Methods.Value.Select(method => method.Name.Value).SequenceEqual(new[] {
                "MoveNext", "get_Current", NonGenericCurrentName, "Reset", "Dispose" })
            && !adapter.Methods.Value.Last().BodyTerminates.IsPresent && adapter.Methods.Value.Last().Body.Value.Length == 0,
            "adapter's native layout, construction ABI, descriptor order or empty Dispose semantics changed");
        var branch = ((BirIf)adapter.Methods.Value[0].Body.Value[0]).Branches.Value.Single();
        Need(branch.Cond.Value is BirCallInstance hasNext && hasNext.Method.Value == "physicalHasNext"
            && branch.Body.Value[0] is BirSetField { Value.HasValue: true } store && store.Value.Value is BirCast cast
            && cast.Type.Value == ownerT && cast.E.Value is BirCallInstance next && next.Method.Value == "physicalNext"
            && next.Ret.Value == new TypeNode.Fqn("System.Object") && !next.MemberRef.IsPresent,
            "iterator protocol bypassed its selected physical slot/result or bound MemberRef before the genuine binder");
        var independent = AdapterSource("IndependentAdapter", refs);
        Need(!ReferenceEquals(independent, adapter) && !ReferenceEquals(independent.Fields.Value[0], adapter.Fields.Value[0])
            && !roots.SelectMany(root => root.Types.OrEmpty()).Any(type => ReferenceEquals(type, independent)),
            "AdapterSource reused an attached declaration/template instead of returning fresh unattached output");

        var receiver = new BirCallStatic { Owner = new TypeNode.Fqn("Producer"), Method = "NextReceiver",
            Ret = carrier, Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>() };
        var projected = (BirCast)KotlinIteratorPhysicalProtocol.CallSource(refs, receiver, "next", text, text);
        Need(ReferenceEquals(((BirCallInstance)projected.E.Value).Recv.Value, receiver)
            && projected.Type.Value == text && !((BirCallInstance)projected.E.Value).MemberRef.IsPresent,
            "result adaptation cloned its actual receiver or guessed an early member reference");

        // The reservation and renderer share the same most-specific provider
        // authority: abstract redeclarations and unrelated defaults suppress it.
        var abstractOwner = new BirInterfaceDefinition { Kind = "interface", Name = "AbstractIterator",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Interfaces = new TypeNode[] { Constructed("IteratorDefault", ownerT) },
            Methods = new[] { Iterator("abstractIterator", Constructed(KotlinIterator, ownerT), true) } };
        var secondDefault = new BirInterfaceDefinition { Kind = "interface", Name = "SecondDefault",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { Iterator("otherIterator", Constructed(KotlinIterator, ownerT)) } };
        BirClassDefinition NoProvider(string name, params TypeNode[] interfaces) => new() {
            Kind = "class", Name = name, Methods = Array.Empty<BirFunctionDeclaration>(),
            Interfaces = interfaces.Append(Constructed(IEnumerableT, text)).ToArray(),
        };
        var abstractBag = NoProvider("AbstractBag", Constructed("AbstractIterator", text));
        var ambiguousBag = NoProvider("AmbiguousBag", Constructed("IteratorDefault", text), Constructed("SecondDefault", text));
        var refusedRoot = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { defaultOwner, abstractOwner, secondDefault, abstractBag, ambiguousBag } };
        var definitions = SupertypeGraph.CollectSource(new[] { refusedRoot });
        var declarations = refusedRoot.Types.Value.ToDictionary(type => type.Name.Value);
        Need(ReserveSlots(definitions["AbstractBag"], definitions, refs, declarations) == null
            && ReserveSlots(definitions["AmbiguousBag"], definitions, refs, declarations) == null,
            "abstract suppression or unrelated-default ambiguity diverged from early slot reservation");
        Console.WriteLine("[owning reverse enumerator] shared selection, frames, ordered actual births, protocol MOVE and native adapter: OK");
    }
}

sealed partial class ReferenceMetadataIndex
{
    // Test-only trusted metadata catalog, using the same validated constructor
    // shape as existing source ABI tests. No fake reflection MethodDef or runtime
    // testing mode: actual producer/ref coverage belongs to the grouped focus.
    internal static ReferenceMetadataIndex CreateIteratorProtocolSourceTestIndex()
    {
        var metadata = new ReferenceDotKtMetadata();
        const string semantic = "kotlin.collections.Iterator", physical = "test.IteratorPhysical";
        metadata.ExistentialPhysicalBySemanticOwner.Add(semantic, physical);
        metadata.DotKtOwners.UnionWith(new[] { semantic, physical });
        foreach (var (source, target, logical, result) in new[] {
            ("hasNext", "physicalHasNext", (TypeNode)new TypeNode.Fqn("System.Boolean"), (TypeNode)new TypeNode.Fqn("System.Boolean")),
            ("next", "physicalNext", (TypeNode)new TypeNode.Tv("type", 0), (TypeNode)new TypeNode.Fqn("System.Object")),
        })
        {
            var identity = "iterator-" + source;
            metadata.MemberBindings.Add(new MemberBinding(semantic, source, 0, null, true, false,
                ParamTypeNodes: Array.Empty<TypeNode>(), ReturnTypeNode: logical, DeclarationId: identity));
            metadata.MemberBindings.Add(new MemberBinding(physical, target, 0, null, true, false,
                ParamTypeNodes: Array.Empty<TypeNode>(), ReturnTypeNode: result) { SourceMethodDeclaration = identity });
        }
        return new ReferenceMetadataIndex(new List<ReferenceAssembly> {
            new("iterator-protocol-source-test", "iterator-protocol-source-test", "1", metadata),
        }, Build(new[] { typeof(object).Assembly.Location })._compileRefs);
    }
}
