using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// PRIMITIVE OPERATOR LOWERING (#52 Phase 5): recognize the primitive value-type OPERATORS that kotc used to
// lower itself (its retired BINARY/UNARY tables) and re-emit the SAME `binOp`/`unaryOp` nodes — so ilemit is
// UNCHANGED and the CIR stays byte-identical. Also recognizes `kotlin.String.plus` (the last operator-recognition
// residual) and re-emits the `concat` node kotc used to synthesize (the string-`+` member call). kotc now emits
// the FAITHFUL member call (`callInstance kotlin.Int.plus`, `kotlin.Char.unaryMinus`, `kotlin.Int.inc`,
// `kotlin.String.plus`); its recv/args are already value-shaped by
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

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs = null)
    {
        StaticType.Refs = refs;
        StaticType.LocalTypes = StaticType.CollectTypes(root);
        switch (root)
        {
            case JsonObject o: WalkObject(o, BirScope.Empty); break;
            case JsonArray a: WalkArray(a, BirScope.Empty); break;
        }
    }

    static void WalkArray(JsonArray arr, BirScope scope)
    {
        // A statement sequence: a `var` enters scope for the SUBSEQUENT siblings only (lexical block scoping — so two
        // for-loops each destructuring to `v` of different element types do not collide). The mutable child is created
        // lazily, only when the array actually declares a var (args/sig arrays allocate nothing).
        var cur = scope;
        for (var i = 0; i < arr.Count; i++)
        {
            switch (arr[i])
            {
                case JsonObject co: WalkObject(co, cur); if (Lower(co, cur) is JsonNode r) arr[i] = r; break;
                case JsonArray ca: WalkArray(ca, cur); break;
            }
            if (arr[i] is JsonObject vo && (vo["k"] as JsonValue)?.GetValue<string>() == "var")
            {
                if (ReferenceEquals(cur, scope)) cur = scope.Child();
                cur.Declare(vo);
            }
        }
    }

    // Bottom-up: rewrite a node's CHILDREN (so a nested operator operand is already lowered), THEN lower the node
    // itself — wrapping the already-lowered recv/args into the binOp/unaryOp. A declaration node extends `scope` with
    // its params + body locals so StaticType can recover a bare operand `local`'s declared type.
    static void WalkObject(JsonObject obj, BirScope scope)
    {
        var child = scope.Extend(obj);
        foreach (var key in obj.Select(kv => kv.Key).ToList())
            switch (obj[key])
            {
                case JsonObject co: WalkObject(co, child); if (Lower(co, child) is JsonNode r) obj[key] = r; break;
                case JsonArray ca: WalkArray(ca, child); break;
            }
    }

    // A primitive-operator `callInstance` / a comparison-intrinsic `callStatic` -> the binOp/unaryOp node kotc
    // used to synthesize, else null (leave as-is).
    static JsonNode Lower(JsonObject o, BirScope scope)
    {
        var k = (o["k"] as JsonValue)?.GetValue<string>();
        if (k == "callStatic") return LowerIntrinsic(o, scope);
        if (k != "callInstance") return null;
        if (OwnerFqn(o["ownerType"]) is not string ownerFqn) return null;
        if ((o["method"] as JsonValue)?.GetValue<string>() is not string member) return null;
        var args = o["args"] as JsonArray ?? new JsonArray();

        // String concatenation (`a + b`, receiver `kotlin.String`) — the MEMBER recognition kotc used to do (#52
        // Phase 5). kotc now emits the FAITHFUL `callInstance kotlin.String.plus(a, b)` (no type hint); re-emit the
        // identical 2-part `concat` node kotc used to synthesize. FaithfulHintRecognition (runs NEXT) recovers each
        // part's static type via StaticType (the former `partTypes` hint) and applies the Phase-4b part routing
        // (collection -> clrCollToString, nullable -> LibraryKt.toString) — the SAME as for a string template.
        if (ownerFqn == "kotlin.String" && member == "plus" && args.Count == 1)
            return new JsonObject
            {
                ["k"] = "concat",
                ["parts"] = new JsonArray { o["recv"]?.DeepClone(), args[0]?.DeepClone() },
            };

        if (!PrimitiveOpFq.Contains(ownerFqn)) return null;

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
    static JsonNode LowerIntrinsic(JsonObject o, BirScope scope)
    {
        if (OwnerFqn(o["owner"]) != IntrinsicOwner) return null;
        if ((o["method"] as JsonValue)?.GetValue<string>() is not string m) return null;
        var args = o["args"] as JsonArray ?? new JsonArray();
        // The comparison intrinsics (`less`/`lessOrEqual`/`greater`/`greaterOrEqual`) -> `{k:binOp, op:<}`. kotc emits
        // the PLAIN operand expressions; the operand SHAPING that the retired kotc COMPARE block did — a nullable
        // primitive (`Int?` smart-cast to `Int`) surfaces `Nullable<T>.Value`, a boxed-Any operand casts to the
        // OTHER operand's concrete type — is reproduced HERE off StaticType (the CLR<->Kotlin relation). Operands
        // stay byte-identical to the retired binOp.
        if (args.Count == 2 && CompareOp.TryGetValue(m, out var cop))
            return new JsonObject
            {
                ["k"] = "binOp", ["op"] = cop,
                ["lhs"] = ShapeCompareOperand(args[0], args[1], scope),
                ["rhs"] = ShapeCompareOperand(args[1], args[0], scope),
            };
        // `==` (EQEQ): structural equality recognized off TWO operand hints — the surface `argTypes` (declared static
        // types) drive the prim/ref split; the cast-stripped `argValueTypes` drive the Kotlin-SEMANTIC recognition
        // (collection structural `==`, Double/Float total-order `==`) that kotc's former collEqRoute/floatTotalEqRoute
        // did. Order reproduces kotc's precedence exactly:
        //   1. BOTH argTypes non-null primitives (PrimitiveEqFq) -> CIL `ceq` (`binOp ==`). [direct primitive, incl direct Double]
        //   2. else BOTH argValueTypes the SAME collection kind -> the struct-eq helper (`listOf(1)==setOf(1)` differs in kind -> falls through).
        //   3. else BOTH argValueTypes a non-null Double / non-null Float -> the float-equals helper.
        //   4. else the null-safe `Object.Equals` (`objEq`).
        // Operands passed to the collection/float HELPER are cast-stripped (an IMPLICIT_CAST-to-Any renders as a `cast`
        // node — matching kotc's former collEqRoute/floatTotalEqRoute `expr(unwrapped)`). The primitive fast-path AND the
        // `objEq` fallback keep the ORIGINAL operands (kotc's former reference EQEQ emitted `expr(operands[i])`, NOT
        // unwrapped — stripping the Any-box off a boxed value operand `anyVal == 1` would feed a raw value to
        // Object.Equals -> invalid IL).
        if (m == "EQEQ" && args.Count == 2)
        {
            // #59: recover the operand static types via StaticType (no kotc hint). SURFACE (the former `argTypes`)
            // drives the prim fast-path; VALUE (the former cast-stripped `argValueTypes`) drives collection/float.
            var ls = StaticType.Surface(args[0], scope);
            var rs = StaticType.Surface(args[1], scope);
            if (ls is TypeNode.Fqn lsf && PrimitiveEqFq.Contains(lsf.Name)
                && rs is TypeNode.Fqn rsf && PrimitiveEqFq.Contains(rsf.Name))
                return new JsonObject { ["k"] = "binOp", ["op"] = "==", ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() };
            var lt = StaticType.Value(args[0], scope);
            var rt = StaticType.Value(args[1], scope);
            var lc = lt != null ? FaithfulHints.ClassifyColl(lt) : null;
            var rc = rt != null ? FaithfulHints.ClassifyColl(rt) : null;
            var lu = FaithfulHints.StripAnyCast(args[0]);
            var ru = FaithfulHints.StripAnyCast(args[1]);
            if (lc is { } lk && rc is { } rk && lk.kind == rk.kind)
                return FaithfulHints.StructEquals(lu, ru, lk.kind, lk.args);
            if (lt != null && rt != null)
            {
                if (FaithfulHints.IsNonNullFqn(lt, "kotlin.Double") && FaithfulHints.IsNonNullFqn(rt, "kotlin.Double"))
                    return FaithfulHints.FloatCall("clrDoubleEquals", lu, ru);
                if (FaithfulHints.IsNonNullFqn(lt, "kotlin.Float") && FaithfulHints.IsNonNullFqn(rt, "kotlin.Float"))
                    return FaithfulHints.FloatCall("clrFloatEquals", lu, ru);
            }
            return new JsonObject { ["k"] = "objEq", ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() };
        }
        // `===` (EQEQEQ): always identity (`ceq` = `binOp ==`).
        if (m == "EQEQEQ" && args.Count == 2)
            return new JsonObject { ["k"] = "binOp", ["op"] = "==", ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() };
        return null;
    }

    // Shape a comparison operand the way the retired kotc COMPARE `operand()` helper did, but off StaticType:
    //   (1) a value-nullable primitive (`Int?`) -> wrap in `{k:nullableValue, elem, e}` (surface `Nullable<T>.Value`);
    //       a raw `Nullable<T>` struct load into a compare op is invalid IL / reads garbage (the C1 miscompile).
    //   (2) a boxed-Any operand (surface `kotlin.Any`, e.g. an un-narrowed smart-cast `x is Int && x > 10`) whose
    //       OTHER operand pins a concrete type -> cast it to that type so the op sees the value, not the box.
    // An operand already unwrapped (a `nullableValue` node) surfaces its non-null elem, so (1) never double-wraps.
    static JsonNode ShapeCompareOperand(JsonNode operand, JsonNode other, BirScope scope)
    {
        var s = StaticType.Surface(operand, scope);
        if (s is TypeNode.Nullable ns && ns.Of is TypeNode.Fqn nf && PrimitiveEqFq.Contains(nf.Name))
            return new JsonObject { ["k"] = "nullableValue", ["elem"] = TypeNode.Write(ns.Of), ["e"] = operand?.DeepClone() };
        if (s is TypeNode.Fqn sf && sf.Name == "kotlin.Any")
        {
            var os = StaticType.Surface(other, scope);
            if (os is not null && !(os is TypeNode.Fqn of && of.Name == "kotlin.Any"))
                return new JsonObject { ["k"] = "cast", ["type"] = TypeNode.Write(os), ["e"] = operand?.DeepClone() };
        }
        return operand?.DeepClone();
    }

    static string OwnerFqn(JsonNode t) =>
        TypeJson.Read(t) is TypeNode.Fqn f ? ReferenceMetadataIndex.BareOwnerFqn(f.Name) : null;

    static bool FirstSigIsChar(JsonObject o) =>
        o["sig"] is JsonArray s && s.Count >= 1 && TypeJson.Read(s[0]) is TypeNode.Fqn f && f.Name == "kotlin.Char";
}
