using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal static void OperandPlanSourceSelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var valueType = new TypeNode.Fqn("sample.Value");
        BirCallStatic Call(string name, TypeNode result = null, params IBirExpression[] args) => new() {
            Method = name, Ret = result ?? integer, Args = args,
        };
        BirCallStatic Suspend(string name = "pause") => new() {
            Method = name, Ret = integer, SuspendCall = true, Args = Array.Empty<IBirExpression>(),
        };
        BirExpressionStatement Statement(IBirExpression value) => new() { Expr = SourceOperandField(value) };
        BirFileDeclaration File(params IBirStatement[] body) => new() {
            FileClass = "sample.OperandsKt", Methods = new[] {
                new BirFunctionDeclaration { Name = "test", Ret = integer, Body = body },
            },
        };
        var methodReturns = new Dictionary<string, TypeNode>(StringComparer.Ordinal) {
            ["sample.Owner#indexed"] = text, ["#fallback"] = integer,
        };
        var fields = new Dictionary<string, TypeNode>(StringComparer.Ordinal) {
            ["sample.Owner#field"] = text, ["#top"] = integer,
        };
        var typer = new SourceExprTyper(methodReturns, fields);
        var retired = new List<BirGraphValue>();
        void Plan(BirFileDeclaration file)
        {
            retired.Clear();
            PlanSuspensionBearingOperands(new[] { file }, typer,
                type => type.Name == valueType.Name, (_, _) => true, retired.Add);
        }
        void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning suspend operand plan: " + message); }
        BirValueBlock Result(BirExpressionStatement statement) => statement.Expr.OrDefault() as BirValueBlock
            ?? throw new InvalidOperationException("Expected a planned operand value block");

        // Post-order lifts the inner suspension before the outer call gets a
        // state label, while preserving both actual call occurrences.
        var left = Call("before");
        var pause = Suspend();
        var outer = Call("outer", integer, left, pause);
        outer.SuspendCall = true;
        var statement = Statement(outer);
        var file = File(statement);
        var method = file.Methods.Value[0];
        var originalBody = method.Body.Value;
        Plan(file);
        var block = Result(statement);
        Require(ReferenceEquals(file.Methods.Value[0], method) && ReferenceEquals(method.Body.Value, originalBody),
            "planning replaced an authored method/body vector");
        Require(block.Stmts.Value.Length == 2
            && ReferenceEquals(((BirVariable)block.Stmts.Value[0]).Init.Value, left)
            && ReferenceEquals(((BirVariable)block.Stmts.Value[1]).Init.Value, pause)
            && ReferenceEquals(block.Result.Value, outer) && outer.Args.Value.All(arg => arg is BirLocal),
            "nested suspend-call operands were not moved in left-to-right order");

        // A nested callable is already a registered owning occurrence at this
        // clock. Binding its enclosing expression must MOVE, never clone it.
        var lambdaPause = Suspend("lambdaPause");
        var lambdaAdd = new BirBinaryOperation { Op = "+", Lhs = new BirLocal { Name = "x" }, Rhs = lambdaPause };
        var lambdaStatement = Statement(lambdaAdd);
        var lambda = new BirNewSuspendLambda {
            DeclarationId = "sample.lambda", Arity = 1, SuspendRet = integer,
            Captures = new[] { new BirCaptureDescriptor { Name = "cap", Type = integer } },
            CapValues = new IBirExpression[] { new BirLocal { Name = "outer" } },
            Params = new[] { new BirLambdaParameter { Name = "x", Type = integer } },
            Body = new IBirStatement[] { lambdaStatement },
            FuncType = new TypeNode.Fn(true, integer, new TypeNode[] { integer }),
        };
        var maker = Call("make", integer, lambda);
        var caller = Call("consume", integer, maker, Suspend());
        statement = Statement(caller);
        file = File(new BirVariable { Name = "outer", Type = integer }, new BirVariable { Name = "x", Type = text }, statement);
        Plan(file);
        block = Result(statement);
        var movedMaker = (BirCallStatic)((BirVariable)block.Stmts.Value[0]).Init.Value;
        var lambdaBlock = Result(lambdaStatement);
        Require(ReferenceEquals(movedMaker, maker) && ReferenceEquals(movedMaker.Args.Value[0], lambda)
            && ReferenceEquals(lambda.Body.Value[0], lambdaStatement)
            && ((BirVariable)lambdaBlock.Stmts.Value[0]).Type.Value == integer,
            "nested callable identity or its parameter shadowing changed");

        // Captures are lexical declarations in the lambda body, not untyped
        // reads or values inferred from the capture expression's physical shape.
        var captureAdd = new BirBinaryOperation { Op = "+", Lhs = new BirLocal { Name = "cap" }, Rhs = Suspend() };
        lambdaStatement = Statement(captureAdd);
        lambda = new BirNewSuspendLambda { Arity = 0, SuspendRet = integer,
            FuncType = new TypeNode.Fn(true, integer, Array.Empty<TypeNode>()), Captures = new[] {
            new BirCaptureDescriptor { Name = "cap", Type = integer },
        }, CapValues = new IBirExpression[] { new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(1) } },
            Params = Array.Empty<BirLambdaParameter>(), Body = new IBirStatement[] { lambdaStatement } };
        Plan(File(Statement(lambda)));
        Require(((BirVariable)Result(lambdaStatement).Stmts.Value[0]).Type.Value == integer,
            "suspend-lambda capture declarations did not seed its local scope");

        var captureValue = new BirBinaryOperation { Op = "+", Lhs = new BirLocal { Name = "x" }, Rhs = Suspend() };
        var ownBody = Statement(Call("consumeString", integer, new BirLocal { Name = "x" }, Suspend()));
        lambda = new BirNewSuspendLambda { Arity = 1, SuspendRet = integer,
            Captures = new[] { new BirCaptureDescriptor { Name = "cap", Type = integer } },
            CapValues = new IBirExpression[] { captureValue },
            Params = new[] { new BirLambdaParameter { Name = "x", Type = text } },
            FuncType = new TypeNode.Fn(true, integer, new TypeNode[] { text }),
            Body = new IBirStatement[] { ownBody } };
        Plan(File(new BirVariable { Name = "x", Type = integer }, Statement(lambda)));
        Require(lambda.CapValues.Value[0] is BirValueBlock captureBlock
            && ((BirVariable)captureBlock.Stmts.Value[0]).Type.Value == integer
            && ((BirVariable)Result(ownBody).Stmts.Value[0]).Type.Value == text,
            "construction operands used the lambda parameter frame instead of the caller frame");

        // An expression's terminal operand drops the consumer/right-hand work,
        // not the observable evaluations before that operand.
        var effect = Call("effect");
        var terminal = new BirThrowExpression { Value = SourceOperandField(new BirConstant {
            Type = new TypeNode.Fqn("System.Exception"), Value = new BirLiteral.NullLiteral(),
        }) };
        var discardedLambda = new BirNewSuspendLambda { Arity = 0, SuspendRet = integer,
            FuncType = new TypeNode.Fn(true, integer, Array.Empty<TypeNode>()),
            Captures = Array.Empty<BirCaptureDescriptor>(), CapValues = Array.Empty<IBirExpression>(),
            Params = Array.Empty<BirLambdaParameter>(), Body = Array.Empty<IBirStatement>() };
        var tail = Call("discarded", integer, discardedLambda, Suspend("unreachablePause"));
        statement = Statement(Call("unreachable", text, effect, terminal, tail));
        Plan(File(statement));
        block = Result(statement);
        Require(block.Type.Value == text && block.Stmts.Value.Length == 1
            && ReferenceEquals(((BirVariable)block.Stmts.Value[0]).Init.Value, effect)
            && ReferenceEquals(block.Result.Value, terminal), "terminal operand did not truncate at the exact evaluation position");
        var retiredLambda = false;
        if (retired.Count == 1) retired[0].Visit(node => retiredLambda |= ReferenceEquals(node, discardedLambda));
        Require(retired.Count == 1 && retiredLambda,
            "a discarded postorder subtree was not explicitly reported with its actual nested declaration");

        // Address computations pin their value operands, but retain the actual
        // storage path. A statement store must remain a statement, not a value.
        var array = Call("array", new TypeNode.Array(valueType));
        var location = new BirArrayGet { Elem = valueType, Array = SourceOperandField(array),
            Index = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) } };
        var writeValue = Suspend();
        var write = new BirSetField { OwnerType = valueType, Name = "value",
            Recv = SourceOperandField(location), Value = SourceOperandField(writeValue) };
        file = File(write);
        Plan(file);
        var statementBlock = file.Methods.Value[0].Body.Value[0] as BirBlock;
        Require(statementBlock?.Body.Value.Length == 2
            && ReferenceEquals(((BirVariable)statementBlock.Body.Value[0]).Init.Value, array)
            && ReferenceEquals(statementBlock.Body.Value[1], write)
            && ReferenceEquals(write.Recv.Value, location) && location.Array.Value is BirLocal
            && ReferenceEquals(write.Value.Value, writeValue), "statement store copied storage or lost its ordered address pin");

        // Declaration parameters type constructor delegation operands as well
        // as ordinary method operands. Existing source indexes are only fallback.
        var delegation = Call("baseArgument", integer, new BirLocal { Name = "p" }, Suspend());
        var constructor = new BirConstructor { Params = new[] { new BirParameter { Name = "p", Type = integer } },
            BaseArgs = new IBirExpression[] { delegation } };
        file = new BirFileDeclaration { FileClass = "sample.CtorKt", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "sample.Ctor", Ctors = new[] { constructor } },
        } };
        Plan(file);
        Require(constructor.BaseArgs.Value[0] is BirValueBlock ctorBlock
            && ((BirVariable)ctorBlock.Stmts.Value[0]).Type.Value == integer,
            "constructor parameter scope was not available to delegation planning");
        var locals = new Dictionary<string, LocalDecl> { ["declared"] = new(integer, "source argument") };
        Require(typer.Of(new BirLocal { Name = "declared", Sty = text }, locals) == integer
            && typer.Of(new BirCallStatic { Owner = new TypeNode.Fqn("sample.Owner"), Method = "indexed" }, locals) == text
            && typer.Of(new BirCallStatic { Method = "fallback" }, locals) == integer
            && typer.Of(new BirFieldGet { OwnerType = new TypeNode.Fqn("sample.Owner"), Name = "field" }, locals) == text
            && typer.Of(new BirStaticFieldGet { Name = "top" }, locals) == integer,
            "declared-local, qualified return or field facts lost their existing precedence");

        var roleRead = new BirLocal { Name = "saved" };
        statement = Statement(Call("role", integer, roleRead, Suspend()));
        Plan(File(new BirVariable { Name = "saved", Type = integer, Role = "the source argument" }, statement));
        Require(((BirVariable)Result(statement).Stmts.Value[0]).Role.Value == "the source argument",
            "a materialized local lost the role recorded by its producer");

        // A descriptor writes exactly the concrete operand it read, including
        // generic receiver/argument and structured spread-part positions.
        var receiver = Call("receiver", new TypeNode.Fqn("sample.Owner"));
        var argument = Call("argument");
        var generic = new BirGenericInstance { Recv = SourceOperandField(receiver), Args = new IBirExpression[] { argument } };
        var descriptor = EvalOrderOf(generic, _ => false).Value;
        Require(descriptor.HasReceiver && descriptor.ArgumentStart == 1 && descriptor.Slots.Count == 2
            && ReferenceEquals(descriptor.Operands[0], receiver) && ReferenceEquals(descriptor.Operands[1], argument),
            "generic call descriptor changed receiver-before-argument order");
        var replacement = new BirLocal { Name = "replacement", Sty = integer };
        descriptor.Slots[1].Slot.Replace(replacement);
        Require(ReferenceEquals(generic.Args.Value[0], replacement), "descriptor did not write its actual owning argument slot");
        var spread = new BirSpreadConcat { Elem = integer, Parts = new[] {
            new BirSpreadPart { E = SourceOperandField(argument) },
            new BirSpreadPart { E = SourceOperandField(receiver) },
        } };
        descriptor = EvalOrderOf(spread, _ => false).Value;
        descriptor.Slots[1].Slot.Replace(replacement);
        Require(ReferenceEquals(descriptor.Operands[0], argument) && ReferenceEquals(spread.Parts.Value[1].E.Value, replacement),
            "spread descriptor detached or reordered a part");

        // No suspension, or a re-readable forced prefix, leaves the actual node,
        // operand vectors and optional null/absence untouched.
        var literal = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(4) };
        var unchanged = Call("unchanged", integer, literal);
        unchanged.Owner = OptionalField<TypeNode>.Null;
        var arguments = unchanged.Args.Value;
        statement = Statement(unchanged);
        Plan(File(statement));
        Require(ReferenceEquals(statement.Expr.Value, unchanged) && ReferenceEquals(unchanged.Args.Value, arguments)
            && unchanged.Owner.IsNull && !unchanged.OwnerType.IsPresent, "no-op planning changed ownership/presence");
        unchanged = Call("stablePrefix", integer, literal, Suspend());
        statement = Statement(unchanged);
        Plan(File(statement));
        Require(ReferenceEquals(statement.Expr.Value, unchanged) && ReferenceEquals(unchanged.Args.Value[0], literal),
            "a re-readable prefix acquired a redundant temporary or a clone");

        // The shared owning realization cannot clone a side-effecting expression
        // merely because the same plan binding has several readers.
        var identityLambda = new BirNewSuspendLambda { DeclarationId = "sample.once", Arity = 0, SuspendRet = integer,
            Captures = Array.Empty<BirCaptureDescriptor>(), CapValues = Array.Empty<IBirExpression>(),
            Params = Array.Empty<BirLambdaParameter>(), FuncType = new TypeNode.Fn(true, integer, Array.Empty<TypeNode>()),
            Body = Array.Empty<IBirStatement>() };
        var binding = new BirCallBinding { Id = "owned", Expr = SourceOperandField(identityLambda),
            Type = new TypeNode.Fqn("sample.Function"), Stable = false };
        var repeated = Call("twice", integer, new BirBindingReference { Id = "owned" }, new BirBindingReference { Id = "owned" });
        var (temporaries, substitutions) = TypedCallEvalLowering.MaterialiseOwned(new[] { binding },
            new BirGraphValue[] { repeated }, "owning repeated read", new[] { false }, _ => false);
        TypedCallEvalLowering.SubstituteOwned(repeated, substitutions);
        Require(temporaries.Length == 1 && ReferenceEquals(((BirVariable)temporaries[0]).Init.Value, identityLambda)
            && repeated.Args.Value.All(argument => argument is BirLocal)
            && !ReferenceEquals(repeated.Args.Value[0], repeated.Args.Value[1]),
            "repeated owning readers cloned a declaration or shared a mutable local read");

        Console.WriteLine("[owning suspend operand plans] self-test OK (postorder, MOVE identity, lexical captures, terminal order, address stores, delegation, indexes, presence, repeated reads)");
    }
}
