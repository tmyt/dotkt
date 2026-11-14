using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MemberCallSubstitution
{
    internal static void FactoriesSourceSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed member factories: " + message); }
        var integer = new TypeNode.Fqn("kotlin.Int");
        var single = new TypeNode.Fqn("kotlin.Float");
        var text = new TypeNode.Fqn("kotlin.String");
        var any = new TypeNode.Fqn("object");
        var metadata = new ReferenceDotKtMetadata();
        metadata.Aliases["kotlin.collections.HashSet"] = "System.Collections.Generic.HashSet";
        metadata.TypeArity["kotlin.collections.HashSet"] = 1;
        metadata.Aliases["kotlin.text.StringBuilder"] = "System.Text.StringBuilder";
        metadata.MemberBindings.Add(new MemberBinding("Producer", "selectedList", 1, null, false, true,
            ParamTypeNodes: new TypeNode[] { new TypeNode.Array(text) }, DeclarationId: "referenced-list",
            CollectionFactoryKind: "list") { VarargPositions = new[] { 0 } });
        var refs = ReferenceMetadataIndex.DefaultDemandTestIndex(metadata);
        var previousFactories = _localFactories;
        var previousOracle = _isValue;
        try
        {
            _isValue = type => type.Name is "kotlin.Int" or "kotlin.Float";
            var permuted = new NullableRepresentationFrame(1, new[] { 0 }, new[] { 1, 0 });
            _localFactories = new Dictionary<string, LocalFactory> {
                ["list"] = new("list", null, null, new[] { 0 }, null),
                ["permuted-list"] = new("list", null, null, new[] { 0 }, permuted),
                ["set"] = new("set", null, null, new[] { 0 }, null),
                ["single-array-element"] = new("list", null, null, Array.Empty<int>(), null),
                ["array"] = new(null, "vararg", null, new[] { 0 }, null),
                ["primitive-array"] = new(null, "vararg", "kotlin.Int", new[] { 0 }, null),
                ["sized"] = new(null, "sized", null, Array.Empty<int>(), null),
            };
            var context = new CollectionHelperBinding.CollectionSourceContext();
            BirCallStatic Effect(string name, TypeNode type) => new() {
                Method = name, Ret = type, Args = Array.Empty<IBirExpression>(),
            };
            BirConstant Number(int value) => new() { Type = integer, Value = new BirLiteral.IntegerLiteral(value) };
            BirCallStatic Factory(string id, IBirExpression[] args, TypeNode[] typeArgs = null) => new() {
                Method = "sameName", DeclarationId = id, Args = args,
                TypeArgs = typeArgs == null ? default : OptionalField<TypeNode[]>.Of(typeArgs),
            };

            var capacity = Effect("capacity", integer);
            var loadFactor = Effect("loadFactor", single);
            var constructor = new BirNew { Type = new TypeNode.Fqn("kotlin.collections.HashSet", new TypeNode[] { integer }),
                ArgTypes = new TypeNode[] { integer, single }, Args = new IBirExpression[] { capacity, loadFactor } };
            var mapped = (BirValueBlock)TransformNew(constructor, refs);
            Require(mapped.Stmts.Value.Length == 2
                && mapped.Stmts.Value.Cast<BirVariable>().Select(variable => ((BirCallStatic)variable.Init.Value).Method.Value)
                    .SequenceEqual(new[] { "capacity", "loadFactor" })
                && mapped.Result.Value is BirNewClr physical && physical.Args.Value.Single() is BirLocal local
                && local.Name.Value == ((BirVariable)mapped.Stmts.Value[0]).Name.Value
                && physical.ArgTypes.Value.Single() == integer
                && constructor.Args.Value.Length == 2 && ReferenceEquals(constructor.Args.Value[0], capacity),
                "mapped load-factor effects were dropped/reordered or constructor signature changed");
            AssertNoPlanVocabulary(mapped);
            constructor.Args = new IBirExpression[] { Number(16), new BirConstant { Type = single, Value = new Float32Literal(BitConverter.SingleToInt32Bits(0.75f)) } };
            Require(TransformNew(constructor, refs) is BirNewClr { Args.Value.Length: 1 },
                "droppable load-factor literal introduced a dead evaluation block");
            var stringBuilder = new BirNew { Type = new TypeNode.Fqn("kotlin.text.StringBuilder"),
                ArgTypes = new TypeNode[] { new TypeNode.Fqn("kotlin.CharSequence") }, Args = new IBirExpression[] { Effect("content", text) } };
            Require(TransformNew(stringBuilder, refs) is BirNewClr ctor
                && ctor.ArgTypes.Value.Single() == text && ctor.Args.Value.Single() is BirCallStatic {
                    Owner.Value: TypeNode.Fqn { Name: "kotlin.LibraryKt" }, Method.Value: "toString", Sig.Value.Length: 1 },
                "alias constructor did not preserve its declared CharSequence-to-String conversion");
            var array = new BirLocal { Name = "storage", Sty = new TypeNode.Array(integer) };
            Require(TransformStorageField(new BirFieldGet { Name = "storage", OwnerType = new TypeNode.Fqn("kotlin.UIntArray"), Recv = array })
                    is BirCast { Type.Value: TypeNode.Array { Elem: TypeNode.Fqn { Name: "kotlin.Int" } } }
                && TransformNew(new BirNew { Type = new TypeNode.Fqn("kotlin.UIntArray"), Args = new IBirExpression[] { array } }, refs)
                    is BirCast { Type.Value: TypeNode.Array { Elem: TypeNode.Fqn { Name: "kotlin.UInt" } } }
                && TransformStaticField(new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("kotlin.String"), Name = "INSTANCE" }, refs)
                    is BirConstant { Value.Value: BirLiteral.NullLiteral }, "unsigned storage/wrap or erased companion value changed");

            var pack = new BirNewArray { Elem = text, Elems = new IBirExpression[] { Effect("first", text), Effect("second", text) } };
            var source = Factory("list", new IBirExpression[] { pack }, new TypeNode[] { text });
            source.Sty = new TypeNode.Fqn("kotlin.collections.MutableList", new TypeNode[] { text });
            var literal = (BirNewList)TryFactorySubst(source, refs, context);
            Require(literal.Elems.Value.Length == 2 && TypeNodeDataEquality.Equal(literal.Sty.Value, source.Sty.Value)
                && !ReferenceEquals(literal.Elems.Value[0], pack.Elems.Value[0]), "collection literal lost result surface or independent element ownership");
            Require(TryFactorySubst(Factory("unknown", source.Args.Value, source.TypeArgs.Value), refs, context) == null
                && TryFactorySubst(Factory("list", new IBirExpression[] { array }, new TypeNode[] { integer }), refs, context) == null,
                "factory selection used a name or decomposed a forwarded vararg");
            var singleArray = (BirNewList)TryFactorySubst(Factory("single-array-element", new IBirExpression[] { pack }, new TypeNode[] { new TypeNode.Array(text) }), refs, context);
            Require(singleArray.Elems.Value.Single() is BirNewArray, "non-vararg array was decomposed into literal elements");
            var frameArguments = new TypeNode[permuted.PhysicalArity];
            frameArguments[permuted.SourcePosition(0)] = text;
            frameArguments[permuted.NullableVariable(new TypeNode.Tv("method", 0)).I] = any;
            Require(TryFactorySubst(Factory("permuted-list", new IBirExpression[] { pack }, frameArguments), refs, context)
                is BirNewList { Elem.Value: TypeNode.Fqn { Name: "kotlin.String" } }, "factory selected a frame companion instead of the ordinary type argument");
            var referenced = Factory("referenced-list", new IBirExpression[] { pack }, new TypeNode[] { text });
            referenced.DeclarationReferencedFactory = true;
            Require(TryFactorySubst(referenced, refs, context) is BirNewList { Elems.Value.Length: 2 }, "exact referenced factory metadata was not consumed");

            var set = (BirValueBlock)TryFactorySubst(Factory("set", new IBirExpression[] { pack }, new TypeNode[] { text }), refs, context);
            Require(set.Stmts.Value.Take(2).Cast<BirVariable>().Select(variable => ((BirCallStatic)variable.Init.Value).Method.Value)
                .SequenceEqual(new[] { "first", "second" })
                && ((BirVariable)set.Stmts.Value[2]).Init.Value is BirNewClr { KotlinHashStorageSignature.Value: true }
                && set.Stmts.Value.Skip(3).Cast<BirExpressionStatement>().All(statement =>
                    statement.Expr.Value is BirNativeInstance { KotlinHashStorageSignature.Value: true }),
                "hash storage allocation or comparer callbacks overtook literal element evaluation");
            var sized = Factory("sized", new IBirExpression[] { Number(3) }, new TypeNode[] { integer });
            sized.Ret = new TypeNode.Array(new TypeNode.Nullable(integer));
            Require(TryFactorySubst(sized, refs, context) is BirNewArraySized { Elem.Value: TypeNode.Fqn { Name: "object" } },
                "sized array ignored the selected nullable result element");
            var primitive = (BirNewArray)TryFactorySubst(Factory("primitive-array", new IBirExpression[] {
                new BirNewArray { Elem = integer, Elems = Array.Empty<IBirExpression>() } }), refs, context);
            Require(primitive.Elem.Value == integer && primitive.Elems.Value.Length == 0, "empty primitive vararg lost its element source");
            context.Variables["integers"] = new TypeNode.Array(integer);
            var forwarded = Factory("array", new IBirExpression[] { new BirLocal { Name = "integers" } }, new TypeNode[] { integer });
            forwarded.Sty = new TypeNode.Array(any);
            var spread = (BirSpreadConcat)TryFactorySubst(forwarded, refs, context);
            Require(spread.Elem.Value == any && spread.Parts.Value.Single().E.Value is BirGenericStatic adapter
                && adapter.TypeArgs.Value.Single() == any && adapter.ResolvedMemberParams.Value.Single() == new TypeNode.Fqn("System.Collections.IEnumerable"),
                "widened array spread lost its fresh-copy/non-generic boxed enumeration adapter");
            var mixed = new BirSpreadConcat { Elem = any, Parts = new[] {
                new BirSpreadPart { E = Number(1) },
                new BirSpreadPart { Spread = true, E = new BirLocal { Name = "integers" } },
            } };
            TransformSpread(mixed, context);
            Require(mixed.Parts.Value[0].E.Value is BirCast { Type.Value: TypeNode.Fqn { Name: "object" } }
                && mixed.Parts.Value[1].E.Value is BirGenericStatic, "mixed spread omitted scalar boxing or array element adaptation");
            Require(CompilerMetadataIntrinsicValue("dotkt.equalityStorage.Set", refs) is BirNewArray {
                Elem.Value: TypeNode.Fqn { Name: "System.Type" } } && CompilerMetadataIntrinsicValue("ordinary", refs) == null,
                "compiler metadata intrinsic did not use the authoritative declaration table");
        }
        finally { _localFactories = previousFactories; _isValue = previousOracle; refs.DisposeNet(); }
        Console.WriteLine("[typed member factories] self-test OK (selected IDs, frames, mapped evaluation, storage and spread)");
    }
}
