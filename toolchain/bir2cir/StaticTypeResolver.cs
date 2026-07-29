using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// STATIC-TYPE CONSUMPTION (#59/#122): the single uniform source bir2cir reads an operand's Kotlin static type from.
// The frontend already resolved every expression's type in FIR, so bir2cir CONSUMES that fact rather than re-deriving
// it (the no-re-resolution-downstream invariant): kotc STAMPS the instantiated `node.type` as a `sty` slot on every
// value node at its expr() chokepoint, and bir2cir's lowering carries it (MemberCallSubstitution / NetInteropBinding /
// EnumMemberBinding / AnySlotRebind copy `sty` onto the nodes they synthesize, alongside `ret`). Surface reads that
// stamp directly — it no longer re-does overload return-type resolution against the ref.dll (the deleted
// ResolveCallReturn / ResolveFieldType / LocalMemberType / SubstMemberTv path, and the cross-file GlobalTypes hack).
//
// Two flavors (the CLR/Roslyn twins of kotc's former birType(op.type) vs stripImplicit/stripCast):
//   Surface — the operand expression's OWN static type (`sty`, or a structural type slot — a boxing/narrowing `cast`
//             node's target IS the surface type). Reproduces EQEQ `argTypes` (the primitive fast-path key).
//   Value   — peel a compiler/boxing `cast` (and the value-nullable unwrap) to the UNDERLYING value type.
//             Reproduces `argValueTypes` / `partTypes` (the collection/float/nullable Kotlin-semantic key).
//
// `sty` already carries the frontend's smart-cast refinement, generic args and nullability, so a smart-cast operand
// resolves to its refined type with no local re-inference. A bir2cir-SYNTHESIZED node that carries no stamp resolves
// through its own structural type slot (a lowered `binOp`/`unaryOp`, a `valueBlock` result, a `newArray` elem) or —
// for a synthesized/spliced `local` read — the lexical `BirScope`, which records declared var/param `type` slots (a
// carried frontend fact, NOT a ref.dll re-resolution). Every kotc-emitted `local`/call/field read carries `sty`, so
// BirScope only ever answers for a bir2cir-introduced temp the passes track via its own `var` decl.

// A local/param type environment for a method body: name -> declared TypeNode. Built by extending a parent scope with a
// declaration's params + its body's `var` locals (a local shadows a same-name param). Mirrors MemberCallSubstitution's
// SubstCtx.VarTypes, but usable by the EARLY passes (PrimitiveOperatorLowering / FaithfulHintRecognition) that run
// before MemberCallSubstitution builds its own SubstCtx.
sealed class BirScope
{
    public readonly Dictionary<string, TypeNode> VarTypes;
    public static readonly BirScope Empty = new();

    BirScope() { VarTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal); }
    BirScope(BirScope parent) { VarTypes = new Dictionary<string, TypeNode>(parent.VarTypes, StringComparer.Ordinal); }

    // A child scope carrying this declaration's PARAMS only (NOT its body locals — those are recorded LEXICALLY as the
    // walk passes each `var`, so two same-named locals in disjoint sub-scopes — e.g. `for ((k,v) in a){…}` twice with a
    // List<Int> then a List<String> `v` — do NOT collide via a flat last-wins dict). Returns `this` when there are no
    // params. NOTE the FULL declared type (with nullability) is recorded: StaticType needs the nullability intact (a
    // nullable primitive is NOT the `==` ceq fast-path; a nullable concat part routes to the null-safe LibraryKt.toString),
    // and ClassifyColl unwraps a nullable coll itself.
    public BirScope Extend(JsonObject decl)
    {
        var ps = decl["params"] as JsonArray;
        if (ps == null || ps.Count == 0) return this;
        var child = new BirScope(this);
        foreach (var p in ps)
            if (p is JsonObject po && (po["name"] as JsonValue)?.GetValue<string>() is string pn
                && TypeJson.Read(po["type"]) is TypeNode pt)
                child.VarTypes[pn] = pt;
        return child;
    }

    // A mutable child scope (a copy of this) that the walk grows in place as it passes each `var` in a statement
    // sequence — so a `var` is in scope for the SUBSEQUENT siblings/children only (lexical block scoping).
    public BirScope Child() => new(this);

    // Seed a scope from an existing name->type map (a consumer that tracks its own lexical environment — e.g. the
    // StringCharSequenceBridge's Env — hands StaticType.Surface a BirScope so a synthesized `local` read resolves).
    public static BirScope FromVars(IReadOnlyDictionary<string, TypeNode> vars)
    {
        var s = new BirScope();
        foreach (var kv in vars) s.VarTypes[kv.Key] = kv.Value;
        return s;
    }

    // Record a `var` declaration into THIS (mutable child) scope, in place. No-op for a non-var / untyped node.
    public void Declare(JsonObject o)
    {
        if ((o["k"] as JsonValue)?.GetValue<string>() == "var"
            && (o["name"] as JsonValue)?.GetValue<string>() is string vn
            && TypeJson.Read(o["type"]) is TypeNode vt)
            VarTypes[vn] = vt;
    }
}

