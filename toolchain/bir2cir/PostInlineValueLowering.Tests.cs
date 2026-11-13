using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class PostInlineValueLoweringTests
{
    internal static void SelfTest()
    {
        var structure = new TypeNode.Fqn("Fixture.Struct", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        var joined = new BirVariable { Name = "joined", Type = structure, JoinNullBranch = true };
        var reference = new BirConditional { Type = new TypeNode.Fqn("kotlin.String"), JoinNullBranch = true };
        var unmarked = new BirVariable { Name = "ordinary", Type = structure, JoinNullBranch = false };
        var voidish = new BirValueBlock { Type = new TypeNode.Fqn("kotlin.Unit"), JoinNullBranch = true };
        var explicitNull = new BirValueBlock { JoinNullBranch = OptionalField<bool>.Null };
        var body = new BirValueBlock {
            Stmts = new IBirStatement[] { joined, unmarked, new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(reference) } },
            Result = OptionalField<IBirExpression>.Of(voidish),
        };
        ValueTypeOracle isValue = type => type.Name is "Fixture.Struct" or "kotlin.Unit";
        ValueJoinNullWidening.Apply(body, isValue);
        ValueJoinNullWidening.Apply(explicitNull, isValue);
        if (!ReferenceEquals(body.Stmts.Value[0], joined) || joined.Type.Value != new TypeNode.Nullable(structure)
            || joined.JoinNullBranch.IsPresent || reference.JoinNullBranch.IsPresent
            || reference.Type.Value != new TypeNode.Fqn("kotlin.String")
            || !unmarked.JoinNullBranch.HasValue || unmarked.JoinNullBranch.Value || unmarked.Type.Value != structure
            || voidish.JoinNullBranch.IsPresent || voidish.Type.Value != new TypeNode.Fqn("kotlin.Unit")
            || !explicitNull.JoinNullBranch.IsNull)
            throw new InvalidOperationException("Typed join widening changed authored fact presence or constructed value/void rules");

        var nothing = new TypeNode.Fqn("kotlin.Nothing");
        BirCallStatic Bottom(string name) => new() {
            Method = name, Ret = nothing, Args = Array.Empty<IBirExpression>(),
        };
        var effectCall = Bottom("effect");
        var initializerCall = Bottom("initializer");
        var thrownCall = Bottom("throwOperand");
        var conditionCall = Bottom("condition");
        var branchCall = Bottom("branchEffect");
        var constructorCall = Bottom("baseArgument");
        var variable = new BirVariable { Name = "value", Type = structure, Init = OptionalField<IBirExpression>.Of(initializerCall) };
        var thrown = new BirThrow { Value = OptionalField<IBirExpression>.Of(thrownCall) };
        var branch = new BirBranch {
            Cond = OptionalField<IBirExpression>.Of(conditionCall),
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(branchCall) } },
        };
        var method = new BirFunctionDeclaration {
            Name = "method", Ret = nothing, Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(effectCall) }, variable, thrown,
                new BirIf { Branches = new[] { branch } },
            },
        };
        var constructor = new BirConstructor {
            Params = Array.Empty<BirParameter>(), BaseArgs = new IBirExpression[] { constructorCall },
            PreStmts = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(Bottom("delegationEffect")) } },
            Body = Array.Empty<IBirStatement>(),
        };
        var type = new BirClassDefinition { Name = "Fixture", Kind = "class", Methods = new[] { method }, Ctors = new[] { constructor } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { type } };
        var methods = type.Methods.Value;
        NothingValueTermination.Apply(file);
        if (!ReferenceEquals(type.Methods.Value, methods) || !ReferenceEquals(type.Methods.Value[0], method)
            || variable.Init.Value is not BirThrowExpression { Value.Value: BirCallStatic { Method.Value: "initializer" } }
            || !ReferenceEquals(((BirExpressionStatement)method.Body.Value[0]).Expr.Value, effectCall)
            || !ReferenceEquals(thrown.Value.Value, thrownCall)
            || branch.Cond.Value is not BirThrowExpression
            || !ReferenceEquals(((BirExpressionStatement)branch.Body.Value[0]).Expr.Value, branchCall)
            || constructor.BaseArgs.Value[0] is not BirThrowExpression
            || ((BirExpressionStatement)constructor.PreStmts.Value[0]).Expr.Value is not BirCallStatic)
            throw new InvalidOperationException("Typed Nothing termination confused consumed values with declarations, records, statements or terminated operands");
        var unchanged = file.DeepClone();
        NothingValueTermination.Apply(file);
        if (!file.StructurallyEquals(unchanged))
            throw new InvalidOperationException("Typed Nothing termination is not idempotent");
        var opaqueCall = Bottom("opaque");
        var payload = new BirInlinePayload {
            Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(opaqueCall) } },
        };
        NothingValueTermination.Apply(payload);
        if (!ReferenceEquals(((BirReturn)payload.Body.Value[0]).Value.Value, opaqueCall))
            throw new InvalidOperationException("Typed Nothing termination entered an opaque executable payload");
        var inferred = new BirConditional { Type = nothing };
        if (!ReferenceEquals(NothingValueTermination.Consume(inferred), inferred))
            throw new InvalidOperationException("Typed Nothing termination used a derived result instead of an authored stamp");
        ClrFlagsOperationLowering.SelfTestPlan();
        Console.WriteLine("[typed post-inline values] self-test OK (join facts, consumed slots, opaque bodies, shared evaluation)");
    }
}

static partial class ClrFlagsOperationLowering
{
    internal static void SelfTestPlan()
    {
        var enumeration = new TypeNode.Fqn("Fixture.Flags");
        var representation = new FlagsEnumRepresentation(enumeration, new TypeNode.Fqn("System.UInt64"));
        BirCallStatic Call(string name) => new() { Method = name, Ret = enumeration, Args = Array.Empty<IBirExpression>() };
        var plan = Plan("contains", representation, Call("receiverEffect"), Call("argumentEffect"));
        if (plan.Bindings.Value is not [BirCallBinding receiver, BirCallBinding argument]
            || receiver.Phase.Value != "recv" || argument.Phase.Value != "arg" || receiver.Id.Value == argument.Id.Value
            || receiver.Stable.Value || argument.Stable.Value
            || plan.Type.Value != new TypeNode.Fqn("kotlin.Boolean"))
            throw new InvalidOperationException("Typed flags lowering changed its ordered, unstable value bindings");
        var root = new BirValueBlock { Result = OptionalField<IBirExpression>.Of(plan) };
        TypedCallEvalLowering.Apply(root, type => type.Name == "Fixture.Flags");
        var calls = new List<string>();
        root.Visit(node => {
            if (node is BirCallStatic call) calls.Add(call.Method.Value);
            if (node is BirCallEvaluation or BirBindingReference)
                throw new InvalidOperationException("Typed flags plan was not realized by the shared evaluator");
        });
        if (!calls.SequenceEqual(new[] { "receiverEffect", "argumentEffect" }))
            throw new InvalidOperationException("Typed flags contains duplicated or reordered a receiver/argument evaluation");
        var inverse = new BirValueBlock { Result = OptionalField<IBirExpression>.Of(Plan("inv", representation, Call("inverseEffect"), null)) };
        TypedCallEvalLowering.Apply(inverse, type => type.Name == "Fixture.Flags");
        var inversions = 0;
        inverse.Visit(node => { if (node is BirCallStatic) inversions++; });
        if (inversions != 1)
            throw new InvalidOperationException("Typed flags inverse did not evaluate its receiver exactly once");
    }
}
