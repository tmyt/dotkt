using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ErasedElementValues
{
    static void SourceScalarSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var projection = new KotlinGenericScalarSlots.Projection(new(), new(StringComparer.Ordinal) { "scalar" },
            null, new(StringComparer.Ordinal), refs);
        BirCallStatic Read() => new() { Owner = new TypeNode.Fqn("Source"), Method = "scalar", DeclarationId = "scalar",
            Ret = ObjectType, DynRet = variable, Sty = variable, Args = Array.Empty<IBirExpression>() };
        BirLocal Local(string name) => new() { Name = name, Sty = variable };
        var read = Read();
        var value = new BirVariable { Name = "value", Type = variable, Init = read };
        var alias = new BirVariable { Name = "alias", Type = variable, Init = Local("value") };
        var conditional = new BirConditional { Type = variable,
            Cond = OptionalField<IBirExpression>.Of(new BirConstant { Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(true) }),
            Then = Local("alias"), Else = Local("value") };
        var block = new BirValueBlock { Type = variable, Stmts = new IBirStatement[] {
            new BirVariable { Name = "inner", Type = variable, Init = Local("alias") },
        }, Result = Local("inner") };
        var cast = new BirCast { Type = variable, ReifiedTypeOperand = false, E = Local("alias"), Sty = variable };
        var constrained = new BirConstrainedCall { Method = "use", Iface = new TypeNode.Fqn("Contract"),
            RecvType = variable, Recv = Local("value"), Ret = ObjectType, Args = Array.Empty<IBirExpression>(),
            Sig = Array.Empty<TypeNode>(), TypeArgs = Array.Empty<TypeNode>(), DeclarationId = "selected", SuspendCall = false,
            DotktDeclarationIdentityBound = true, NullableWitnessCount = 0,
        };
        var closure = new BirNewClosure { ClosureType = new TypeNode.Fqn("Closure"), Method = "invoke",
            Captures = new IBirExpression[] { Local("value") },
            FuncType = new TypeNode.Fn(false, ObjectType, Array.Empty<TypeNode>()),
            SynthClass = new BirClosureDefinition { Name = "Closure", Params = Array.Empty<BirLambdaParameter>(), Ret = ObjectType,
                Body = new IBirStatement[] { new BirReturn { Value = Local("value") } } },
        };
        var addressed = new BirVariable { Name = "addressed", Type = variable, Init = Read() };
        var native = new BirCallStatic { Method = "native", Sig = new TypeNode[] { new TypeNode.ByRef(variable), new TypeNode.ByRef(variable) },
            Args = new IBirExpression[] { new BirByRefOf { Inner = Local("incoming") }, new BirByRefOf { Inner = Local("addressed") } },
        };
        var method = new BirFunctionDeclaration { Name = "consume", Ret = ObjectType,
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] {
                new TypeNode.Fqn("Contract"), new TypeNode.Fqn("Bound"),
            } } },
            Params = new[] { new BirParameter { Name = "incoming", Type = ObjectType, KotlinType = variable } },
            Body = new IBirStatement[] {
                value, alias, new BirSetLocal { Name = "alias", Value = Local("value") }, addressed,
                new BirExpressionStatement { Expr = native }, new BirExpressionStatement { Expr = conditional },
                new BirExpressionStatement { Expr = block }, new BirExpressionStatement { Expr = cast },
                new BirExpressionStatement { Expr = closure }, new BirExpressionStatement { Expr = constrained },
            },
        };
        var root = new BirFileDeclaration { FileClass = "Source", Methods = new[] { method }, Types = new IBirTypeDeclaration[] {
            new BirInterfaceDefinition { Name = "Contract", Kind = "interface" },
            new BirClassDefinition { Name = "Bound", Kind = "class" },
        } };
        var expected = root.ToDom();
        ApplyAll(new[] { expected }, refs, _ => false, scalarResults: call => projection.Result(call, refs), scalarCallsOnly: true);
        var parameters = method.Params.Value;
        ApplyScalarCalls(root, refs, _ => false, null, call => projection.Result(call, refs));
        if (!ReferenceEquals(root.Methods.Value[0], method) || !ReferenceEquals(method.Params.Value, parameters)
            || method.Body.Value[0] is not BirVariable { Type.Value: TypeNode.Tv, Name.Value: "$nativeArgument$0" }
            || value.Type.Value != ObjectType || alias.Type.Value != ObjectType || addressed.Type.Value != variable
            || read.Ret.Value != ObjectType || read.DynRet.Value != ObjectType || read.Sty.Value != ObjectType
            || conditional.Type.Value != ObjectType || block.Type.Value != ObjectType
            || cast.Type.Value != new TypeNode.Nullable(new TypeNode.Fqn("Bound"))
            || value.Init.Value is not BirCallStatic { ErasedValueVariableFact.HasValue: true }
            || closure.Captures.Value[0] is not BirCast { Type.Value: TypeNode.Fqn { Name: "object" } }
            || ((BirReturn)closure.SynthClass.Value.Body.Value[0]).Value.Value is not BirLocal { Sty.Value: TypeNode.Tv }
            || ((BirExpressionStatement)method.Body.Value[^1]).Expr.Value is not BirCallInstance {
                Virtual.Value: true, DeclarationId.Value: "selected", DotktDeclarationIdentityBound.Value: true,
                Recv.Value: BirCast { Type.Value: TypeNode.Fqn { Name: "Contract" } } }
            || !JsonNode.DeepEquals(expected, root.ToDom()))
            throw new InvalidOperationException("Owning scalar values lost origins, storage, lexical bindings, capture bounds or constrained facts");

        var ownerVariable = new TypeNode.Tv("type", 0);
        var field = new BirFieldDeclaration { Name = "field", Type = ObjectType, KotlinType = ownerVariable };
        var fieldRead = new BirFieldGet { Name = "field", OwnerType = new TypeNode.Fqn("Storage"), Recv = new BirThis(), Sty = ownerVariable };
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
            new BirVariable { Name = "value", Type = ownerVariable, Init = fieldRead },
        } };
        var owner = new BirClassDefinition { Name = "Storage", Kind = "class", Fields = new[] { field },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ctors = new[] { constructor } };
        root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
        expected = root.ToDom();
        ApplyAll(new[] { expected }, refs, _ => false, scalarResults: call => projection.Result(call, refs), scalarCallsOnly: true);
        ApplyScalarCalls(root, refs, _ => false, null, call => projection.Result(call, refs));
        if (!ReferenceEquals(owner.Fields.Value[0], field) || field.Type.Value != ObjectType
            || fieldRead.Sty.Value != ObjectType || constructor.PreStmts.IsPresent
            || !JsonNode.DeepEquals(expected, root.ToDom()))
            throw new InvalidOperationException("Owning scalar values changed this-field sources or constructor prologue presence");
        Console.WriteLine("[owning scalar values] self-test OK (native generic storage, origins, fixed-point aliases, regions, captures, bounds, constrained facts, fields and constructors)");
    }
}
