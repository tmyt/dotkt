using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class LocalFunctionLowering
{
    internal static void SourceSelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        BirExpressionStatement Expr(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        BirConstant Number(int value) => new() { Type = integer, Value = new BirLiteral.IntegerLiteral(value) };
        BirCallLocal Call(string id, params IBirExpression[] args) => new() {
            Id = id, Args = args, Sig = args.Select(_ => (TypeNode)integer).ToArray(), Ret = integer,
        };
        BirFunctionDeclaration Function(string name, params IBirStatement[] body) => new() {
            SourceName = name, Static = true, Params = Array.Empty<BirParameter>(), Ret = integer, Body = body,
        };

        var parameter = new BirParameter { Name = "state", Type = integer, SharedCellType = new TypeNode.Fqn("Cell") };
        var borrowed = Function("borrowed", new BirSetLocal { Name = "state", Value = OptionalField<IBirExpression>.Of(Number(3)) },
            new BirReturn { Value = OptionalField<IBirExpression>.Of(new BirLocal { Name = "state", Sty = integer }) });
        borrowed.Params = new[] { parameter };
        var recursive = Function("recursive", Expr(Call("recursive")), new BirReturn { Value = OptionalField<IBirExpression>.Of(Number(0)) });
        var callerVariable = new BirVariable { Name = "caller", Type = integer, Init = OptionalField<IBirExpression>.Of(Number(1)) };
        var direct = Call("borrowed", new BirLocal { Name = "caller", Sty = integer });
        var reference = new BirLocalFunctionReference { Id = "recursive", FuncType = new TypeNode.Fn(false, integer, Array.Empty<TypeNode>()) };
        var caller = Function("caller", callerVariable,
            new BirLocalFunction { Id = "borrowed", Decl = borrowed }, new BirLocalFunction { Id = "recursive", Decl = recursive }, Expr(direct), Expr(reference));
        caller.Name = "caller";
        caller.Ret = unit;
        var file = new BirFileDeclaration { FileClass = "LocalFile", Methods = new[] { caller } };
        var projected = new List<BirFunctionDeclaration>();
        Apply(file, refs, (method, frame) => {
            if (frame != null) throw new InvalidOperationException("Facade local acquired a type-owner frame");
            projected.Add(method);
        });
        var lowered = (BirCallStatic)((BirExpressionStatement)caller.Body.Value[1]).Expr.Value;
        var delegateValue = (BirNewDelegate)((BirExpressionStatement)caller.Body.Value[2]).Expr.Value;
        var recursiveUse = (BirCallStatic)((BirExpressionStatement)recursive.Body.Value[0]).Expr.Value;
        if (!ReferenceEquals(file.Methods.Value[0], caller) || !ReferenceEquals(file.Methods.Value[1], borrowed)
            || !ReferenceEquals(file.Methods.Value[2], recursive) || !ReferenceEquals(borrowed.Params.Value[0], parameter)
            || !ReferenceEquals(caller.Body.Value[0], callerVariable) || caller.Body.Value.Length != 3
            || !projected.SequenceEqual(new[] { borrowed, recursive })
            || borrowed.Name.Value != "dotkt$local0_borrowed" || recursive.Name.Value != "dotkt$local1_recursive"
            || borrowed.SourceName.IsPresent || borrowed.DeclarationSourceName.Value != "borrowed"
            || parameter.Type.Value is not TypeNode.ByRef { Of: TypeNode.Fqn { Name: "kotlin.Int" } }
            || ((BirExpressionStatement)borrowed.Body.Value[0]).Expr.Value is not BirByRefStore
            || ((BirReturn)borrowed.Body.Value[1]).Value.Value is not BirByRefLoad
            || lowered.Sig.Value[0] is not TypeNode.ByRef || lowered.Args.Value[0] is not BirLocal { Name.Value: "caller" }
            || lowered.DeclarationId.Value != borrowed.DeclarationId.Value || recursiveUse.DeclarationId.Value != recursive.DeclarationId.Value
            || delegateValue.DeclarationId.Value != recursive.DeclarationId.Value
            || lowered.CalleeOwner.Value != new TypeNode.Fqn("LocalFile"))
            throw new InvalidOperationException("Typed local-function hoisting lost continuing records, ID recursion, borrowed captures or delegate binding");

        // Owner companions and method variables remain different frames. The
        // actual scalar projection is called once on the moved declaration.
        var ownerFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
        var localFrame = new NullableRepresentationFrame(2, Array.Empty<int>(), storageIndices: new[] { 0 });
        var valueParameter = new BirParameter { Name = "value", Type = new TypeNode.Tv("method", 1),
            KotlinType = new TypeNode.Tv("method", 1), NullableGeneric = new TypeNode.Tv("method", 1) };
        var nested = Function("nested", new BirReturn { Value = OptionalField<IBirExpression>.Of(Number(7)) });
        nested.Mods = new BirModifiers { Suspend = true };
        nested.TypeParams = new[] { new BirTypeParameter { Name = "N" } };
        nested._syntheticTypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) };
        nested.Ret = new TypeNode.Tv("method", 0);
        nested.Params = new[] { new BirParameter { Name = "item", Type = new TypeNode.Tv("method", 0) } };
        nested.Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(new BirLocal { Name = "item" }) } };
        var descriptor = new BirFieldGet { Name = "field", MemberType = new TypeNode.Tv("method", 0),
            Sig = new TypeNode[] { new TypeNode.Tv("method", 0) } };
        var local = Function("generic", new BirLocalFunction { Id = "nested", Decl = nested }, Expr(descriptor),
            new BirReturn { Value = OptionalField<IBirExpression>.Of(new BirLocal { Name = "ownerValue" }) });
        local.Mods = new BirModifiers { Suspend = true };
        local.Params = new[] { valueParameter, new BirParameter { Name = "ownerValue", Type = new TypeNode.Tv("method", 0) } };
        local.TypeParams = new[] { new BirTypeParameter { Name = "O" },
            new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { new TypeNode.Tv("method", 0) } }, new BirTypeParameter { Name = "StorageO" } };
        local._syntheticTypeArgs = new TypeNode[] { new TypeNode.Tv("type", 0), new TypeNode.Tv("method", 7), new TypeNode.Tv("type", 1) };
        local.NullableRepresentationFrame = localFrame;
        local.Ret = new TypeNode.Tv("method", 0);
        local.NullableGenericRet = new TypeNode.Tv("method", 0);
        local.RetKotlinType = new TypeNode.Tv("method", 0);
        var use = Call("generic", new BirConstant { Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral("argument") },
            new BirLocal { Name = "owned", Sty = new TypeNode.Tv("type", 0) });
        use.TypeArgs = new TypeNode[] { new TypeNode.Tv("type", 0), new TypeNode.Fqn("kotlin.String"), new TypeNode.Tv("type", 1) };
        var ownerMethod = Function("owner", new BirLocalFunction { Id = "generic", Decl = local }, Expr(use));
        ownerMethod.Name = "owner";
        ownerMethod.Params = new[] { new BirParameter { Name = "owned", Type = new TypeNode.Tv("type", 0) } };
        ownerMethod.Ret = unit;
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", TypeParams = new[] {
                new BirTypeParameter { Name = "O" }, new BirTypeParameter { Name = "StorageO" } },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame), Methods = new[] { ownerMethod } };
        var genericFile = new BirFileDeclaration { FileClass = "GenericFile", Methods = Array.Empty<BirFunctionDeclaration>(), Types = new IBirTypeDeclaration[] { owner } };
        var policy = GenericRepresentationPolicy.CreateSource(new[] { genericFile }, new Dictionary<string, string>(), refs);
        var projection = new KotlinGenericScalarSlots.Projection(new(), new(StringComparer.Ordinal), policy, new(StringComparer.Ordinal), refs);
        var seenFrames = new List<NullableRepresentationFrame>();
        Apply(genericFile, refs, (method, frame) => { seenFrames.Add(frame); projection.ProjectMethod(method, frame); });
        var genericUse = (BirCallStatic)((BirExpressionStatement)ownerMethod.Body.Value[0]).Expr.Value;
        if (!ReferenceEquals(owner.Methods.Value[0], ownerMethod) || !ReferenceEquals(owner.Methods.Value[1], nested)
            || !ReferenceEquals(owner.Methods.Value[2], local) || !ReferenceEquals(local.Params.Value[0], valueParameter)
            || seenFrames.Count != 2 || seenFrames.Any(frame => !ReferenceEquals(frame, ownerFrame))
            || local.LexicalOwnerTypeParamCount.Value != 2 || nested.LexicalOwnerTypeParamCount.Value != 2
            || local.TypeParams.Value.Length != 1 || local.TypeParams.Value[0].Name.Value != "M"
            || local.TypeParams.Value[0].Constraints.Value[0] != new TypeNode.Tv("type", 0)
            || local.NullableRepresentationFrame.Value.SourceArity != 1 || local._syntheticTypeArgs.IsPresent
            || nested.TypeParams.Value.Length != 1 || nested.Ret.Value != new TypeNode.Tv("method", 0)
            || local.Ret.Value != new TypeNode.Tv("type", 0) || local.RetKotlinType.Value != new TypeNode.Tv("type", 0)
            || valueParameter.Type.Value != new TypeNode.Fqn("object") || valueParameter.KotlinType.Value != new TypeNode.Tv("method", 0)
            || descriptor.MemberType.Value != new TypeNode.Tv("type", 0) || descriptor.Sig.Value[0] != new TypeNode.Tv("method", 0)
            || genericUse.TypeArgs.Value.Single() != new TypeNode.Fqn("kotlin.String")
            || genericUse.CalleeOwner.Value != new TypeNode.Fqn("Owner", new TypeNode[] { new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1) })
            || !projection.Parameters.ContainsKey(local.DeclarationId.Value))
            throw new InvalidOperationException("Typed local-function lowering lost scalar authority, nested frames or source/physical generic correspondence");

        // A three-level owner needs group reversal [C,B,A], not one rotation.
        BirClassDefinition Type(string name, int arity) => new() { Name = name, Kind = "class",
            TypeParams = Enumerable.Range(0, arity).Select(index => new BirTypeParameter { Name = "T" + index }).ToArray() };
        var outer = Type("Outer", 1);
        var middle = Type("Middle", 2); middle.Mods = new BirModifiers { Inner = true }; middle.SemanticOwner = "Outer"; middle.OuterTypeParamCount = 1;
        var leaf = Type("Leaf", 3); leaf.Mods = new BirModifiers { Inner = true }; leaf.SemanticOwner = "Middle"; leaf.OuterTypeParamCount = 2;
        var member = Function("member", new BirReturn { Value = OptionalField<IBirExpression>.Of(Number(1)) });
        member.TypeParams = Enumerable.Range(0, 3).Select(index => new BirTypeParameter { Name = "C" + index }).ToArray();
        member._syntheticTypeArgs = Enumerable.Range(0, 3).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        var memberCall = Call("member");
        memberCall.TypeArgs = new TypeNode[] { new TypeNode.Fqn("A"), new TypeNode.Fqn("B"), new TypeNode.Fqn("C") };
        var leafMethod = Function("leaf", new BirLocalFunction { Id = "member", Decl = member }, Expr(memberCall)); leafMethod.Name = "leaf";
        leafMethod.Ret = unit;
        leaf.Methods = new[] { leafMethod };
        var nestedFile = new BirFileDeclaration { FileClass = "NestedFile", Types = new IBirTypeDeclaration[] { outer, middle, leaf } };
        Apply(nestedFile, refs);
        var selected = (BirCallStatic)((BirExpressionStatement)leafMethod.Body.Value[0]).Expr.Value;
        if (selected.CalleeOwner.Value != new TypeNode.Fqn("Leaf", new TypeNode[] { new TypeNode.Fqn("C"), new TypeNode.Fqn("B"), new TypeNode.Fqn("A") })
            || selected.TypeArgs.IsPresent || member.TypeParams.IsPresent || !ReferenceEquals(leaf.Methods.Value[1], member))
            throw new InvalidOperationException("Typed local-function selection lost nested owner group order");
        Console.WriteLine("[source local functions] self-test OK (actual hoisting, recursion, byref locations, scalar authority, nested and source generic frames)");
    }
}
