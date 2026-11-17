using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal static void CallsSourceSelfTest()
    {
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        using var callSignatures = new ReferencedCallSignatures();
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var any = new TypeNode.Fqn("kotlin.Any");
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed suspend calls: " + message); }
        BirFunctionDeclaration Caller(IBirExpression expression) => new() {
            Name = "caller", Mods = new BirModifiers { Suspend = true }, SuspendRet = text, SuspendResult = text,
            Ret = text, Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { new BirReturn { Value = SourceOperandField(expression) } },
        };
        SuspendModuleContext Context(BirFileDeclaration file, SourceDeclarationSignatures signatures = null) => new(
            new[] { file }, references, new HashSet<string> { ContinuationImplFqn, "kotlin.Result" }, false,
            new Dictionary<string, string> { ["kotlin.coroutines.Continuation"] = "ContinuationCarrier" },
            _ => false, signatures, null, null, new DeclarationTestTransitions(), callSignatures.ProjectColdCall);

        var methodParameter = new BirTypeParameter { Name = "M" };
        var selected = new BirCallInstance { OwnerType = new TypeNode.Fqn("Base"), Method = "work", SuspendCall = true,
            Recv = new BirThis(), Virtual = false, Super = true, DeclarationId = "selected-work", Sty = text, Ret = any,
            Args = new IBirExpression[] { new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("value") } },
            Sig = new TypeNode[] { any }, CalleeParams = new TypeNode[] { text }, TypeArgs = new TypeNode[] { integer },
            MemberMethodTypeParams = new[] { methodParameter }, _exactExistentialBridgeOwnerCall = true, ClrOwnerResolved = true };
        var caller = Caller(selected);
        var owner = new BirClassDefinition { Name = "Caller", Kind = "class", Methods = new[] { caller } };
        var file = new BirFileDeclaration { FileClass = "CallerKt", Types = new IBirTypeDeclaration[] { owner } };
        var module = Context(file);
        module.PrepareStage0();
        var retainedBody = caller.Body.Value;
        var retainedStatement = retainedBody.Single();
        var output = new SourceFunGen(module, new SourceFunctionInput.Named(file, owner, caller, "", [])).Build();
        var calls = new List<BirCallInstance>();
        ((BirGraphValue)output.Types.Single()).Visit(node => { if (node is BirCallInstance call) calls.Add(call); });
        var cold = calls.Single(call => call.Method.OrDefault() == "work$dotkt_suspend");
        Require(cold.DeclarationId.Value == "selected-work|cold" && cold.Super.Value && !cold.Virtual.Value
            && cold.ClrOwnerResolved.Value && cold._exactExistentialBridgeOwnerCall.Value,
            "selected declaration/nonvirtual exact bridge dispatch was not carried to its cold call");
        Require(cold.Sig.Value.Length == 2 && cold.Sig.Value[0] == text && cold.CalleeParams.Value[0] == text
            && !ReferenceEquals(cold.Sig.Value, cold.CalleeParams.Value)
            && cold.MemberMethodTypeParams.Value[0].Name.Value == "M"
            && !ReferenceEquals(cold.MemberMethodTypeParams.Value[0], methodParameter),
            "selected physical signature/frame snapshot lost completion or independent metadata");
        var machine = (BirClassDefinition)output.Types.Single();
        Require(machine.Fields.Value.Single(field => field.Name.Value == "__aw$1").Type.Value == text,
            "awaited instantiated result type changed");
        // This fixture emits ContinuationImpl locally, so the runtime original
        // remains live and AcquireBody must report an independent executable birth.
        Require(ReferenceEquals(caller.Body.Value, retainedBody)
            && ReferenceEquals(caller.Body.Value.Single(), retainedStatement)
            && ReferenceEquals(((BirReturn)retainedStatement).Value.Value, selected),
            "retained runtime source body was moved or mutated");
        Require(((DeclarationTestTransitions)module.Transitions).Cloned.Any(node => ReferenceEquals(node, retainedStatement))
            && !ReferenceEquals(cold, selected),
            "retained runtime body did not use a reported independent executable copy");

        // Real core-library awaitable metadata supplies the pattern. The Kotlin
        // Result helper is an actual local declaration captured by the same source
        // signature authority, not a fabricated reflected MethodDef.
        var task = new TypeNode.Fqn("System.Threading.Tasks.Task", new TypeNode[] { integer });
        var awaitMarker = new BirCallInstance { OwnerType = task, Method = "await", SuspendCall = true, ClrAwaitBridge = true,
            AwaitResult = new TypeNode.Tv("type", 0), Sty = integer, Ret = integer,
            Recv = new BirLocal { Name = "task", Sty = task }, Args = new IBirExpression[] {
                new BirCallStatic { Method = "captureContext", SuspendCall = true, Ret = BoolTn, Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>() },
            } };
        caller = Caller(awaitMarker); caller.SuspendRet = integer; caller.SuspendResult = integer; caller.Ret = integer;
        caller.Params = new[] { new BirParameter { Name = "task", Type = task } };
        owner = new BirClassDefinition { Name = "Caller", Kind = "class", Methods = new[] { caller } };
        var resultVariable = new TypeNode.Tv("method", 0);
        var helper = new BirFunctionDeclaration { Name = "success", Static = true, DeclarationId = "result-success",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "value", Type = resultVariable } },
            Ret = new TypeNode.Fqn("kotlin.Result", new TypeNode[] { resultVariable }), Body = Array.Empty<IBirStatement>() };
        file = new BirFileDeclaration { FileClass = "CallerKt", Types = new IBirTypeDeclaration[] {
            owner, new BirClassDefinition { Name = "kotlin.Result", Kind = "class", Methods = new[] { helper } },
        }, Methods = new[] { new BirFunctionDeclaration { Name = "captureContext", Static = true,
            Mods = new BirModifiers { Suspend = true }, Params = Array.Empty<BirParameter>(), Ret = BoolTn,
            SuspendRet = BoolTn, SuspendResult = BoolTn, Body = Array.Empty<IBirStatement>() } } };
        var signatures = new SourceDeclarationSignatures();
        file = signatures.Capture(new[] { file })[0];
        callSignatures.Capture(file);
        owner = (BirClassDefinition)file.Types.Value[0]; caller = owner.Methods.Value[0];
        module = Context(file, signatures); module.PrepareStage0();
        output = new SourceFunGen(module, new SourceFunctionInput.Named(file, owner, caller, "", [])).Build();
        machine = (BirClassDefinition)output.Types.Single();
        BirCallStatic configuredCold = null;
        machine.Visit(node => {
            if (node is BirCallStatic call && call.Method.OrDefault() == "captureContext$dotkt_suspend") configuredCold = call;
        });
        Require(configuredCold != null && callSignatures.Read(configuredCold) is [var completion]
            && TypeNodeDataEquality.Equal(completion, ContAnyTn),
            "cold call synthesis bypassed the module's pre-erasure signature projection capability");
        Require(TypeNodeDataEquality.Equal(machine.Fields.Value.Single(field => field.Name.Value == "__awaitable$1").Type.Value, task)
            && machine.Fields.Value.Single(field => field.Name.Value == "__awval$2").Type.Value == integer,
            "captureContext suspension lost the earlier awaitable or closed result type");
        var native = new List<BirNativeInstance>();
        ((BirGraphValue)machine).Visit(node => { if (node is BirNativeInstance call) native.Add(call); });
        Require(native.Count(call => call.Method.OrDefault() == "ConfigureAwait") == 1
            && native.Count(call => call.Method.OrDefault() == "OnCompleted") == 1
            && native.Count(call => call.Method.OrDefault() == "GetResult") == 1
            && machine.Methods.Value.Count(method => method.Name.OrDefault() == "$awaitOnDone$2") == 1,
            "await pattern/callback emission duplicated or omitted a physical call");
        var callback = machine.Methods.Value.Single(method => method.Name.OrDefault() == "$awaitOnDone$2");
        BirCallStatic success = null;
        callback.Visit(node => { if (node is BirCallStatic call && call.Method.OrDefault() == "success") success = call; });
        Require(success?.DeclarationId.OrDefault() == "result-success" && success.TypeArgs.Value.Length == 1,
            "await callback bypassed the shared selected authored-helper authority");

        var captured = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(9) };
        var originalReturn = new BirReturn { Value = new BirFieldGet { OwnerType = new TypeNode.Fqn("IntrinsicBlock"),
            Recv = new BirThis(), Name = "captured", Ret = new TypeNode.Tv("type", 0) } };
        var invoke = new BirFunctionDeclaration { Name = "invoke", Static = false, Ret = any,
            Params = new[] { new BirParameter { Name = "continuation", Type = new TypeNode.Fqn(ContinuationFqn, new TypeNode[] { integer }) } },
            Body = new IBirStatement[] { originalReturn } };
        var closure = new BirClassDefinition { Name = "IntrinsicBlock", Kind = "class", TypeParams = new[] {
            new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { any } },
        }, Fields = new[] { new BirFieldDeclaration { Name = "captured", Type = new TypeNode.Tv("type", 0) } }, Methods = new[] { invoke } };
        var literal = new BirNewClosure { ClosureType = new TypeNode.Fqn("IntrinsicBlock"), Captures = new IBirExpression[] { captured },
            TypeArgs = new TypeNode[] { integer }, FuncType = new TypeNode.Fn(false, any,
                new TypeNode[] { new TypeNode.Fqn(ContinuationFqn, new TypeNode[] { integer }) }) };
        var intrinsic = new BirCallStatic { Owner = new TypeNode.Fqn(IntrinsicsKtFqn), Method = "suspendCoroutineUninterceptedOrReturn",
            SuspendCall = true, Ret = integer, Sty = integer, Args = new IBirExpression[] { literal } };
        caller = Caller(intrinsic); caller.SuspendRet = integer; caller.SuspendResult = integer; caller.Ret = integer;
        owner = new BirClassDefinition { Name = "Caller", Kind = "class", Methods = new[] { caller } };
        file = new BirFileDeclaration { FileClass = "CallerKt", Types = new IBirTypeDeclaration[] { owner, closure } };
        module = Context(file); module.PrepareStage0();
        output = new SourceFunGen(module, new SourceFunctionInput.Named(file, owner, caller, "", [])).Build();
        machine = (BirClassDefinition)output.Types.Single();
        Require(module.ConsumedIntrinsicClosures.Contains(closure)
            && ReferenceEquals(invoke.Body.Value[0], originalReturn)
            && ((BirFieldGet)originalReturn.Value.Value).Ret.Value is TypeNode.Tv { Scope: "type", I: 0 }
            && machine.Fields.Value.Single(field => field.Name.Value == "__aw$1").Type.Value == integer,
            "intrinsic body instantiation changed its source template or lost the selected result type");
        Require(((DeclarationTestTransitions)module.Transitions).Cloned.Contains(originalReturn)
            && ((DeclarationTestTransitions)module.Transitions).Cloned.Contains(captured),
            "intrinsic executable copies bypassed the shared independent-birth observer");
        Console.WriteLine("[typed suspend calls] self-test OK (selected signature/dispatch, result stamp, retained source independent birth, configured await metadata/order, shared helper identity)");
    }
}
