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
// Two flavors answer distinct current lowering questions:
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
    // the node carries no recoverable static type. Callers conservatively treat that as "not a recognized bare
    // primitive"/"not a recognized collection" rather than inferring a type from physical layout.
    //
    // FOUNDED ON `bir-common/NodeType.cs`, which answers every kind whose type is IN the node (and is the same
    // deriver the suspend spill and the plan's address pins type their locals with) — INCLUDING the precedence of the
    // three result-type stamps, which that file now states once for the whole toolchain (`sty` before `ret` before
    // `dynRet`, #199). Only two sorts of arm stay here — a kind that needs the enclosing lexical SCOPE, and a kind
    // whose answer is a bir2cir SPELLING rather than a node fact. Everything else delegates, so the two cannot
    // classify a kind differently. The call/field/`bindRef` family needs NO arm at all: reading `sty` first IS the
    // core's order, so those kinds are node-local answers like any other.
    public static TypeNode Surface(JsonNode node, BirScope scope)
    {
        if (node is not JsonObject o) return null;
        TypeNode Core() => NodeType.Of(o, x => Surface(x, scope), PrimArrayElem);
        switch ((o["k"] as JsonValue)?.GetValue<string>())
        {
            // A local read: the frontend stamp (accurate incl. smart-cast); else the declared type from the lexical
            // scope, for a bir2cir-SYNTHESIZED local (a spliced temp) that carries no stamp. NOTE the stamp SHADOWS a
            // later var-decl retype. The retyping pass owns keeping or deleting that stamp under the NodeType §2.7
            // contract; consumers must not silently demote the frontend stamp below the lexical declaration.
            case "local":
                return TypeJson.Read(o["sty"])
                    ?? ((o["name"] as JsonValue)?.GetValue<string>() is string vn
                        && scope.VarTypes.TryGetValue(vn, out var vt) ? vt : null);
            // A spliced inline call becomes a `valueBlock {stmts, result}` (InlineSplice) — its static type is the
            // RESULT's, resolved with the block's own `var`s AND the enclosing scope (the core resolves the block's
            // own; only here are the enclosing ones visible, for a result that reads an outer synthesized temp).
            case "valueBlock":
            {
                var inner = scope.Child();
                foreach (var arr in new[] { o["stmts"] as JsonArray, o["body"] as JsonArray })
                    if (arr != null) foreach (var st in arr) if (st is JsonObject so) inner.Declare(so);
                return TypeJson.Read(o["type"]) ?? Surface(o["result"], inner);
            }
            // An array-producing construction: the core answers structurally, this reader's classifiers key on the
            // name. `enumValues<T>()`/`T.entries` is one of them (-> `Array<T>`), which is why it needs the SPELLING
            // conversion here and nothing else — the derivation itself is the core's.
            //
            // (The safe-call wrap's ABSENT arm, `nullableNull`, is node-local and answered entirely by the core: it
            // carries no value, so only its `elem` says the type is `Nullable<elem>`. It matters to this reader
            // because a BARE-LOCAL-receiver safe call returns the raw `cond` with no `type` stamp, so the surface is
            // recovered from the two arms — else a `b?.d == y` float `==` misses the value-nullable classification
            // and keeps the raw `ceq` over `Nullable<T>`, which is unverifiable IL (#181).)
            case "newArray" or "newArrayInit" or "newArraySized" or "spreadConcat" or "enumValues":
                return ArrayAsFqn(Core());
            // Everything else — the call/member/field family and its `clr*` reshapes, a plan's `bindRef` read,
            // const/cast/conv/new/arrayGet/cond/callEval/concat/isInst/objEq/unaryOp/binOp/the nullable wrap+unwrap,
            // and the kinds neither of these two derivers used to answer — is node-local.
            default:
                return Core();
        }
    }

    // The two SPELLINGS of an array type. The shared deriver answers structurally (`{t:"array"}`), which is what a
    // spill slot's declared type has to be; this reader's consumers (FaithfulHints' collection classification,
    // ArrayConstructionLowering's element recovery) are name-keyed and match `kotlin.Array<E>`. Converting once,
    // here, on the arms that CONSTRUCT an array type is the whole of the difference between them — the alternative
    // was one deriver arm in each file that silently disagreed.
    static TypeNode ArrayAsFqn(TypeNode t) => t is TypeNode.Array a ? new TypeNode.Fqn("kotlin.Array", new[] { a.Elem }) : t;

    // The specialized-array element table the shared deriver deliberately does not restate (`kotlin.IntArray` ->
    // `kotlin.Int`), passed to it as the one table the toolchain already keeps.
    static string PrimArrayElem(string name) => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var e) ? e : null;

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
