using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal static void EngineSourceSelfTest()
    {
        SourceFunGen.EngineSelfTest();
        TypedEscapingFinally.OwnedSelfTest();
    }

    internal sealed partial class SourceFunGen
    {
        internal static void EngineSelfTest()
        {
            static void Check(bool condition, string reason)
            { if (!condition) throw new InvalidOperationException("Typed suspend engine: " + reason); }
            var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            using var callSignatures = new ReferencedCallSignatures();
            var method = new BirFunctionDeclaration { Name = "core", Mods = new BirModifiers { Suspend = true },
                Ret = IntTn, SuspendRet = IntTn, SuspendResult = IntTn,
                Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
            var owner = new BirClassDefinition { Name = "EngineOwner", Kind = "class", Methods = new[] { method } };
            var file = new BirFileDeclaration { FileClass = "EngineFile", Methods = Array.Empty<BirFunctionDeclaration>(),
                Types = new IBirTypeDeclaration[] { owner } };
            var transitions = new DeclarationTestTransitions();
            var context = new SuspendModuleContext(new[] { file }, references, new HashSet<string>(), false,
                new Dictionary<string, string> { ["kotlin.coroutines.Continuation"] = "ContinuationCarrier" },
                _ => false, null, new Dictionary<string, int[]>(), new Dictionary<string, string>(), transitions, callSignatures.ProjectColdCall);
            context.PrepareStage0();
            SourceFunGen Generator() => new(context, new SourceFunctionInput.Named(file, owner, method, "", []));

            // Construction operands belong to the caller; the donor body remains
            // the nested lambda's frame, even in a suspension-free subtree.
            var generator = Generator();
            generator.FieldStorage("carried", IntTn, RoleLocal, true, "pause");
            var donorThis = new BirThis(); var donorLocal = Local("carried");
            var donorReturn = new BirReturn { Value = donorThis };
            var supplied = new BirCast { Type = IntTn, E = Local("carried") };
            var nested = new BirNewSuspendLambda { Arity = 0, SuspendRet = IntTn,
                Params = Array.Empty<BirLambdaParameter>(),
                Captures = new[] { new BirCaptureDescriptor { Name = "carried", Type = IntTn },
                    new BirCaptureDescriptor { Name = "receiver", Type = new TypeNode.Fqn("EngineOwner"), Outer = true } },
                CapValues = new IBirExpression[] { supplied, new BirThis() },
                Body = new IBirStatement[] { donorReturn, new BirExpressionStatement { Expr = donorLocal } } };
            Check(ReferenceEquals(generator.RewriteNoSpill((BirGraphValue)nested), nested)
                && ReferenceEquals(nested.Body.Value[0], donorReturn) && ReferenceEquals(donorReturn.Value.Value, donorThis)
                && ReferenceEquals(((BirExpressionStatement)nested.Body.Value[1]).Expr.Value, donorLocal)
                && ReferenceEquals(nested.CapValues.Value[0], supplied)
                && supplied.E.Value is BirFieldGet { Name.Value: "carried" }
                && nested.CapValues.Value[1] is BirFieldGet { Name.Value: ThisField },
                "NoSpill crossed a donor frame or cloned a continuing capture operand");
            var self = generator.StateMachineSelf();
            Check(ReferenceEquals(generator.RewriteNoSpill((BirGraphValue)self), self)
                && generator.RewriteNoSpill((BirGraphValue)new BirThis()) is BirFieldGet { Name.Value: ThisField },
                "authored state-machine self and the enclosing receiver were conflated");

            var from = new BirCallStatic { Owner = new TypeNode.Fqn("EngineFile"), Method = "from", Ret = IntTn, Args = Array.Empty<IBirExpression>() };
            var to = new BirCallStatic { Owner = new TypeNode.Fqn("EngineFile"), Method = "to", Ret = IntTn, Args = Array.Empty<IBirExpression>() };
            static BirCallStatic Pause() => new() { Owner = new TypeNode.Fqn("EngineFile"), Method = "pause", Ret = IntTn,
                SuspendCall = true, Args = Array.Empty<IBirExpression>() };
            var evaluation = new BirExpressionStatement { Expr = Pause() };
            var loop = new BirFor { Var = "index", From = from, To = to, Step = 1, Cmp = "<",
                Body = new IBirStatement[] { evaluation, new BirContinue() } };
            var flattened = generator.FlattenSuspendingLoops(new IBirStatement[] { loop });
            Check(flattened[0] is BirVariable { Name.Value: "index" } initial && ReferenceEquals(initial.Init.Value, from)
                && flattened[2] is BirBranchIf { Cond.Value: BirBinaryOperation comparison }
                && ReferenceEquals(comparison.Rhs.Value, to) && ReferenceEquals(flattened[3], evaluation)
                && flattened[4] is BirGoto next && flattened[5] is BirLabel nextLabel && next.Id.Value == nextLabel.Id.Value,
                "loop normalization changed operand ownership, header order or the continue target");

            var handlerRead = Local("caught");
            var handlerCall = new BirExpressionStatement { Expr = Pause() };
            var ordinary = new BirCatch { ExcType = new TypeNode.Fqn("System.ArgumentException"), Var = "ordinary", Body = Array.Empty<IBirStatement>() };
            var handler = new BirCatch { ExcType = new TypeNode.Fqn("System.Exception"), Var = "caught",
                Body = new IBirStatement[] { handlerCall, new BirExpressionStatement { Expr = handlerRead } } };
            var attempt = new BirTry { Body = Array.Empty<IBirStatement>(), Catches = new[] { ordinary, handler } };
            var hoisted = generator.HoistSuspendingCatches(new IBirStatement[] { attempt });
            Check(hoisted[0] is BirVariable { Name.Value: "__exc$1" }
                && ReferenceEquals(hoisted[1], attempt) && ReferenceEquals(attempt.Catches.Value[0], ordinary)
                && !ReferenceEquals(attempt.Catches.Value[1], handler)
                && ReferenceEquals(hoisted[3], handlerCall) && handlerRead.Name.Value == "__exc$1",
                "suspending catch normalization lost sibling identity, handler scope or tail ownership");

            var returned = IntConst(42); var cleanup = new BirExpressionStatement { Expr = Pause() };
            var finallyOutput = new List<IBirStatement>();
            generator.EmitSuspendingFinally(new IBirStatement[] { Ret(returned), Goto(900) },
                new IBirStatement[] { cleanup }, default, finallyOutput);
            var protectedTry = finallyOutput.OfType<BirTry>().Single();
            var returnStore = protectedTry.Body.Value.OfType<BirSetLocal>().First();
            Check(ReferenceEquals(returnStore.Value.Value, returned)
                && finallyOutput.Count(statement => ReferenceEquals(statement, cleanup)) == 1
                && Array.IndexOf(finallyOutput.ToArray(), cleanup) > finallyOutput.IndexOf(protectedTry)
                && finallyOutput.OfType<BirGoto>().Any(jump => jump.Id.Value == 900),
                "finally routing duplicated a return/cleanup operand or lost an outgoing completion");

            // A no-suspension lambda still has a real SM/create protocol. Each
            // actual donor body moves once; object ABI stores use typed casts.
            for (var arity = 0; arity <= 2; arity++)
            {
                var result = Ret(IntConst(arity));
                var parameters = Enumerable.Range(0, arity).Select(index => new BirLambdaParameter { Name = "p" + index, Type = IntTn }).ToArray();
                var lambda = new BirNewSuspendLambda { Arity = arity, Params = parameters, Captures = Array.Empty<BirCaptureDescriptor>(),
                    SuspendRet = IntTn, Body = new IBirStatement[] { result } };
                var input = new SourceFunctionInput.Lambda(file, null, lambda, "EngineLambda" + arity, [], 0, false, false);
                var built = new SourceFunGen(context, input).Build();
                var machine = (BirClassDefinition)built.Types.Single();
                var invoke = machine.Methods.Value.Single(candidate => candidate.Name.Value == "invokeSuspend");
                var create = machine.Methods.Value.Single(candidate => candidate.Name.Value == "create");
                Check(lambda.Body.Value.Length == 0 && ReferenceEquals(invoke.Body.Value[1], result)
                    && machine.Fields.Value.Select(field => field.Name.Value).SequenceEqual(
                        new[] { StateLabelField }.Concat(parameters.Select(parameter => parameter.Name.Value)))
                    && create.Params.Value.Length == (arity == 0 ? 1 : 2)
                    && create.PendingOverrideReturn.HasValue && machine.Ctors.Value[0].ThisArgs.IsPresent
                    && !machine.Ctors.Value[0].ThisArgs.HasValue,
                    "lambda body move, field order, create arity, pending physical slot or explicit null changed");
                if (arity > 0) Check(create.Body.Value.OfType<BirSetField>().Count() == arity
                    && create.Body.Value.OfType<BirSetField>().All(store => store.Value.Value is BirCast),
                    "lambda object ABI parameter stores lost their declared storage casts");
            }

            var methodVariable = new TypeNode.Tv("method", 1);
            var donor = new BirVariable { Name = "donor", Type = methodVariable };
            var captureValue = new BirDefaultValue { Type = methodVariable };
            var frame = new BirNewSuspendLambda { _suspendDeclarationTypeArgs = new TypeNode[] { methodVariable },
                TypeArgs = new TypeNode[] { methodVariable }, CapValues = new IBirExpression[] { captureValue },
                Body = new IBirStatement[] { donor } };
            RebindMethodTypeVariablesToSm(frame, 2);
            Check(frame.TypeArgs.Value[0] is TypeNode.Tv { Scope: "type", I: 3 }
                && captureValue.Type.Value is TypeNode.Tv { Scope: "type", I: 3 }
                && ReferenceEquals(frame.Body.Value[0], donor) && donor.Type.Value == methodVariable
                && frame._suspendDeclarationTypeArgs.Value[0] == methodVariable,
                "SM frame rebind changed a donor declaration instead of construction-only fields");

            var parameter = new BirLambdaParameter { Name = "native", Type = AnyTn };
            var use = Local("native");
            var native = new BirCallStatic { Owner = new TypeNode.Fqn("Native"), Method = "Write",
                Sig = new TypeNode[] { new TypeNode.ByRef(IntTn) }, Args = new IBirExpression[] { new BirByRefOf { Inner = use } } };
            var nativeBody = new List<IBirStatement> { new BirExpressionStatement { Expr = native } };
            TypedPhysicalValueCoercion.AllocateNativeLocals(nativeBody.Cast<BirGraphValue>(),
                new BirGraphValue[] { parameter }, nativeBody);
            Check(parameter.Type.Value == AnyTn && nativeBody[0] is BirVariable { Name.Value: "$nativeArgument$0", Type.Value: var nativeType,
                Init.Value: BirLocal { Name.Value: "native" } } && nativeType == IntTn
                && ReferenceEquals(((BirByRefOf)native.Args.Value[0]).Inner.Value, use) && use.Name.Value == "$nativeArgument$0",
                "actual lambda parameter storage was fabricated, cloned or selected by another allocator");
            Console.WriteLine("[typed suspend engine] self-test OK (capture frames, owning normalization, finally routes, SM/create ABI, shared native storage)");
        }
    }
}
