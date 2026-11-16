using System.Collections.Generic;
using System.Threading;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// TOP-LEVEL `repeat(n, <action>)` INLINE LOOP — the NON-LITERAL-lambda fallback (#73 M7 / #75). A LITERAL-lambda
// `repeat(n){…}` is now spliced by kotc into a `callInline` node and lowered by InlineSplice (which honors a non-local
// `return`); this pass handles only the residual shape where the action is NOT a lambda literal — e.g. `repeat(n, ::fn)`,
// a callable reference kotc emits as a `callStatic owner:null method:repeat args:[<n>, <action>]` with the action as a
// `newClosure`/`newDelegate`. This @InlineOnly stdlib helper has NO rt.dll body, so bir2cir re-emits the counted loop
// (n evaluated ONCE, index 0..n-1) that invokes the action once per iteration:
//
//   repeat(n) { i -> body }  ->  { var __repc = <action>; repeatInline __rep in 0..<n>-1 { __repc.invoke(__rep) } }
//
// The action is loop-INVOKED as a delegate (`delegateInvoke`), which is SHAPE-AGNOSTIC over kotc's two lambda-arg
// forms — a capturing `newClosure` (body inline in `synthClass`, capture reads already rewritten to `this.<field>`) and
// a non-capturing `newDelegate` (body hoisted into a lifted static method). Both ARE delegate values, so invoking the
// value sidesteps splicing either body. The action is hoisted into a `var` so the closure/delegate is allocated ONCE,
// before the loop; `times` is hoisted into a preceding `var` so it is evaluated once and BEFORE the action (Kotlin
// arg-0-before-arg-1 order); `repeatInline` (ilemit Emitter.Statements/Expressions) re-reads that local.
//
// LIMITATION (recorded in docs/dotkt-semantics.md): because the action is invoked as a delegate rather than spliced,
// a NON-LOCAL `return` through this fallback does NOT return from the enclosing function. This only affects the
// non-literal (callable-reference) action; a literal-lambda `repeat{…}` splices via InlineSplice and DOES honor a
// non-local return. A non-literal argument to `repeat` is only a callable reference (its param is not `noinline`).
//
// The action's function type is read off the call's `sig[1]` (the declared `(Int)->Unit` — always concrete for repeat),
// used both as the hoist var's type and the delegateInvoke `funcType`. Gate: `owner:null method:repeat`, 2 args, and
// `sig[1]` a function type (so `CharSequence.repeat(n): String` — a member call carrying ownerType — never matches).
// A user top-level `fun repeat(...)` shadow (app build) is skipped via localTopLevelFns.
//
// Runs BEFORE ClosureSynthesis so the action's `newClosure` (moved into the hoist var's init) is synthesized into a
// closure class exactly once there, and before MemberCallSubstitution (the bodiless @InlineOnly `repeat` never binds).
// Unconditional (ref + rt + app).
static class RepeatInlineLowering
{
    static int _counter;

    public static void Apply(BirFileDeclaration root, ISet<string> localTopLevelFns, bool appBuild)
    {
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node is BirCallStatic call ? Rewrite(call, localTopLevelFns, appBuild) : node;
        }
        Walk(root);
    }

    static BirGraphValue Rewrite(BirCallStatic call, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (call.Method.OrDefault() != "repeat" || call.OwnerType.IsPresent || !call.Owner.IsNull
            || call.Prop.IsPresent || appBuild && localTopLevelFns.Contains("repeat")) return call;
        if (call.Args.OrDefault() is not { Length: 2 } args
            || call.Sig.OrDefault() is not { Length: >= 2 } signature || signature[1] is not TypeNode.Fn) return call;

        var id = "$" + Interlocked.Increment(ref _counter);
        var countName = "__repn" + id;
        var actionName = "__repc" + id;
        var indexName = "__rep" + id;
        return new BirValueBlock {
            Stmts = new IBirStatement[] {
                new BirVariable {
                    Name = countName, Type = BirGraphValue.CopyType(signature[0]),
                    Init = OptionalField<IBirExpression>.Of(Copy(args[0])),
                },
                new BirVariable {
                    Name = actionName, Type = BirGraphValue.CopyType(signature[1]),
                    Init = OptionalField<IBirExpression>.Of(Copy(args[1])),
                },
            },
            Result = OptionalField<IBirExpression>.Of(new BirRepeat {
                Var = indexName, Count = OptionalField<IBirExpression>.Of(new BirLocal { Name = countName }),
                Body = new IBirStatement[] {
                    new BirExpressionStatement {
                        Expr = OptionalField<IBirExpression>.Of(new BirDelegateInvoke {
                            FuncType = BirGraphValue.CopyType(signature[1]),
                            Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = actionName }),
                            Args = new IBirExpression[] { new BirLocal { Name = indexName } },
                        }),
                    },
                },
            }),
        };
    }

    static IBirExpression Copy(IBirExpression value) => (IBirExpression)((BirGraphValue)value).DeepClone();
}
