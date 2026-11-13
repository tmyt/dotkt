using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A `kotlin.reflect.KClass` member read (`T::class.simpleName` / `.qualifiedName`) -> the Kotlin name.
// kotc emits the pure-Kotlin property read `callInstance(kotlin.reflect.KClass[..].get_simpleName, recv = <::class>)`.
// The `::class` receiver is a System.Type token, emitted by kotc as either:
//   • `classRef`  — an UNBOUND class literal on a type (`Int::class`, `Foo::class`, `Box::class`, a reified `T::class`
//     after inline-splice substitution): `{"k":"classRef","type":<Kotlin FQN>}` = a static `typeof`.
//   • `getType`   — a BOUND class reference on a value (`1::class`, `"x"::class`, `x::class`): `{"k":"getType","e":<expr>}`
//     = a run-time `<expr>.GetType()`, reflecting the value's RUNTIME class.
// Native class literals are System.Type values; ordinary KClass implementations remain supported. This pass owns
// the statically-known Kotlin<->CLR NAME reversal (#138). Because it runs
// BEFORE BirTypeLowering, every `type`/`sty` slot is still a pure Kotlin FQN (`kotlin.Int`, `kotlin.String`, a user
// FQN), so where the Kotlin type identity is statically known it CONST-FOLDS the accessor straight off that token —
// `qualifiedName` = the FQN verbatim, `simpleName` = its last `.`-segment. No CLR->Kotlin reverse TABLE is needed: the
// Kotlin identity is still in hand. Foldable cases:
//   • classRef — ALWAYS: the token IS the literal type (`Int::class` -> "Int"/"kotlin.Int", `Box::class` -> "Box").
//   • getType — when the argument's static type is a KNOWN-FINAL builtin (the primitive tower, String, the specialized
//     arrays, Unit/Nothing) AND folding the argument away drops no evaluation (Q2, `ValueStability.IsDroppable`: a
//     `const`/`local`/`this`/plan read, NOT a wrapper/call — see below). A final type has no subtypes, so the RUNTIME
//     class == the static type;
//     `1::class` -> "Int"/"kotlin.Int", `"x"::class` -> "String"/"kotlin.String" — the reported failures. (Not folding a
//     side-effecting `foo()::class` receiver keeps `foo()`'s evaluation.)
// Everything else keeps its semantic member read for the declaration-owned runtime adapter:
//   • a BOUND `getType` on an OPEN/interface static type (`x: Any`, `list: List<Int>`) — the runtime class is a subtype,
//     so the name is genuinely dynamic. Closing that (CLR-renamed builtins + generic backtick mangling surfacing at run
//     time) needs a run-time CLR->Kotlin reverse-map HELPER (a stdlib runtime fn this pass would route to) — §5g of
//     docs/dotkt-semantics.md, a sequenced cross-layer stdlib follow-up, NOT a bir2cir-only const-fold.
//   • a BOUND `getType` on a FINAL USER class — for a NON-GENERIC, TOP-LEVEL user type `Type.FullName`/`.Name` already
//     ARE the Kotlin qualified/simple name (no CLR rename), so the run-time read is correct. A GENERIC user class reads
//     back backtick-mangled (`Box`1`) and a NESTED one uses the CLR `+` separator (`Outer+Inner`); those two shapes join
//     the §5g dynamic follow-up (an UNBOUND `Box::class`/`Outer.Inner::class` is already exact via the classRef fold).
//   • a BOUND `getType` whose receiver is a SMART-CAST wrapper (`cast`/`nullableValue`) — not folded (a `cast` node
//     also carries a throwing explicit `as`, so dropping it would drop the throw); §5g dynamic follow-up.
//   • a BOUND `getType` on a `classRef` — the DOUBLE class literal `(Int::class)::class`, whose receiver is a KClass,
//     not an Int. A classRef's type slot names the REFERENCED type, so reading it as the value's type would fold to
//     the wrong name. Routed to the run-time read like any other KClass-typed receiver.
// Non-ref only. Replace owning expression slots while declaration/ownership indices keep their existing nodes.
static class KClassMemberBinding
{
    // Kotlin builtin types that are FINAL (no subtypes), so a bound `value::class` on one is statically resolvable
    // (runtime class == static type) and const-folding to the Kotlin name is sound. Most are also CLR-RENAMED (the
    // primitive tower -> System.Int32/…, String -> System.String, the specialized arrays -> int[]/…), so the fold is
    // what restores the Kotlin name; Unit/Nothing keep their Kotlin name but are included so a `Unit` local's `::class`
    // folds uniformly. (`kotlin.Array<T>` is a distinct `{t:array}` token, not an Fqn — its bound case is a follow-up.)
    static readonly HashSet<string> KnownFinal = new()
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
        "kotlin.UInt", "kotlin.ULong", "kotlin.UShort", "kotlin.UByte",
        "kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char",
        "kotlin.String", "kotlin.Unit", "kotlin.Nothing",
        "kotlin.IntArray", "kotlin.LongArray", "kotlin.ShortArray", "kotlin.ByteArray",
        "kotlin.DoubleArray", "kotlin.FloatArray", "kotlin.BooleanArray", "kotlin.CharArray",
        "kotlin.UIntArray", "kotlin.ULongArray", "kotlin.UShortArray", "kotlin.UByteArray",
    };

    public static void Apply(BirFileDeclaration root)
    {
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            if (node is not BirCallInstance call || call.OwnerType.OrDefault() is not TypeNode.Fqn { Name: "kotlin.reflect.KClass" }
                || call.Prop.OrDefault() != "get" || call.Method.OrDefault() is not ("simpleName" or "qualifiedName")
                || call.Recv.OrDefault() is not IBirExpression receiver || StaticKotlinFqn(receiver) is not string name)
                return node;
            return new BirConstant { Type = new TypeNode.Fqn("kotlin.String"),
                Value = new BirLiteral.TextLiteral(call.Method.Value == "simpleName" ? SimpleOf(name) : name) };
        }
        Walk(root);
    }

    // The statically-known Kotlin FQN a `::class` receiver names, or null when the class is only known at run time.
    static string StaticKotlinFqn(IBirExpression recv) => recv switch
    {
        // Unbound class literal — the token IS the literal type; always resolvable (finality is irrelevant, it is a type,
        // not a reflected instance).
        BirClassReference value => FqnOf(value.Type.OrDefault()),
        // Bound `value::class` — resolvable iff the value's static type is a known-final builtin (runtime class == static
        // type) AND folding the receiver away drops no evaluation. That last test is Q2 of the value questions
        // (bir-common/ValueStability.cs), asked here for the same reason CallEvalLowering asks it of a binding nothing
        // reads: this fold makes the receiver unread, so it may only be removed where evaluating it is unobservable.
        BirRuntimeClass value when value.E.OrDefault() is BirGraphNode e && ValueStability.IsDroppable(e.Tag)
            && e is not BirClassReference
            && FqnOf(e.ResultTypeStamp ?? e.ExplicitSourceType) is string st && KnownFinal.Contains(st) => st,
        _ => null,
    };

    // Does this node's `sty`/`type` slot name the type of the VALUE it produces? For almost every kind it does, which
    // is what lets the bound-`::class` fold above read the receiver's static type straight off it. `classRef` is the
    // exception and the reason this test exists: its `type` is the type the token REFERS TO, not the type it IS
    // (kotc emits `{"k":"classRef","type":<the referenced type>}`, BirEmitterExpressions `IrClassReference`) — the
    // value is a `System.Type`. Reading it as the value's type folds `(Int::class)::class.simpleName` to "Int",
    // which is the name of the class the token names, not of the token. The receiver there is a `KClass`.
    // The Kotlin FQN a type slot names — the bare Fqn, or a nullable-of-Fqn unwrapped to its core (an inline-spliced
    // `T::class` with a nullable T arg, or a `String?` receiver; the Kotlin answer is the non-null identity's name).
    // Null for any other token shape (tv/array/fn), which the caller routes to the run-time read instead of folding.
    static string FqnOf(TypeNode typeNode) => typeNode switch
    {
        TypeNode.Fqn f => f.Name,
        TypeNode.Nullable { Of: TypeNode.Fqn f } => f.Name,
        _ => null,
    };

    // simpleName = the segment after the last `.` (a nested class's `Outer.Inner` -> `Inner`); the whole name otherwise.
    static string SimpleOf(string fqn)
    {
        int dot = fqn.LastIndexOf('.');
        return dot >= 0 ? fqn.Substring(dot + 1) : fqn;
    }

    internal static void SelfTest()
    {
        BirCallInstance Name(IBirExpression receiver) => new() { OwnerType = new TypeNode.Fqn("kotlin.reflect.KClass"),
            Method = "simpleName", Prop = "get", Recv = OptionalField<IBirExpression>.Of(receiver) };
        var effect = Name(new BirRuntimeClass { E = new BirCallStatic { Method = "effect", Sty = new TypeNode.Fqn("kotlin.String") } });
        var token = Name(new BirRuntimeClass { E = new BirClassReference { Type = new TypeNode.Fqn("kotlin.Int") } });
        var statements = new[] {
            new BirExpressionStatement { Expr = Name(new BirClassReference { Type = new TypeNode.Nullable(new TypeNode.Fqn("Probe.Outer.Inner")) }) },
            new BirExpressionStatement { Expr = Name(new BirRuntimeClass { E = new BirLocal { Name = "n", Sty = new TypeNode.Fqn("kotlin.Int") } }) },
            new BirExpressionStatement { Expr = effect }, new BirExpressionStatement { Expr = token },
        };
        var method = new BirFunctionDeclaration { Name = "use", Body = statements.Cast<IBirStatement>().ToArray() };
        var root = new BirFileDeclaration { Methods = new[] { method } };
        Apply(root);
        if (!ReferenceEquals(root.Methods.Value[0], method)
            || statements[0].Expr.Value is not BirConstant { Value.Value: BirLiteral.TextLiteral { Value: "Inner" } }
            || statements[1].Expr.Value is not BirConstant { Value.Value: BirLiteral.TextLiteral { Value: "Int" } }
            || !ReferenceEquals(statements[2].Expr.Value, effect) || !ReferenceEquals(statements[3].Expr.Value, token))
            throw new InvalidOperationException("Typed KClass fold lost identity, Kotlin names, receiver evaluation or token type semantics");
    }
}
