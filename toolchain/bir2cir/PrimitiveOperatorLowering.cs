using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
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
    // The value primitives whose Kotlin type has a VALUE `<`/`>`/`<=`/`>=` compare (the signed integrals + Double/Float
    // + bool/char, NOT the unsigned inline classes). ShapeCompareOperand reads this to detect a value-nullable operand
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
    // Value primitives whose OPERATOR result is DECLARED wider-or-different than ilemit's natural typing (ilemit types
    // a bin/unary result as the narrow left operand, and promotes a narrow operand to int in a mixed op). Kotlin
    // DECLARES: Byte/Short arith -> Int; UByte/UShort arith -> UInt; Char.plus/minus(Int) -> Char, Char.minus(Char) ->
    // Int; inc/dec -> the receiver's own narrow type (but the `+1` desugaring runs at int width). For these owners the
    // bare op truncates on box / narrow store, so wrap it in a `conv` to the frontend-resolved return type (`dynRet`).
    // The full-width owners (Int/Long/Double/Float/UInt/ULong) are left bare — their result already matches ilemit.
    static readonly HashSet<string> NarrowResultFq = new(StringComparer.Ordinal)
    {
        "kotlin.Char", "kotlin.Byte", "kotlin.Short", "kotlin.UByte", "kotlin.UShort",
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
            // #94: unsigned `shr` is LOGICAL (zero-filling) — Kotlin's `UInt.shr`/`ULong.shr` zero-fill, unlike the
            // sign-propagating arithmetic `>>`. Route it to `>>>` (ilemit `Shr_Un`). `shl` is bit-identical for
            // signed/unsigned, and the narrow unsigned types (UByte/UShort) have no shift operator, so only UInt/ULong
            // reach here.
            if (member == "shr" && (ownerFqn == "kotlin.UInt" || ownerFqn == "kotlin.ULong")) aop = ">>>";
            var bin = new JsonObject { ["k"] = "binOp", ["op"] = aop, ["lhs"] = o["recv"]?.DeepClone(), ["rhs"] = args[0]?.DeepClone() };
            return WrapDeclaredReturn(o, ownerFqn, bin);
        }
        if (args.Count == 0 && UnaryOp.TryGetValue(member, out var uop))
            return WrapDeclaredReturn(o, ownerFqn, new JsonObject { ["k"] = "unaryOp", ["op"] = uop, ["e"] = o["recv"]?.DeepClone() });
        // inc/dec (the `i++`/`i--` desugaring) -> `(recv + 1)`/`(recv - 1)`. The `const 1` is typed `kotlin.Int` for
        // EVERY primitive (ilemit widens it), so a narrow receiver's `+1` runs at int width and MUST be converted back
        // to the receiver's own narrow type (`dynRet`) — else `(127.toByte()).inc()` yields int 128, not Byte -128.
        if (args.Count == 0 && (member == "inc" || member == "dec"))
            return WrapDeclaredReturn(o, ownerFqn, new JsonObject
            {
                ["k"] = "binOp", ["op"] = member == "inc" ? "+" : "-",
                ["lhs"] = o["recv"]?.DeepClone(),
                ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = 1 },
            });
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
        //   1. BOTH argTypes non-null primitives (EqEqPrimFq — the integral set + bool/char, NOT Double/Float) -> CIL
        //      `ceq` (`binOp ==`). [direct integral primitive; a DIRECT float `==` arrives as `ieee754equals`, not here]
        //   2. else BOTH argValueTypes the SAME collection kind -> the struct-eq helper (`listOf(1)==setOf(1)` differs in kind -> falls through).
        //   3. else BOTH argValueTypes a non-null Double / non-null Float -> the total-order float-equals helper
        //      (this is where a STRUCTURAL Double/Float EQEQ — data-class `equals` — lands: NaN==NaN true, -0.0!=0.0; #95).
        //   4. else BOTH argValueTypes a raw value-nullable `Double?` / `Float?` -> null-safe total-order bit-equality
        //      (the #152 twin of step 3: a STRUCTURAL EQEQ over a NULLABLE Double/Float field — null==null true, one null
        //      false, both present -> clr{Double,Float}Equals; else the boxed `objEq` gives IEEE `Double.Equals`).
        //   5. else the null-safe `Object.Equals` (`objEq`).
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
            if (ls is TypeNode.Fqn lsf && EqEqPrimFq.Contains(lsf.Name)
                && rs is TypeNode.Fqn rsf && EqEqPrimFq.Contains(rsf.Name))
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
                // #152: STRUCTURAL EQEQ over two value-nullable Double/Float (`Double?`/`Float?` — a data-class `equals`
                // field, or a `==` routed to the structural path). Both VALUE types are raw `Nullable<T>` (not boxed Any):
                // the non-null arms above miss them, and the `objEq` fallback boxes both -> `Double.Equals`, which uses
                // IEEE `==` for the value compare (`(-0.0).Equals(0.0)==true`) — violating Kotlin's TOTAL-ORDER structural
                // equals (`-0.0 != 0.0`, `NaN == NaN`) that #95 adopted for the non-null case and that the bit-based
                // hashCode requires. Synthesize null-safe bit-equality off `nullableHasValue`/`nullableValue` +
                // clrDoubleEquals/clrFloatEquals: null==null -> true, exactly one null -> false, both present -> the
                // total-order helper on the unwrapped values. (A DIRECT `a == b` on `Double?` is IEEE per #95 and is
                // routed to the `ieee754equals` arm above — it never reaches this EQEQ arm.) The RAW `args[i]` (not the
                // cast-stripped `lu`/`ru`) are hoisted: StripAnyCast only peels a bare-`Any` box, and the var init's
                // `object -> Nullable<T>` coercion is the correct unbox — feeding the raw nullable operand is deliberate.
                if (NullableFloatElem(lt) is string lfe && NullableFloatElem(rt) is string rfe && lfe == rfe)
                    return NullableFloatEquals(args[0], args[1], lfe);
            }
            return new JsonObject { ["k"] = "objEq", ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() };
        }
        // `===` (EQEQEQ): always identity (`ceq` = `binOp ==`).
        if (m == "EQEQEQ" && args.Count == 2)
            return new JsonObject { ["k"] = "binOp", ["op"] = "==", ["lhs"] = args[0]?.DeepClone(), ["rhs"] = args[1]?.DeepClone() };
        // `ieee754equals`: the ordered IEEE-754 float/double comparison (`-0.0 == 0.0`, `NaN != NaN`) -> raw CIL
        // `ceq` (`binOp ==`). Operands are already value-shaped by kotc; byte-identical to the former kotc lowering.
        if (m == "ieee754equals" && args.Count == 2)
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
        if (s is TypeNode.Nullable ns && ns.Of is TypeNode.Fqn nf && ComparePrimFq.Contains(nf.Name))
            return new JsonObject { ["k"] = "nullableValue", ["elem"] = TypeNode.Write(ns.Of), ["e"] = operand?.DeepClone() };
        if (s is TypeNode.Fqn sf && sf.Name == "kotlin.Any")
        {
            var os = StaticType.Surface(other, scope);
            if (os is not null && !(os is TypeNode.Fqn of && of.Name == "kotlin.Any"))
                return new JsonObject { ["k"] = "cast", ["type"] = TypeNode.Write(os), ["e"] = operand?.DeepClone() };
        }
        return operand?.DeepClone();
    }

    // The value-nullable float element FQN (`kotlin.Double`/`kotlin.Float`) iff the type is raw `Nullable<T>` over a
    // bare Double/Float — the #152 gate. A boxed-Any operand (StaticType.Value = kotlin.Any) or a non-float nullable
    // fails, so ONLY a `Double?`/`Float?` structural EQEQ reaches the null-safe bit-equality synthesis.
    static string NullableFloatElem(TypeNode t) =>
        t is TypeNode.Nullable n && n.Of is TypeNode.Fqn { Args: null } f
            && (f.Name == "kotlin.Double" || f.Name == "kotlin.Float") ? f.Name : null;

    // Distinct temp-name counter for the #152 hoisted operands (mirrors PreconditionLowering's `__rn$`).
    static int _neCounter;

    // Null-safe TOTAL-ORDER equality over two value-nullable `Double?`/`Float?` operands (#152). Hoists each raw
    // `Nullable<T>` operand into a temp (so a side-effecting operand is evaluated ONCE, not per HasValue/Value read),
    // then builds `null==null -> true / one null -> false / both present -> clr{Double,Float}Equals(a.Value, b.Value)`
    // as nested `cond` (no `&&`/`||` node kind in CIR). The float helper does `toBits()==toBits()` — `-0.0 != 0.0`,
    // `NaN == NaN` — matching the bit-based hashCode.
    static JsonNode NullableFloatEquals(JsonNode a, JsonNode b, string elemFqn)
    {
        var floatEq = elemFqn == "kotlin.Double" ? "clrDoubleEquals" : "clrFloatEquals";
        var na = "__ne$" + Interlocked.Increment(ref _neCounter);
        var nb = "__ne$" + Interlocked.Increment(ref _neCounter);
        JsonObject NullableType() => new() { ["t"] = "nullable", ["of"] = TypeJson.Fqn(elemFqn) };
        JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
        JsonObject Var(string name, JsonNode init) => new() { ["k"] = "var", ["name"] = name, ["type"] = NullableType(), ["init"] = init.DeepClone() };
        JsonObject HasValue(string name) => new() { ["k"] = "nullableHasValue", ["elem"] = TypeJson.Fqn(elemFqn), ["e"] = Local(name) };
        JsonObject Value(string name) => new() { ["k"] = "nullableValue", ["elem"] = TypeJson.Fqn(elemFqn), ["e"] = Local(name) };
        JsonObject Bool(bool v) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["value"] = v };
        // both present -> the total-order float helper on the unwrapped values; both null -> true.
        var whenSameNullness = new JsonObject
        {
            ["k"] = "cond",
            ["cond"] = HasValue(na),
            ["then"] = FaithfulHints.FloatCall(floatEq, Value(na), Value(nb)),
            ["else"] = Bool(true),
        };
        // hasValue(a) == hasValue(b) ? (above) : false  — exactly one null is unequal.
        var result = new JsonObject
        {
            ["k"] = "cond",
            ["cond"] = new JsonObject { ["k"] = "binOp", ["op"] = "==", ["lhs"] = HasValue(na), ["rhs"] = HasValue(nb) },
            ["then"] = whenSameNullness,
            ["else"] = Bool(false),
        };
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray { Var(na, a), Var(nb, b) },
            ["result"] = result,
        };
    }

    static string OwnerFqn(JsonNode t) =>
        TypeJson.Read(t) is TypeNode.Fqn f ? ReferenceMetadataIndex.BareOwnerFqn(f.Name) : null;

    // Wrap a lowered narrow/char operator (`bare`) in a `conv` to its DECLARED Kotlin return type, carried on the
    // call as the frontend-resolved `dynRet`. Consumes what kotc already resolved (no re-derivation of Kotlin's
    // operator-typing rules here). A full-width owner (not in NarrowResultFq) is left bare — ilemit's natural typing
    // already matches. See NarrowResultFq for the truncation rationale (#93). kotc stamps `dynRet` UNCONDITIONALLY on
    // every member callInstance (BirEmitterCalls, `birType(call.type)`), so the missing/non-Fqn fallback is defensive
    // and believed-unreachable for a NarrowResultFq owner (a bare fallback would silently reintroduce truncation).
    static JsonNode WrapDeclaredReturn(JsonObject o, string ownerFqn, JsonNode bare)
    {
        if (!NarrowResultFq.Contains(ownerFqn)) return bare;
        if (TypeJson.Read(o["dynRet"]) is not TypeNode.Fqn ret) return bare;
        return new JsonObject { ["k"] = "conv", ["to"] = TypeJson.Fqn(ret.Name), ["e"] = bare };
    }
}
