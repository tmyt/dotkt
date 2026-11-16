using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionBclSlotSynthesis
{
    internal static void SourceSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed native collection slots: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var element = new TypeNode.Fqn("System.String");
        var list = new TypeNode.Fqn(IList, new TypeNode[] { element });
        var userCopy = new BirFunctionDeclaration { Name = "CopyTo", Static = false, Virtual = false,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("System.String") };
        var reservedCopy = new BirFunctionDeclaration { Name = "dotkt$native$CopyTo" };
        var suppliedContains = new BirFunctionDeclaration { Name = "Contains", Vis = "public", Virtual = true,
            Params = new[] { new BirParameter { Name = "value", Type = element } }, Ret = new TypeNode.Fqn("System.Boolean") };
        var original = new[] { userCopy, reservedCopy, suppliedContains };
        var owner = new BirClassDefinition { Name = "Tests.NativeCollectionSlots", Kind = "class", Interfaces = new TypeNode[] { list },
            Methods = original, DotktKotlinMutableCollectionContract = SourceTypeVector.Create(new TypeNode[] { list }) };
        var unused = new BirClassDefinition { Name = "Tests.UncapturedCollection", Kind = "class", Interfaces = new TypeNode[] { list } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner, unused } };
        var publications = new List<BirFunctionDeclaration>();
        ApplyAll(new[] { file }, refs, type => type, (actualFile, actualOwner, appended) => {
            Require(ReferenceEquals(actualFile, file) && ReferenceEquals(actualOwner, owner)
                && actualOwner.Methods.Value.Skip(original.Length).SequenceEqual(appended, ReferenceEqualityComparer.Instance),
                "publication must follow attachment and report precisely the actual new suffix");
            publications.AddRange(appended);
        });
        Require(owner.Methods.Value.Take(3).SequenceEqual(original, ReferenceEqualityComparer.Instance)
            && publications.Select(method => method.Name.Value).SequenceEqual(new[] { "get_IsReadOnly", "dotkt$native$CopyTo$", "IndexOf" })
            && !unused.Methods.IsPresent && !owner.DotktKotlinMutableCollectionContract.IsPresent,
            "exact public slot suppression, collision order or captured-only eligibility changed");
        var copy = publications[1];
        var descriptor = copy.ClrInterfaceImpls.Value.Single();
        Require(copy.Vis.Value == "private" && copy.ClrFinal.Value && copy.Virtual.Value && !copy.Static.Value
            && !copy.Override.Value && !copy.Abstract.Value && !copy.ObjectOverride.Value && !copy.DeclarationId.IsPresent
            && descriptor.Owner.Value is TypeNode.Fqn { Name: ICollection, Args: [TypeNode.Fqn { Name: "System.String" }] }
            && descriptor.Member.Value == "CopyTo" && descriptor.Arity.Value == 0
            && descriptor.Params.Value is [TypeNode.Array { Elem: TypeNode.Fqn { Name: "System.String" } }, TypeNode.Fqn { Name: "System.Int32" }]
            && descriptor.Ret.Value is TypeNode.Fqn { Name: "void" },
            "explicit native slot identity or exact physical signature changed");
        var query = (BirCallInstance)((BirReturn)publications[2].Body.Value.Single()).Value.Value;
        Require(query.OwnerType.Value is TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.KotlinListDefaultSlots" }
            && query.Method.Value == "dotktIndexOf" && query.Sig.Value is [TypeNode.Fqn { Name: "System.Object" }]
            && query.Args.Value is [BirCast { Type.HasValue: true, Type.Value: TypeNode.Fqn { Name: "System.Object" }, E.HasValue: true,
                E.Value: BirLocal { Name.HasValue: true, Name.Value: "element" } }]
            && query.Virtual.Value && query.Ret.Value is TypeNode.Fqn { Name: "System.Int32" } && !query.Sty.IsPresent,
            "query forwarding stopped using the already-selected semantic object slot");

        var openElement = new TypeNode.Fqn("Tests.Pair", new TypeNode[] { new TypeNode.Tv("type", 1) });
        var fresh = CopyToSource(openElement);
        var second = CopyToSource(openElement);
        Require(!ReferenceEquals(fresh, second) && !ReferenceEquals(fresh.Params.Value, second.Params.Value)
            && fresh.Params.Value[0].Type.Value is TypeNode.Array { Elem: TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 1 }] } }
            && fresh.Body.Value is [BirVariable, BirVariable, BirTry],
            "native copy renderer did not author independent exact-frame declarations");
        openElement.Args[0] = new TypeNode.Fqn("System.Object");
        var attempt = (BirTry)fresh.Body.Value[2];
        var loop = (BirWhile)attempt.Body.Value.Single();
        var store = (BirArraySet)((BirExpressionStatement)loop.Body.Value[0]).Expr.Value;
        var current = (BirCallInstance)store.Value.Value;
        var cleanup = (BirCallInstance)((BirExpressionStatement)attempt.Finally.Value.Single()).Expr.Value;
        Require(store.Elem.Value is TypeNode.Fqn { Args: [TypeNode.Tv { I: 1 }] }
            && current.OwnerType.Value is TypeNode.Fqn { Name: "System.Collections.Generic.IEnumerator", Args: [TypeNode.Fqn { Args: [TypeNode.Tv { I: 1 }] }] }
            && current.Method.Value == "get_Current" && current.Sig.HasValue && current.Sig.Value.Length == 0
            && current.Args.HasValue && current.Args.Value.Length == 0
            && cleanup.OwnerType.Value is TypeNode.Fqn { Name: "System.IDisposable" } && cleanup.Method.Value == "Dispose"
            && attempt.Catches.HasValue && attempt.Catches.Value.Length == 0
            && loop.Cond.Value is BirCallInstance { Method.HasValue: true, Method.Value: "MoveNext" },
            "CopyTo lost native enumeration, snapshot isolation or finally-disposal around writes");
        Console.WriteLine("[source native collection slots] self-test OK (captured contracts, exact slots, publication, collisions and native disposal)");
    }
}

