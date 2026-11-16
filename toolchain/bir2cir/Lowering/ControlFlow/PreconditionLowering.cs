using System.Collections.Generic;
using System.Threading;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// PRECONDITION / ERROR FAMILY (#73 M6). kotc emits the FAITHFUL top-level call for these stdlib symbols — it no
// longer bakes the throw/condition. bir2cir SYNTHESIZES the semantics FQN-keyed, reproducing the exact CIR kotc used
// to emit at the call site:
//
//   require(cond)         -> cond ? Unit : throw IllegalArgumentException("Failed requirement")
//   check(cond)           -> cond ? Unit : throw IllegalStateException("Check failed")
//   error(msg)            -> throw IllegalStateException(msg)                     (always throws)
//   TODO() / TODO(reason) -> throw NotImplementedError("An operation is not implemented.")   (reason ignored, as kotc did)
//   requireNotNull(x)     -> { var t = x; t != null ? t : throw IllegalArgumentException("Required value was null") }
//   checkNotNull(x)       -> { var t = x; t != null ? t : throw IllegalStateException("Required value was null") }
//   noWhenBranchMatchedException (kotlin.internal.ir intrinsic) -> throw IllegalStateException("noWhenBranchMatchedException")
//
// WHY here: these are @InlineOnly helpers — their bodies are NOT in the rt.dll, so SOME layer must synthesize the
// throw/condition; per the 4-layer split that layer is bir2cir (the Kotlin<->CLR relation), not kotc. The exception
// TYPES stay bare Kotlin FQNs (`kotlin.IllegalArgumentException`/…): the IllegalArgumentException->System.ArgumentException
// / IllegalStateException->System.InvalidOperationException BCL mapping happens DOWNSTREAM off the ref.dll @ClrTypeAlias
// (MemberCallSubstitution.TransformNew). NotImplementedError is a real emitted Kotlin exception (not aliased).
//
// require/check `requireNotNull`/`checkNotNull` value-nullable awareness: the concrete `T` is on the call's `typeArgs`
// (e.g. `requireNotNull(s: String?)` -> `typeArgs:[kotlin.String]`, `checkNotNull(n: Int?)` -> `typeArgs:[kotlin.Int]`).
// A value-type `T` (Int/Long/…) uses the `Nullable<T>` HasValue/Value shape; a reference `T` uses object null testing.
// This split follows the CLR representation selected for the frontend-resolved `T`.
//
// The top-level helpers arrive as `callStatic owner:null method:<name>` (the `until`/`println` owner:null precedent).
// Two guards keep a user symbol from being mistaken for the helper: (1) a top-level PROPERTY accessor (`prop:` marker)
// is excluded, and (2) an app-build user top-level `fun <name>(...)` shadow is skipped via localTopLevelFns.
// require/check ADDITIONALLY gate on a Boolean first parameter, so a user `fun require(x: Foo)` is never miscompiled
// (error/TODO/requireNotNull/checkNotNull rest on guards (1)+(2) alone).
//
// Runs BEFORE ClosureSynthesis (so a discarded `require(cond){ lazyMessage }` closure — kotc drops the lazyMessage,
// hardcoding "Failed requirement", and this pass preserves that — is never synthesized into an orphan closure type)
// and before MemberCallSubstitution (which would else 0-candidate the bodiless @InlineOnly helper). Unconditional
// (ref + rt + app): a ref-build ctor field-init that calls require must still lower to the same throw/cond.
static class PreconditionLowering
{
    static int _counter;

