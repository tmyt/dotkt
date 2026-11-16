using System;
using System.Collections.Generic;
using System.Threading;
using DotKt.Bir;

// PRIMITIVE OPERATOR LOWERING (#52 Phase 5): recognize faithful primitive value-type operator calls and realize the
// canonical CIR `binOp`/`unaryOp` nodes that ilemit emits one-to-one. Also recognizes `kotlin.String.plus` (the last
// operator-recognition residual) and realizes the CIR `concat` node for the string-`+` member call. kotc emits
// the FAITHFUL member call (`callInstance kotlin.Int.plus`, `kotlin.Char.unaryMinus`, `kotlin.Int.inc`,
// `kotlin.String.plus`); its recv/args are already value-shaped by
// kotc (recvExpr/argExpr — nullable-unwrap + boxed-Any cast). The primitive-op GATE and the IL-op selection are
// the Kotlin<->CLR relation, so they live HERE now, keyed off the pure-Kotlin owner FQN.
//
// Runs UNCONDITIONALLY (reference AND app builds) on owning source BIR at representation entry, then again on
// owning typed nodes synthesized by range and other lowerings, before ref body-squash/type/suspend consumers.
// A primitive operator is a bodyless builtin
// member with NO ref.dll symbol — in the reference build a ctor
// field-initializer / base-arg is NOT body-squashed, so a surviving `callInstance kotlin.Int.inv` would reach
// ilemit as an unresolvable method call. Lowering it to the CIR `unaryOp`/`binOp` vocabulary here states the raw CIL
// operation before the one-to-one emitter boundary.
static partial class PrimitiveOperatorLowering
{
    // Kotlin value-type primitives whose operators have raw CIL bin/un representations. A non-primitive kotlin.*
    // owner (a VALUE CLASS like kotlin.time.Duration) keeps its operator as a
    // real method call.
    static readonly HashSet<string> PrimitiveOpFq = new(StringComparer.Ordinal)
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte", "kotlin.Double", "kotlin.Float",
        "kotlin.Boolean", "kotlin.Char", "kotlin.UInt", "kotlin.ULong", "kotlin.UByte", "kotlin.UShort",
    };
    // Arithmetic + bitwise/shift member operator name -> IL binOp symbol.
    static readonly Dictionary<string, string> ArithOp = new(StringComparer.Ordinal)
    {
        ["plus"] = "+", ["minus"] = "-", ["times"] = "*", ["div"] = "/", ["rem"] = "%",
        ["and"] = "&", ["or"] = "|", ["xor"] = "^", ["shl"] = "<<", ["shr"] = ">>", ["ushr"] = ">>>",
    };
    // Unary member operator name -> IL unaryOp symbol.
    static readonly Dictionary<string, string> UnaryOp = new(StringComparer.Ordinal)
    {
        ["unaryMinus"] = "-", ["unaryPlus"] = "+", ["not"] = "!", ["inv"] = "~",
    };
    // Comparison intrinsic name -> IL comparison symbol. These are `kotlin.internal.ir` COMPILER INTRINSICS
    // (top-level, no ref.dll symbol); kotc emits them faithfully as a `callStatic owner=kotlin.internal.ir`
    // (the owner marker makes the match collision-safe vs a user top-level `less`).
    static readonly Dictionary<string, string> CompareOp = new(StringComparer.Ordinal)
    {
        ["less"] = "<", ["lessOrEqual"] = "<=", ["greater"] = ">", ["greaterOrEqual"] = ">=",
    };
    // The IR-intrinsic marker owner kotc stamps on the comparison/equality intrinsic calls.
    const string IntrinsicOwner = "kotlin.internal.ir";
    // The value primitives whose Kotlin type has a VALUE `<`/`>`/`<=`/`>=` compare (the signed integrals + Double/Float
    // + bool/char, NOT the unsigned inline classes). TypedCompareOperand reads this to detect a value-nullable operand
    // (`Int?`/`Double?` smart-cast into a compare) that must surface `Nullable<T>.Value` — Double/Float ARE included
    // here (a `Double?` compare needs the unwrap). This is a COMPARE-operand set only; the EQEQ fast-path uses the
    // narrower EqEqPrimFq (Double/Float excluded — see #95).
    static readonly HashSet<string> ComparePrimFq = new(StringComparer.Ordinal)
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
        "kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char",
    };
    // The EQEQ fast-path (a direct primitive `==` -> CIL `ceq`) EXCLUDES Double/Float. A STRUCTURAL EQEQ over two
    // Double/Float (the frontend-generated data-class `equals`) needs Kotlin's TOTAL-ORDER equality — `NaN == NaN`
    // is TRUE, `+0.0 != -0.0` — consistent with the bit-based hashCode; `ceq` gives IEEE (NaN != NaN, +0.0 == -0.0),
    // disagreeing with hashCode and breaking hashSet/data-class semantics (#95). A DIRECT float `a == b` never
    // reaches EQEQ: the frontend emits the `ieee754equals` intrinsic for it (kept IEEE `ceq` below). So dropping
    // Double/Float here reroutes ONLY the structural case to clrDoubleEquals/clrFloatEquals (the total-order helper).
    static readonly HashSet<string> EqEqPrimFq = new(StringComparer.Ordinal)
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte", "kotlin.Boolean", "kotlin.Char",
    };
    // Value primitives whose OPERATOR result is DECLARED wider-or-different than the operand's storage type. Kotlin
    // operators run at their physical stack width; PhysicalValueCoercion makes mixed-width operands explicit. Kotlin
    // DECLARES: Byte/Short arith -> Int; UByte/UShort arith -> UInt; Char.plus/minus(Int) -> Char, Char.minus(Char) ->
    // Int; inc/dec -> the receiver's own narrow type (but the `+1` desugaring runs at int width). For these owners the
    // bare op truncates on box / narrow store, so wrap it in a `conv` to the frontend-resolved return type (`dynRet`).
    // The full-width owners (Int/Long/Double/Float/UInt/ULong) are left bare — their result already matches ilemit.
    static readonly HashSet<string> NarrowResultFq = new(StringComparer.Ordinal)
    {
        "kotlin.Char", "kotlin.Byte", "kotlin.Short", "kotlin.UByte", "kotlin.UShort",
    };

    internal static void SelfTest() => TypedSelfTest();

    // The value-nullable float element FQN (`kotlin.Double`/`kotlin.Float`) iff the type is raw `Nullable<T>` over a
    // bare Double/Float — the #152 gate. A boxed-Any operand (BirGraphStaticType.Value = kotlin.Any) or a non-float nullable
    // fails, so ONLY a `Double?`/`Float?` structural EQEQ reaches the null-safe bit-equality synthesis.
    static string NullableFloatElem(TypeNode t) =>
        t is TypeNode.Nullable n && n.Of is TypeNode.Fqn { Args: null } f
            && (f.Name == "kotlin.Double" || f.Name == "kotlin.Float") ? f.Name : null;

    // Classify an `ieee754equals` operand's SURFACE type as a Double/Float family with its nullness (#180). Returns null
    // for a non-float surface type (unresolvable / not a Double/Float), so the caller keeps the raw `binOp ==` used by
    // the non-null direct path and the defensive unresolved fallback. A raw value-nullable `Double?`/`Float?`
    // -> (nullable=true, elem); a non-null `Double`/`Float` -> (nullable=false, elem).
    static (bool nullable, string elem)? FloatOperand(TypeNode t)
    {
        if (NullableFloatElem(t) is string ne) return (true, ne);
        if (FaithfulHints.IsNonNullFqn(t, "kotlin.Double")) return (false, "kotlin.Double");
        if (FaithfulHints.IsNonNullFqn(t, "kotlin.Float")) return (false, "kotlin.Float");
        return null;
    }

    // Distinct temp-name counter for the #152/#180 hoisted operands (mirrors PreconditionLowering's `__rn$`).
    static int _neCounter;
    static string FreshNullableEqualityTemporary() => "__ne$" + Interlocked.Increment(ref _neCounter);
    static string ArithmeticSymbol(string owner, string member, string symbol) =>
        member == "shr" && owner is "kotlin.UInt" or "kotlin.ULong" ? ">>>" : symbol;
    static int ShiftMask(string owner) => owner is "kotlin.Long" or "kotlin.ULong" ? 63 : 31;
}
