using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// PRIMITIVE OPERATOR LOWERING (#52 Phase 5): recognize the primitive value-type OPERATORS that kotc used to
// lower itself (its retired BINARY/UNARY tables) and re-emit the SAME `binOp`/`unaryOp` nodes — so ilemit is
// UNCHANGED and the CIR stays byte-identical. kotc now emits the FAITHFUL member call (`callInstance
// kotlin.Int.plus`, `kotlin.Char.unaryMinus`, `kotlin.Int.inc`); its recv/args are already value-shaped by
// kotc (recvExpr/argExpr — nullable-unwrap + boxed-Any cast). The primitive-op GATE and the IL-op selection are
// the Kotlin<->CLR relation, so they live HERE now, keyed off the pure-Kotlin owner FQN.
//
// Runs UNCONDITIONALLY (reference AND app builds) at the VERY START of the per-file pipeline, before ANY other
// pass. Two reasons: (1) the OLD kotc produced binOp/unaryOp in EVERY build, so restoring the shape first keeps
// every downstream pass (ref body-squash, type lowering, suspend) seeing the exact tree it saw before. (2) a
// primitive's operator is a bodyless builtin member with NO ref.dll symbol — in the reference build a ctor
// field-initializer / base-arg is NOT body-squashed, so a surviving `callInstance kotlin.Int.inv` would reach
// ilemit as an unresolvable method call; lowering it to `unaryOp` here (a raw IL op, no method lookup) is what
// the OLD kotc-emitted shape did.
static class PrimitiveOperatorLowering
{
    // Kotlin value-type primitives whose operators lower to raw CIL bin/un ops (the former kotc PRIMITIVE_OP_FQ
    // gate). A non-primitive kotlin.* owner (a VALUE CLASS like kotlin.time.Duration) keeps its operator as a
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
    // Value primitives whose `==` is CIL `ceq` (the former kotc PRIMITIVE_EQ_FQ — the SIGNED set + bool + char,
    // NOT the unsigned inline classes). Any other operand static type (nullable, reference, unsigned) makes `==`
    // the null-safe `Object.Equals`. bir2cir reads the EQEQ intrinsic's `argTypes` off this set.
    static readonly HashSet<string> PrimitiveEqFq = new(StringComparer.Ordinal)
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
        "kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char",
    };

    public static void Apply(JsonNode root)
    {
        switch (root)
        {
            case JsonObject o: WalkObject(o); break;
            case JsonArray a: WalkArray(a); break;
        }
    }

    static void WalkArray(JsonArray arr)
    {
        for (var i = 0; i < arr.Count; i++)
            switch (arr[i])
            {
                case JsonObject co: WalkObject(co); if (Lower(co) is JsonNode r) arr[i] = r; break;
                case JsonArray ca: WalkArray(ca); break;
            }
    }

    // Bottom-up: rewrite a node's CHILDREN (so a nested operator operand is already lowered), THEN lower the node
    // itself — wrapping the already-lowered recv/args into the binOp/unaryOp.
    static void WalkObject(JsonObject obj)
    {
        foreach (var key in obj.Select(kv => kv.Key).ToList())
            switch (obj[key])
            {
                case JsonObject co: WalkObject(co); if (Lower(co) is JsonNode r) obj[key] = r; break;
                case JsonArray ca: WalkArray(ca); break;
            }
    }

    // A primitive-operator `callInstance` / a comparison-intrinsic `callStatic` -> the binOp/unaryOp node kotc
    // used to synthesize, else null (leave as-is).
    static JsonNode Lower(JsonObject o)
    {
        var k = (o["k"] as JsonValue)?.GetValue<string>();
        if (k == "callStatic") return LowerIntrinsic(o);
        if (k != "callInstance") return null;
        if (OwnerFqn(o["ownerType"]) is not string ownerFqn || !PrimitiveOpFq.Contains(ownerFqn)) return null;
        if ((o["method"] as JsonValue)?.GetValue<string>() is not string member) return null;
        var args = o["args"] as JsonArray ?? new JsonArray();

        if (args.Count == 1 && ArithOp.TryGetValue(member, out var aop))
        {
            var bin = new JsonObject { ["k"] = "binOp", ["op"] = aop, ["lhs"] = o["recv"]?.DeepClone(), ["rhs"] = args[0]?.DeepClone() };
            // Char arithmetic result typing: `Char.plus(Int):Char`, `Char.minus(Int):Char`, `Char.minus(Char):Int`.
            // ilemit types a bin result as its LEFT operand (Char, uint16) and promotes Char->Int in a mixed op, so a
            // bare result renders as the wrong glyph/number. Force the operator's DECLARED Kotlin return type via a
            // `conv` (Int/Char), derived from the member + the arg type in `sig` — the SAME wrap the former kotc
            // Char-arith path emitted.
            if (ownerFqn == "kotlin.Char" && (member == "plus" || member == "minus"))
            {
                var to = (member == "minus" && FirstSigIsChar(o)) ? "kotlin.Int" : "kotlin.Char";
                return new JsonObject { ["k"] = "conv", ["to"] = TypeJson.Fqn(to), ["e"] = bin };
            }
            return bin;
        }
        if (args.Count == 0 && UnaryOp.TryGetValue(member, out var uop))
            return new JsonObject { ["k"] = "unaryOp", ["op"] = uop, ["e"] = o["recv"]?.DeepClone() };
        // inc/dec (the `i++`/`i--` desugaring) -> `(recv + 1)`/`(recv - 1)`. The `const 1` is typed `kotlin.Int`
        // for EVERY primitive (matching the retired kotc literal, even for Long/Double — ilemit widens it), so the
        // CIR stays byte-identical.
        if (args.Count == 0 && (member == "inc" || member == "dec"))
            return new JsonObject
            {
                ["k"] = "binOp", ["op"] = member == "inc" ? "+" : "-",
                ["lhs"] = o["recv"]?.DeepClone(),
                ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = 1 },
            };
        return null;
    }

    // A comparison intrinsic `callStatic owner=kotlin.internal.ir` -> `{k:binOp, op:<}`, else null. The operands
    // are already value-shaped by kotc (same shaping the retired binOp had), so the CIR is byte-identical.
    static JsonNode LowerIntrinsic(JsonObject o)
    {
        if (OwnerFqn(o["owner"]) != IntrinsicOwner) return null;
        if ((o["method"] as JsonValue)?.GetValue<string>() is not string m) return null;
        var args = o["args"] as JsonArray ?? new JsonArray();
        if (args.Count == 2 && CompareOp.TryGetValue(m, out var cop))
            return new JsonObject { ["k"] = "binOp", ["op"] = cop, ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() };
        // `==` (EQEQ): structural equality split off the operands' static `argTypes` — BOTH non-null primitives
        // (PrimitiveEqFq) -> CIL `ceq` (`binOp ==`); else the null-safe `Object.Equals` (`objEq`). This reproduces
        // kotc's former isPrimitiveEqType decision exactly: a nullable operand's argType is a `{t:nullable}` wrapper
        // (not a bare Fqn), a reference operand's is a non-primitive Fqn — both fail IsPrimEq -> objEq.
        if (m == "EQEQ" && args.Count == 2)
        {
            var at = o["argTypes"] as JsonArray;
            var bothPrim = at != null && at.Count == 2 && IsPrimEq(at[0]) && IsPrimEq(at[1]);
            return bothPrim
                ? new JsonObject { ["k"] = "binOp", ["op"] = "==", ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() }
                : new JsonObject { ["k"] = "objEq", ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() };
        }
        // `===` (EQEQEQ): always identity (`ceq` = `binOp ==`).
        if (m == "EQEQEQ" && args.Count == 2)
            return new JsonObject { ["k"] = "binOp", ["op"] = "==", ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() };
        return null;
    }

    static bool IsPrimEq(JsonNode t) => TypeJson.Read(t) is TypeNode.Fqn f && PrimitiveEqFq.Contains(f.Name);

    static string OwnerFqn(JsonNode t) =>
        TypeJson.Read(t) is TypeNode.Fqn f ? ReferenceMetadataIndex.BareOwnerFqn(f.Name) : null;

    static bool FirstSigIsChar(JsonObject o) =>
        o["sig"] is JsonArray s && s.Count >= 1 && TypeJson.Read(s[0]) is TypeNode.Fqn f && f.Name == "kotlin.Char";
}