static class StaticType
{
    // The operand expression's OWN static type. A value node reads the FRONTEND-STAMPED `sty` (kotc's instantiated
    // node.type, carried through lowering); a bir2cir-synthesized node reads its own structural type slot. null when
    // the node carries no recoverable static type — treated by callers as "not a bare primitive"/"not a collection",
    // the same posture the former re-resolution took on a miss.
    public static TypeNode Surface(JsonNode node, BirScope scope)
    {
        if (node is not JsonObject o) return null;
        var k = (o["k"] as JsonValue)?.GetValue<string>();
        switch (k)
        {
            // A boxing/narrowing cast's TARGET is the surface type (kotc's `birType(op.type)` = the cast target).
            case "cast": return TypeJson.Read(o["type"]);
            case "const": return TypeJson.Read(o["type"]);
            case "conv": return TypeJson.Read(o["to"]);
            case "nullableValue": return TypeJson.Read(o["elem"]);
            // A safe-call wrap (`a?.member` desugar, BirEmitterControlFlow): both arms produce `Nullable<elem>` — the
            // `nullableWrap` present-value and the `nullableNull` absent-value. A BARE-LOCAL-receiver safe call returns
            // the raw `cond` with NO `type` stamp (only the bindOnce path wraps it in a typed valueBlock), so its
            // surface must be recovered from these arms — else a `b?.d == y` float `==` misses the value-nullable
            // classification and keeps the raw `ceq` over `Nullable<T>` (unverifiable IL, #181).
            case "nullableWrap" or "nullableNull":
                return TypeJson.Read(o["elem"]) is TypeNode nwe ? new TypeNode.Nullable(nwe) : null;
            // A local read: the frontend stamp (accurate incl. smart-cast); else the declared type from the lexical
            // scope, for a bir2cir-SYNTHESIZED local (a spliced temp) that carries no stamp. NOTE the stamp SHADOWS a
            // later var-decl retype (InlineSplice.RetypeReceiverToConcrete / CharSeqStringLowering) — benign today
            // (collection classification is name-keyed; CharSeq rets ride the explicit charSeqRetLambdas channel).
            case "local":
                return TypeJson.Read(o["sty"])
                    ?? ((o["name"] as JsonValue)?.GetValue<string>() is string vn
                        && scope.VarTypes.TryGetValue(vn, out var vt) ? vt : null);
            // A call / member / property read: the stamped `sty` (kotc / carried through the clr* reshape), else the
            // carried `ret` (a generic call, or a clr* node whose synthesizer stamped only `ret`). Already the
            // INSTANTIATED result type — no owner type-variable substitution needed.
            case "callStatic" or "callInstance" or "clrInstance" or "clrStatic" or "clrPropGet":
            case "field" or "lateinitGet" or "staticField":
                return TypeJson.Read(o["sty"]) ?? TypeJson.Read(o["ret"]);
            case "new" or "newClr": return TypeJson.Read(o["type"]);
            case "newArray" or "newArrayInit" or "newArraySized":            // an array factory / sized ctor -> Array<elem>
                return TypeJson.Read(o["elem"]) is TypeNode ae ? new TypeNode.Fqn("kotlin.Array", new[] { ae }) : null;
            case "arrayGet": return TypeJson.Read(o["elem"]);                 // `a[i]` -> the element type
            case "enumValue": return TypeJson.Read(o["type"]);
            // `enumValues<T>()`/`T.entries` -> Array<T>. `type` is the structured enum Type (both EnumIntrinsicLowering's
            // re-emission and kotc's direct `.values()`/`.entries` recognition clone the faithful FQN node).
            case "enumValues":
                return TypeJson.Read(o["type"]) is TypeNode eet ? new TypeNode.Fqn("kotlin.Array", new[] { eet }) : null;
            case "safeCastValue": return TypeJson.Read(o["elem"]);           // `x as? V` -> V (value)
            case "concat": return new TypeNode.Fqn("kotlin.String");         // a template/`+` concat is always String
            case "isInst" or "isInstRef" or "objEq": return new TypeNode.Fqn("kotlin.Boolean");
            // An expression-level ternary (`if`-expr / elvis / when-expr) -> its unified branch type (`type`), else the
            // then/else branch types (a statement-position / `!!`-desugar cond carries no `type`).
            case "cond": return TypeJson.Read(o["type"]) ?? Surface(o["then"], scope) ?? Surface(o["else"], scope);
            // A spliced inline call becomes a `valueBlock {stmts, result}` (InlineSplice) — its static type is the
            // RESULT's, resolved with the block's OWN `var`s in scope (e.g. an `apply`-splice's result is a `local`
            // declared in its stmts). The valueBlock itself carries no stamp; its result is a stamped node.
            case "valueBlock":
            {
                var inner = scope.Child();
                foreach (var arr in new[] { o["stmts"] as JsonArray, o["body"] as JsonArray })
                    if (arr != null) foreach (var st in arr) if (st is JsonObject so) inner.Declare(so);
                return TypeJson.Read(o["type"]) ?? Surface(o["result"], inner);
            }
            // A call under its evaluation plan (§2.7, BIR-only — the passes that run before CallEvalLowering see it):
            // the bindings are statements evaluated ahead of the call, so the VALUE is the wrapped call's.
            case "callEval": return TypeJson.Read(o["type"]) ?? Surface(o["expr"], scope);
            // A LOWERED primitive operator (PrimitiveOperatorLowering / RangeMembershipLowering synthesize these) —
            // recover its RESULT type structurally, matching kotc's former `birType(op.type)` for the un-lowered op.
            case "unaryOp":
                return (o["op"] as JsonValue)?.GetValue<string>() == "!" ? new TypeNode.Fqn("kotlin.Boolean") : Surface(o["e"], scope);
            case "binOp":
                return (o["op"] as JsonValue)?.GetValue<string>() is "<" or "<=" or ">" or ">=" or "==" or "!=" or "&&" or "||"
                    ? new TypeNode.Fqn("kotlin.Boolean")
                    : Surface(o["lhs"], scope) ?? Surface(o["rhs"], scope);
            // A bare `this` and anything else remain null → the caller treats them as non-primitive/non-collection.
            default: return null;
        }
    }

    // The operand's UNDERLYING value type: peel a `cast` (a compiler boxing/narrowing OR explicit `as`; the BIR does
    // not distinguish them, so this peels both — the CLR twin of kotc's `stripCast`) and the value-nullable unwrap,
    // then read the inner. Reproduces `argValueTypes` / `partTypes` (the collection/float/nullable recognition key).
    public static TypeNode Value(JsonNode node, BirScope scope)
    {
        if (node is not JsonObject o) return null;
        var k = (o["k"] as JsonValue)?.GetValue<string>();
        if (k == "cast" && o["e"] is JsonNode ce) return Value(ce, scope);
        if (k == "nullableValue" && o["e"] is JsonNode ne)
        {
            // A value-nullable unwrap's underlying value type is its `elem` (the non-null value); prefer it over the
            // wrapped `e` (which is the Nullable<T> local).
            return TypeJson.Read(o["elem"]) ?? Value(ne, scope);
        }
        return Surface(node, scope);
    }
}