    // The value-nullable split MUST match kotc's `nullableElem` = `makeNotNull().let { isPrimitiveType() ||
    // isUnsignedType() }` — the 8 primitives PLUS the unsigned inline-classes. Unsigned is a value type on the CLR
    // (`UInt?` = `Nullable<uint>`, #76 native-unsigned), so `requireNotNull(u: UInt?)` takes the same HasValue/Value
    // unwrap shape as `Int?` — a bare objEq pass-through would leave a `Nullable<uint>` STRUCT at the use site (#118,
    // the #56 struct-consumer issue). The `kotlin.UInt` elem lowers to `System.UInt32` downstream exactly as `kotlin.Int`.
    static readonly HashSet<string> ValueTypes = new(System.StringComparer.Ordinal)
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte", "kotlin.Char", "kotlin.Boolean",
        "kotlin.Double", "kotlin.Float",
        "kotlin.UInt", "kotlin.ULong", "kotlin.UShort", "kotlin.UByte",
    };

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
        var method = call.Method.OrDefault();
        if (method == null) return call;
        if (method is "noWhenBranchMatchedException" or "throwUninitializedPropertyAccessException"
            && call.Owner.OrDefault() is TypeNode.Fqn owner && owner.Name.StartsWith("kotlin", StringComparison.Ordinal))
            return ThrowExpr(NewExc("kotlin.IllegalStateException", method));

        // Presence matters: owner:null is a top-level helper; an absent owner,
        // member ownerType, or even a null prop marker is not that declaration.
        if (call.OwnerType.IsPresent || !call.Owner.IsNull || call.Prop.IsPresent
            || appBuild && localTopLevelFns.Contains(method)) return call;
        var args = call.Args.OrDefault();
        var signature = call.Sig.OrDefault();
        switch (method)
        {
            case "TODO":
                // Keep the current helper contract: neither reason nor lazy
                // messages are evaluated by the authored replacement.
                return ThrowExpr(NewExc("kotlin.NotImplementedError", "An operation is not implemented.", nullableMessage: false));
            case "error":
                return args is { Length: > 0 } ? ThrowExpr(NewExcExpr("kotlin.IllegalStateException", Copy(args[0]))) : call;
            case "require":
            case "check":
                if (args is not { Length: > 0 } || signature is not { Length: > 0 }
                    || signature[0] is not TypeNode.Fqn { Name: "kotlin.Boolean" }) return call;
                var (exception, message) = method == "require"
                    ? ("kotlin.IllegalArgumentException", "Failed requirement") : ("kotlin.IllegalStateException", "Check failed");
                return new BirConditional {
                    Cond = OptionalField<IBirExpression>.Of(Copy(args[0])),
                    Then = OptionalField<IBirExpression>.Of(UnitNull()),
                    Else = OptionalField<IBirExpression>.Of(ThrowExpr(NewExc(exception, message))),
                };
            case "requireNotNull":
            case "checkNotNull":
                if (args is not { Length: > 0 } || call.TypeArgs.OrDefault() is not { Length: > 0 } arguments) return call;
                var element = arguments[0];
                var exceptionType = method == "requireNotNull" ? "kotlin.IllegalArgumentException" : "kotlin.IllegalStateException";
                var name = "__rn$" + Interlocked.Increment(ref _counter);
                IBirExpression Read() => new BirLocal { Name = name };
                var variable = new BirVariable {
                    Name = name, Type = new TypeNode.Nullable(BirGraphValue.CopyType(element)),
                    Init = OptionalField<IBirExpression>.Of(Copy(args[0])),
                };
                var failure = ThrowExpr(NewExc(exceptionType, "Required value was null"));
                BirConditional condition;
                if (element is TypeNode.Fqn elem && ValueTypes.Contains(elem.Name))
                    condition = new BirConditional {
                        Cond = OptionalField<IBirExpression>.Of(new BirNullableHasValue {
                            Elem = BirGraphValue.CopyType(element), E = OptionalField<IBirExpression>.Of(Read()),
                        }),
                        Then = OptionalField<IBirExpression>.Of(new BirNullableValue {
                            Elem = BirGraphValue.CopyType(element), E = OptionalField<IBirExpression>.Of(Read()),
                        }),
                        Else = OptionalField<IBirExpression>.Of(failure),
                    };
                else
                    condition = new BirConditional {
                        Cond = OptionalField<IBirExpression>.Of(new BirUnaryOperation {
                            Op = "!", E = OptionalField<IBirExpression>.Of(new BirObjectEquals {
                                Lhs = OptionalField<IBirExpression>.Of(Read()), Rhs = OptionalField<IBirExpression>.Of(NullLiteral()),
                            }),
                        }),
                        Then = OptionalField<IBirExpression>.Of(Read()),
                        Else = OptionalField<IBirExpression>.Of(failure),
                    };
                return new BirValueBlock { Stmts = new IBirStatement[] { variable }, Result = OptionalField<IBirExpression>.Of(condition) };
            default:
                return call;
        }
    }

    static IBirExpression Copy(IBirExpression value) => (IBirExpression)((BirGraphValue)value).DeepClone();
    static BirConstant UnitNull() => new() { Type = new TypeNode.Fqn("kotlin.Unit"), Value = new BirLiteral.NullLiteral() };
    static BirConstant NullLiteral() => new() {
        Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Nothing")), Value = new BirLiteral.NullLiteral(),
    };
    static BirNew NewExc(string type, string message, bool nullableMessage = true) => new() {
        Type = new TypeNode.Fqn(type),
        ArgTypes = new TypeNode[] { nullableMessage ? new TypeNode.Nullable(new TypeNode.Fqn("kotlin.String")) : new TypeNode.Fqn("kotlin.String") },
        Args = new IBirExpression[] { new BirConstant { Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral(message) } },
    };
    static BirNew NewExcExpr(string type, IBirExpression message) => new() {
        Type = new TypeNode.Fqn(type),
        ArgTypes = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.String")) }, Args = new[] { message },
    };
    static BirThrowExpression ThrowExpr(IBirExpression exception) => new() { Value = OptionalField<IBirExpression>.Of(exception) };
}