static partial class StarProjectionCountLowering
{
    internal static void SourceSelfTest()
    {
        static BirPropertyGet Count(TypeNode type) => new() { Name = "Count", Type = type,
            Recv = OptionalField<IBirExpression>.Of(new BirCast { Type = type, E = new BirLocal { Name = "source" } }) };
        var raw = new[] { "System.Collections.ICollection", "System.Collections.IList", "System.Collections.IDictionary" }
            .Select(name => Count(new TypeNode.Fqn(name))).ToArray();
        var enumerable = Count(new TypeNode.Fqn("System.Collections.IEnumerable"));
        var emptyArguments = Count(new TypeNode.Fqn("System.Collections.IList", Array.Empty<TypeNode>()));
        var generic = Count(new TypeNode.Fqn("System.Collections.Generic.ICollection", new TypeNode[] { new TypeNode.Fqn("System.Object") }));
        var missingType = new BirCast { E = new BirLocal { Name = "untyped" } };
        var untyped = new BirPropertyGet { Name = "Count", Recv = OptionalField<IBirExpression>.Of(missingType) };
        var nodes = raw.Concat(new[] { enumerable, emptyArguments, generic, untyped }).ToArray();
        var receivers = nodes.Select(node => node.Recv.Value).ToArray();
        var file = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Body = nodes.Select(node =>
            (IBirStatement)new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(node) }).ToArray() } } };
        Apply(file);
        if (!raw.All(node => node.Type.Value is TypeNode.Fqn { Name: "System.Collections.ICollection", Args: null }
                && ((BirCast)node.Recv.Value).Type.Value is TypeNode.Fqn { Name: "System.Collections.ICollection", Args: null })
            || !nodes.Select(node => node.Recv.Value).SequenceEqual(receivers, ReferenceEqualityComparer.Instance)
            || enumerable.Type.Value is not TypeNode.Fqn { Name: "System.Collections.IEnumerable" }
            || emptyArguments.Type.Value is not TypeNode.Fqn { Args.Length: 0 }
            || generic.Type.Value is not TypeNode.Fqn { Name: "System.Collections.Generic.ICollection" }
            || missingType.Type.IsPresent || untyped.Type.IsPresent)
            throw new InvalidOperationException("Typed raw Count lost exact raw classifier/presence or actual receiver identity");
        Console.WriteLine("[source raw collection count] self-test OK (exact raw faces and actual receiver identity)");
    }
}

static partial class ReadOnlyCollectionViewInterfaces
{
    internal static void SourceSelfTest()
    {
        static TypeNode.Fqn Face(string name, string element) => new(name, new TypeNode[] { new TypeNode.Fqn(element) });
        var list = Face(CollectionViewFaces.IList, "System.String");
        var collection = Face(CollectionViewFaces.ICollection, "System.Int32");
        var set = Face(CollectionViewFaces.ISet, "System.Int32");
        var unrelated = new TypeNode.Fqn("Tests.Unrelated");
        var cls = new BirClassDefinition { Kind = "class", Interfaces = new TypeNode[] { unrelated, list, collection, set } };
        var existing = Face(CollectionViewFaces.IReadOnlyList, "System.String");
        var iface = new BirInterfaceDefinition { Kind = "interface", Interfaces = new TypeNode[] { list, existing } };
        var absent = new BirClassDefinition { Kind = "class" };
        Apply(new BirFileDeclaration { Types = new IBirTypeDeclaration[] { cls, iface, absent } });
        if (cls.Interfaces.Value.Length != 6 || !ReferenceEquals(cls.Interfaces.Value[0], unrelated)
            || !ReferenceEquals(cls.Interfaces.Value[1], list) || !ReferenceEquals(cls.Interfaces.Value[2], collection)
            || !ReferenceEquals(cls.Interfaces.Value[3], set)
            || !cls.Interfaces.Value[4].Equals(Face(CollectionViewFaces.IReadOnlyList, "System.String"))
            || !cls.Interfaces.Value[5].Equals(Face(CollectionViewFaces.IReadOnlyCollection, "System.Int32"))
            || iface.Interfaces.Value.Length != 2 || !ReferenceEquals(iface.Interfaces.Value[1], existing)
            || absent.Interfaces.IsPresent || cls.Methods.IsPresent || iface.Methods.IsPresent)
            throw new InvalidOperationException("Typed read-only siblings lost exact original order, interface coverage or first-sibling dedup");
        Console.WriteLine("[source read-only collection faces] self-test OK (class/interface order and first-sibling dedup)");
    }
}
