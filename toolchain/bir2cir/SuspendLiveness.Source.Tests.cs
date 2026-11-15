using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendLiveness
{
    internal static void SourceSelfTest()
    {
        static void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Typed suspend liveness: " + reason); }
        static BirLocal Read(string name) => new() { Name = name };
        static BirConstant Number() => new() { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(1) };
        static BirConstant Boolean() => new() { Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(true) };
        static BirVariable Declare(string name) => new() { Name = name, Type = new TypeNode.Fqn("kotlin.Int"), Init = Number() };
        static BirCallStatic Suspend(string name) => new() { Owner = new TypeNode.Fqn("Tests.Suspend"), Method = name,
            SuspendCall = true, Ret = new TypeNode.Fqn("kotlin.Int"), Args = Array.Empty<IBirExpression>() };
        static BirExpressionStatement Eval(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        static BirSetLocal Set(string name, IBirExpression value) => new() { Name = name, Value = OptionalField<IBirExpression>.Of(value) };

        // Field minting consumes the original declaration order/type/role. The
        // verdict is path liveness, not a lexical declared-before/read-after span.
        var first = Declare("live"); first.Role = "the receiver of `read`";
        var dead = Declare("dead");
        IBirStatement[] body = { first, dead, Eval(Read("dead")), Eval(Suspend("first")), Eval(Read("live")),
            Set("live", Number()), Eval(Suspend("second")), Eval(Read("live")) };
        var original = body.ToArray();
        var result = Analyze(body);
        Check(result.DeclaredVars.Select(variable => variable.Name).SequenceEqual(new[] { "live", "dead" })
            && ReferenceEquals(result.DeclaredVars[0].Type, first.Type.Value)
            && result.DeclaredVars[0].Role == first.Role.Value
            && result.LivesAcrossSuspension("live") && result.FirstSuspensionAcross("live") == "first"
            && !result.LivesAcrossSuspension("dead") && result.FirstSuspensionAcross("dead") == null
            && !result.LivesAcrossSuspension(null) && result.FirstSuspensionAcross(null) == null
            && body.SequenceEqual(original, ReferenceEqualityComparer.Instance), "declaration facts/order or first live suspension changed");

        var flat = Analyze(new IBirStatement[] { Declare("iteration"), Declare("carried"), new BirLabel { Id = 10 },
            Set("iteration", Number()), Eval(Read("iteration")), Eval(Suspend("loop")), Eval(Read("carried")),
            new BirBranchIf { Id = 10, Cond = Boolean() } });
        Check(!flat.LivesAcrossSuspension("iteration") && flat.FirstSuspensionAcross("carried") == "loop",
            "a normalized backedge confused per-iteration values with carried values");

        var deferred = Analyze(new IBirStatement[] { Declare("operand"),
            Eval(new BirBinaryOperation { Op = "+", Lhs = Read("operand"), Rhs = Suspend("rhs") }) });
        Check(deferred.FirstSuspensionAcross("operand") == "rhs", "the residual post-resume expression lost its local read");
        var nested = Analyze(new IBirStatement[] { Declare("argument"), Eval(new BirCallStatic {
            Owner = new TypeNode.Fqn("Tests.Suspend"), Method = "outer", Ret = new TypeNode.Fqn("kotlin.Int"), SuspendCall = true,
            Args = new IBirExpression[] { Read("argument"), Suspend("inner") } }) });
        Check(nested.FirstSuspensionAcross("argument") == "inner", "nested suspension operands were consumed on the wrong side of resume");

        var branches = Analyze(new IBirStatement[] { Declare("branch"), Eval(Suspend("beforeBranch")),
            new BirIf { Branches = new[] {
                new BirBranch { Cond = Boolean(), Body = new IBirStatement[] { Set("branch", Number()) } },
                new BirBranch { Else = true, Body = new IBirStatement[] { Eval(Read("branch")) } },
            } } });
        Check(branches.FirstSuspensionAcross("branch") == "beforeBranch", "one branch's write killed another branch's read");
        var stopped = Analyze(new IBirStatement[] { Declare("unreachable"), Eval(Suspend("stop")),
            new BirReturn { Value = Number() }, Eval(Read("unreachable")) });
        Check(!stopped.LivesAcrossSuspension("unreachable"), "return control flow fell through into unreachable reads");
        var jump = Analyze(new IBirStatement[] { Declare("jumped"), Eval(Suspend("beforeJump")),
            Eval(new BirValueBlock { Stmts = new IBirStatement[] { new BirGoto { Id = 77 }, Set("jumped", Number()) }, Result = Number() }),
            new BirLabel { Id = 77 }, Eval(Read("jumped")) });
        Check(jump.FirstSuspensionAcross("jumped") == "beforeJump", "a nested expression goto lost its actual CFG target");

        var exceptional = Analyze(new IBirStatement[] { Declare("assignment"), Declare("finallyValue"),
            new BirTry { Body = new IBirStatement[] { Set("assignment", Suspend("assign")) },
                Catches = new[] { new BirCatch { Var = "assignment", ExcType = new TypeNode.Fqn("kotlin.Throwable"),
                    Body = new IBirStatement[] { Eval(Read("assignment")) } } },
                Finally = new IBirStatement[] { Eval(Read("finallyValue")) } } });
        Check(exceptional.FirstSuspensionAcross("assignment") == "assign"
            && exceptional.FirstSuspensionAcross("finallyValue") == "assign", "an exceptional predecessor was killed by a write or colliding catch binder");
        var eventless = Analyze(new IBirStatement[] { Declare("handlerValue"), Eval(Suspend("beforeTry")),
            new BirTry { Body = new IBirStatement[] { Eval(new BirCallStatic { Owner = new TypeNode.Fqn("Tests.Ordinary"),
                Method = "eventless", Ret = new TypeNode.Fqn("kotlin.Unit"), Args = Array.Empty<IBirExpression>() }) },
                Catches = new[] { new BirCatch { Var = "error", ExcType = new TypeNode.Fqn("kotlin.Throwable"),
                    Body = new IBirStatement[] { Eval(Read("handlerValue")) } } } } });
        Check(eventless.FirstSuspensionAcross("handlerValue") == "beforeTry", "an eventless protected region lost its handler edge");

        var lambda = new BirNewSuspendLambda { Arity = 0,
            Captures = new[] { new BirCaptureDescriptor { Name = "overridden", Type = new TypeNode.Fqn("kotlin.Int") },
                new BirCaptureDescriptor { Name = "captured", Type = new TypeNode.Fqn("kotlin.Int") } },
            CapValues = new IBirExpression[] { new BirValueBlock { Stmts = new IBirStatement[] { Declare("captureLocal") },
                Result = Suspend("capture") }, null },
            Params = Array.Empty<BirLambdaParameter>(), SuspendRet = new TypeNode.Fqn("kotlin.Int"),
            Body = new IBirStatement[] { Declare("foreignLocal"), Eval(Suspend("foreign")), Eval(Read("overridden")) } };
        var captured = Analyze(new IBirStatement[] { Declare("overridden"), Declare("captured"), Eval(lambda) });
        Check(captured.DeclaredVars.Select(variable => variable.Name).SequenceEqual(new[] { "overridden", "captured", "captureLocal" })
            && captured.FirstSuspensionAcross("captured") == "capture" && !captured.LivesAcrossSuspension("overridden")
            && SourceHasSuspension(lambda, true) && !SourceHasSuspension(lambda, false),
            "other-frame bodies, overridden capture slots or capture declaration order leaked into enclosing liveness");
        var closure = Analyze(new IBirStatement[] { Declare("deferredCapture"), Eval(new BirNewClosure {
            ClosureType = new TypeNode.Fqn("Tests.Closure"), Captures = new IBirExpression[] { Read("deferredCapture"), Suspend("laterCapture") } }) });
        Check(closure.FirstSuspensionAcross("deferredCapture") == "laterCapture", "capture reads were not deferred past a sibling capture suspension");

        var inline = new BirForEach { Var = "item", Elem = new TypeNode.Fqn("kotlin.Int"), Src = new BirNewArray { Elem = new TypeNode.Fqn("kotlin.Int"),
                Elems = Array.Empty<IBirExpression>() }, Body = new IBirStatement[] { Eval(Suspend("inlineLoop")) } };
        var inlineBody = Analyze(new IBirStatement[] { Declare("outside"), Eval(new BirCallStatic {
            Owner = new TypeNode.Fqn("Tests.Ordinary"), Method = "consume", Ret = new TypeNode.Fqn("kotlin.Unit"),
            Args = new IBirExpression[] { Read("outside"), inline } }) });
        Check(SourceHasSuspension(inline, true) && !SourceHasSuspension(inline, false)
            && inlineBody.FirstSuspensionAcross("outside") == "inlineLoop", "inline-loop own/wide suspension scopes diverged");
        var header = Declare("header"); header.Type = new TypeNode.Fqn("kotlin.Boolean"); header.Init = Boolean();
        // Analyze consumes the normalized body: a surviving structured loop is
        // suspension-free. Its header is needed after the preceding resume,
        // while each iteration defines its own value before the backedge read.
        var structured = Analyze(new IBirStatement[] { header, Declare("perIteration"), Eval(Suspend("structured")),
            new BirWhile { Label = "loop", Cond = Read("header"), Body = new IBirStatement[] {
                Set("perIteration", Number()), Eval(Read("perIteration")),
                new BirContinue { Label = "loop" },
            } } });
        Check(structured.FirstSuspensionAcross("header") == "structured" && !structured.LivesAcrossSuspension("perIteration"),
            "structured-loop headers/backedges or continuing-loop scope changed");

        // Concrete typed vocabulary owns the frame partition. Neither inline
        // loops nor ordinary SAM construction are a separate callable frame.
        BirGraphNode[] otherFrames = { new BirNewClosure(), new BirNewDelegate(), new BirInlineLambda(), new BirNewSuspendLambda() };
        BirGraphNode[] inlineLoops = { new BirForEach(), new BirRepeat() };
        Check(otherFrames.All(SuspendColdLowering.IsSourceOtherFrame)
            && inlineLoops.All(SuspendColdLowering.IsSourceInlineLoop)
            && !inlineLoops.Any(SuspendColdLowering.IsSourceOtherFrame)
            && !otherFrames.Any(SuspendColdLowering.IsSourceInlineLoop)
            && !SuspendColdLowering.IsSourceOtherFrame(new BirNewSam())
            && !SuspendColdLowering.IsSourceInlineLoop(new BirNewSam()), "shared concrete frame classifiers lost the existing partition");
        Console.WriteLine("[typed suspend liveness] self-test OK (declaration facts, CFG/backedges, deferred operands, exceptional edges, capture/frame scopes)");
    }
}
