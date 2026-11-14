using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ErasedElementValues
{
    static void SourceElementSelfTest()
    {
        static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Owning element values: " + reason); }
        var variable = new TypeNode.Tv("method", 0);
        BirLocal Local(string name) => new() { Name = name, Sty = variable };
        BirArrayGet Read() => new() { Array = new BirLocal { Name = "xs" },
            Index = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0) } };
        var read = Read();
        var value = new BirVariable { Name = "value", Type = variable, Init = read };
        var alias = new BirVariable { Name = "alias", Type = variable, Init = Local("value") };
        var cast = new BirCast { Type = variable, ReifiedTypeOperand = false, E = Local("alias"), Sty = variable };
        var bodyRead = Local("item");
        var loop = new BirForArray { Var = "item", Elem = variable, Array = new BirLocal { Name = "xs" },
            Body = new IBirStatement[] { new BirVariable { Name = "iteration", Type = variable, Init = bodyRead } } };
        var outside = new BirLocal { Name = "item", Sty = new TypeNode.Fqn("kotlin.String") };
        var addressed = new BirVariable { Name = "addressed", Type = variable, Init = Read() };
        var addressRead = Read() with { Elem = variable };
        var native = new BirCallStatic { Method = "native", Sig = new TypeNode[] {
                new TypeNode.ByRef(variable), new TypeNode.ByRef(variable) },
            Args = new IBirExpression[] { new BirByRefOf { Inner = Local("addressed") }, addressRead } };
        var innerVariable = new TypeNode.Tv("method", 2);
        var innerRead = new BirArrayGet { Array = new BirLocal { Name = "inner", Sty = new TypeNode.Array(innerVariable) },
            Index = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0) } };
        var suspension = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(),
            // This unbound donor body owns method TVs independently of the caller.
            TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" },
                new BirTypeParameter { Name = "C" } },
            Body = new IBirStatement[] { new BirVariable { Name = "own", Type = innerVariable, Init = innerRead } },
            CapValues = new IBirExpression[] { Local("alias") } };
        var objectRead = new BirLocal { Name = "ordinaryObject", Sty = ObjectType };
        var method = new BirFunctionDeclaration { Name = "consume", Ret = ObjectType,
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] {
                new TypeNode.Fqn("Contract"), new TypeNode.Fqn("Bound") } } },
            Params = new[] { new BirParameter { Name = "xs", Type = new TypeNode.Array(variable) } },
            Body = new IBirStatement[] { value, alias, addressed, new BirExpressionStatement { Expr = native },
                new BirVariable { Name = "item", Type = new TypeNode.Fqn("kotlin.String"),
                    Init = new BirConstant { Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral("outer") } },
                loop, new BirExpressionStatement { Expr = outside }, new BirExpressionStatement { Expr = cast },
                new BirVariable { Name = "ordinaryObject", Type = ObjectType, Init = Local("alias") },
                new BirExpressionStatement { Expr = objectRead },
                new BirExpressionStatement { Expr = suspension } } };
        var root = new BirFileDeclaration { Methods = new[] { method } };
        var declarations = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirInterfaceDefinition { Name = "Contract", Kind = "interface" },
            new BirClassDefinition { Name = "Bound", Kind = "class" } } };
        var body = method.Body.Value;
        var parameters = method.Params.Value;
        ApplyAll(new[] { root, declarations }, null);
        Require(ReferenceEquals(root.Methods.Value[0], method) && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(method.Params.Value, parameters) && parameters[0].Type.Value == new TypeNode.Array(variable)
            && read.Elem.Value == ObjectType && read.Sty.Value == ObjectType && value.Type.Value == ObjectType
            && alias.Type.Value == ObjectType && addressed.Type.Value == variable && read.ErasedValueVariableFact.Value == variable
            && addressRead.Elem.Value == variable && !addressRead.Sty.IsPresent
            && !objectRead.ErasedValueVariableFact.IsPresent,
            "array reads/aliases changed signatures, native addresses or continuing declarations");
        Require(loop.Elem.Value == ObjectType && ((BirVariable)loop.Body.Value[0]).Type.Value == ObjectType
            && bodyRead.Sty.Value == ObjectType && bodyRead.ErasedValueVariableFact.Value == variable
            && outside.Sty.Value == new TypeNode.Fqn("kotlin.String")
            && cast.Type.Value == new TypeNode.Nullable(new TypeNode.Fqn("Bound"))
            && suspension.CapValues.Value[0] is BirCast { Type.Value: TypeNode.Fqn { Name: "object" } }
            && suspension.TypeParams.Value.Length == 3 && ((BirVariable)suspension.Body.Value[0]).Type.Value == innerVariable
            && innerRead.Elem.IsPresent == false && innerRead.Sty.IsPresent == false,
            "module bounds, loop scope, captures or independent suspend frame changed");

        var ownerVariable = new TypeNode.Tv("type", 0);
        var delegation = new BirArrayGet { Array = new BirLocal { Name = "values" },
            Index = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0) } };
        var constructor = new BirConstructor {
            Params = new[] { new BirParameter { Name = "values", Type = new TypeNode.Array(ownerVariable) } },
            BaseArgs = new IBirExpression[] { delegation }, Body = Array.Empty<IBirStatement>() };
        var constructed = new BirClassDefinition { Name = "Derived", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ctors = new[] { constructor } };
        ApplyAll(new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] { constructed } } }, null);
        Require(ReferenceEquals(constructed.Ctors.Value[0], constructor) && !constructor.PreStmts.IsPresent
            && ReferenceEquals(constructor.BaseArgs.Value[0], delegation) && delegation.Elem.Value == ObjectType
            && delegation.ErasedValueVariableFact.Value == ownerVariable
            && constructor.Params.Value[0].Type.Value == new TypeNode.Array(ownerVariable),
            "constructor delegation lost its owner frame, array witness or prologue presence");

        foreach (var direction in new[] { "next", "previous" })
        {
            var receiver = new BirCallStatic { Method = "receiverOnce", Args = Array.Empty<IBirExpression>(),
                Ret = new TypeNode.Fqn("kotlin.collections.ListIterator", new TypeNode[] { variable }) };
            var iterator = new BirCallInstance { Method = direction, OwnerType = receiver.Ret, Recv = receiver,
                DeclarationId = "iterator-" + direction, Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(),
                Sty = variable, Super = false, ClrOwnerResolved = false };
            var binding = new KotlinIteratorValueBinding();
            Require(binding.Element(iterator) == variable && binding.Element(iterator with { Super = true }) == null
                && binding.Element(iterator with { ClrOwnerResolved = true }) == null
                && binding.Element(iterator with { OwnerType = new TypeNode.Fqn("Unrelated") }) == null,
                "iterator binding lost selected protocol/native/super distinctions");
            var overridden = iterator with { DeclarationId = default, OwnerType = new TypeNode.Fqn("Derived"),
                Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("kotlin.collections.ListIterator"),
                    Member = direction, Arity = 0, DeclarationId = "override-" + direction } } };
            Require(binding.Element(overridden) == variable, "selected Kotlin override did not retain its iterator relation");
            var local = new BirVariable { Name = "value", Type = variable, Init = iterator };
            var iteratorAlias = new BirVariable { Name = "alias", Type = variable, Init = Local("value") };
            var captureReceiver = (BirCallStatic)receiver.DeepClone();
            var captureRead = (BirCallInstance)iterator.DeepClone();
            captureRead.Recv = captureReceiver;
            var capture = new BirNewClosure { ClosureType = new TypeNode.Fqn("Closure"),
                Captures = new IBirExpression[] { captureRead } };
            var caller = new BirFunctionDeclaration { Name = "iterate", Params = Array.Empty<BirParameter>(),
                TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Body = new IBirStatement[] { local, iteratorAlias, new BirExpressionStatement { Expr = capture } } };
            var file = new BirFileDeclaration { Methods = new[] { caller } };
            ApplyAll(new[] { file }, null);
            var block = local.Init.Value as BirValueBlock;
            Require(block?.Stmts.Value is [BirVariable temporary] && ReferenceEquals(temporary.Init.Value, receiver)
                && local.Type.Value == ObjectType && iteratorAlias.Type.Value == ObjectType
                && block.Result.Value is BirConditional { Else.Value: BirCallInstance direct, Then.Value: BirCallInstance erased }
                && direct.DeclarationId.Value == "iterator-" + direction && direct.Sty.Value == ObjectType
                && direct.ErasedValueVariableFact.Value == variable && !block.ErasedValueVariableFact.IsPresent
                && direct.Recv.Value is BirLocal use && use.Name.Value == temporary.Name.Value
                && erased.Method.Value == (direction == "previous" ? "dotktPreviousValue" : "dotktNextValue"),
                "iterator receiver was cloned/evaluated twice or exact fallback/origin facts were lost");
            Require(capture.Captures.Value[0] is BirCast { Type.Value: TypeNode.Fqn { Name: "object" },
                    E.Value: BirValueBlock capturedBlock }
                && capturedBlock.Stmts.Value is [BirVariable { Init.Value: BirCallStatic capturedReceiver }]
                && !ReferenceEquals(capturedReceiver, captureReceiver) && capturedReceiver.Method.Value == "receiverOnce"
                && capturedBlock.Result.Value is BirConditional { Else.Value: BirCallInstance { Sty.Value: TypeNode.Fqn { Name: "object" } } },
                "capture cloned an unbound iterator read instead of its already-planned value block");
        }
        Console.WriteLine("[owning element values] self-test OK (array/iterator modes, module bounds, alias/address/loop scope, captures, exact protocol and receiver-once)");
        SourceElementFrameSelfTest();
    }

    static void SourceElementFrameSelfTest()
    {
        var variable = new TypeNode.Tv("method", 2);
        var source = new TypeNode.Fqn("Carrier", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("method", 1)) });
        var physical = new TypeNode.Fqn("Carrier", new TypeNode[] { new TypeNode.Tv("method", 3) });
        BirFunctionDeclaration Method(TypeNode.Tv element) => new() { Name = "consume", Ret = ObjectType,
            Params = new[] { new BirParameter { Name = "xs", Type = new TypeNode.Array(element) } },
            Body = new IBirStatement[] { new BirReturn { Value = new BirCast { Type = element,
                ReifiedTypeOperand = false, E = new BirArrayGet { Array = new BirLocal { Name = "xs" },
                    Index = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0) } } } } } };
        var method = Method(variable);
        method.TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" },
            new BirTypeParameter { Name = "T", Constraints = new[] { physical } }, new BirTypeParameter { Name = "$nullable1" } };
        method.NullableRepresentationFrame = new NullableRepresentationFrame(3, new[] { 1 }, new[] { 0, 1, 2, 3 });
        method.MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [2] = new[] { source } });
        var nestedSource = new TypeNode.Fqn("Carrier", new TypeNode[] { new TypeNode.Tv("type", 1) });
        var nestedPhysical = new TypeNode.Fqn("Carrier", new TypeNode[] { new TypeNode.Tv("type", 2) });
        var nested = Method(new TypeNode.Tv("method", 0));
        nested.TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new[] { nestedPhysical } } };
        nested.MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = new[] { nestedSource } });
        var physicalOnly = (BirFunctionDeclaration)nested.DeepClone();
        physicalOnly.MethodTypeParameterBoundsPre = default;
        var owner = new BirClassDefinition { Name = "Nested", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "Outer" }, new BirTypeParameter { Name = "$nullableOuter" },
                new BirTypeParameter { Name = "Inner" } }, Methods = new[] { nested, physicalOnly },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(
                new NullableRepresentationFrame(2, new[] { 0 }, new[] { 0, 2, 1 })) };
        ApplyAll(new[] { new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] { owner } } }, null);
        foreach (var declaration in new[] { method, nested, physicalOnly })
        {
            var expected = ReferenceEquals(declaration, method) ? physical : nestedPhysical;
            if (((BirCast)((BirReturn)declaration.Body.Value[0]).Value.Value).Type.Value != new TypeNode.Nullable(expected))
                throw new InvalidOperationException("Owning array bound confused saved source and physical nullable frame indices");
        }
    }
}
