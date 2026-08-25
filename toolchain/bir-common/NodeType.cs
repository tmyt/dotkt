// THE NODE-LOCAL STATIC TYPE of an expression node: "what type does this node's own content say it produces?"
//
// Two bir2cir sites mint a local for a value taken out of an expression — the suspend lowering's evaluation-order
// spill, and the call-evaluation plan's address pins — and a local without a type is not a lesser local, it is
// unverifiable IL. Both need the same answer, derived the same way, so the derivation lives here once.
//
// bir2cir's `StaticType.Surface` (StaticTypeResolver.cs) — the operand-classification reader the early passes ask
// what an expression's Kotlin static type is — is FOUNDED on this file rather than restating it: it adds only the
// arms that need a lexical SCOPE this file cannot see and the one array SPELLING its readers key on, and delegates
// every other kind here. So a kind cannot be typed one way for a spill slot and another way for a classifier.
//
// SCOPE: node-local facts only. An explicit `sty`/`ret`/`dynRet` stamp — in THAT order, see PRECEDENCE on `Of` — then
// whatever slot the kind carries its own result type in (`arrayGet.elem`, `conv.to`, `delegateInvoke.funcType.ret`,
// …). A kind whose type is only knowable
// from an INDEX — a `callStatic`/`callInstance` with no `sty`, a raw `field` read — returns null here; a caller that
// owns such an index (SuspendColdLowering does) supplies it and passes itself as `recurse`, so an operand of a
// `binOp` still resolves through the caller's full deriver rather than falling back to this core.
//
// Returning NULL is a real answer: the caller decides whether that is an error. Neither caller may substitute
// `kotlin.Any` — that boxes a value type and hides a type the CLR would refuse.

#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

public static class NodeType
{
    static readonly TypeNode IntTn = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode BoolTn = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode StringTn = new TypeNode.Fqn("kotlin.String");
    static readonly TypeNode NothingTn = new TypeNode.Fqn("kotlin.Nothing");

    /// <summary>
    /// Does this expression produce NO value because control never leaves it normally — an expression-position
    /// `throw` or `return`, a `break`/`continue` wrapped to sit in a value slot, a call to a `Nothing`-returning
    /// function, or a block ending in any of those? Kotlin's name for that is `Nothing`, so the question is just
    /// "did the type come out `Nothing`", asked through whichever deriver the caller owns.
    /// </summary>
    public static bool IsNothing(TypeNode? t) => t is TypeNode.Fqn { Args: null, Name: "kotlin.Nothing" };

    /// <summary>
    /// The node's EXPLICIT result-type stamp — `sty`, then `ret`, then `dynRet`, in the PRECEDENCE stated on
    /// <see cref="Of"/> — or null when the node carries none. This is the frontend's own answer to "what does this
    /// node produce", carried across by the lowering; everything <see cref="Of"/> adds beyond it is DERIVED from a
    /// node's kind and operands. A caller that must not act on a derivation asks for the stamp alone: the derived
    /// arms answer best-effort, and a best-effort `kotlin.Nothing` is not a licence to delete a value (bir2cir's
    /// NothingValueTermination). Reading the stamp through here rather than re-spelling the three slots is what keeps
    /// that caller and <see cref="Of"/> from disagreeing about which slot wins.
    /// </summary>
    public static TypeNode? Stamp(JsonObject o)
        => TypeJson.Read(o["sty"]) ?? TypeJson.Read(o["ret"]) ?? TypeJson.Read(o["dynRet"]);

    /// <summary>
    /// A pass has just retyped this node's `ret`/`dynRet`; discharge the spec §2.7 obligation that comes with that —
    /// *a pass that changes a node's RESULT TYPE rewrites or deletes its `sty`* — by DELETING the stamp when, and
    /// only when, the change made it wrong. A pass that can compute the new INSTANTIATED type restamps instead; this
    /// is for the passes that cannot, because what they wrote is the physical/erased/declared shape rather than this
    /// call site's instantiation.
    ///
    /// "When, and only when" is the whole point, and it is asked through <see cref="IrSanity.StampAgrees"/> so that
    /// the pass and the #305 chokepoint cannot answer it differently. Dropping unconditionally would be wrong in both
    /// directions: it discards a stamp that still describes the value (`ret` then answers the same thing, so nothing
    /// is gained), while a physical erasure that genuinely changes the result must not leave a semantic stamp that a
    /// downstream spill slot or state-machine field would trust first.
    /// </summary>
    public static void DropStampIfStale(JsonObject o)
    {
        if (TypeJson.Read(o["sty"]) is not TypeNode sty) return;
        // The two result slots read straight, not through a loop over a slot-name array: every retyping pass calls
        // this on every node it touches, and the `sty` guard above already returned for the majority that carry no
        // stamp — the remainder should not pay an allocation to learn the same two names each time.
        if (Refutes(sty, o["ret"]) || Refutes(sty, o["dynRet"])) o.Remove("sty");
    }

    static bool Refutes(TypeNode sty, JsonNode? slot)
        => TypeJson.Read(slot) is TypeNode result && !IrSanity.StampAgrees(sty, result);

    /// <summary>
    /// The node's own static type, or null when only an index could answer. <paramref name="recurse"/> is the
    /// caller's FULL deriver, used for the kinds whose type is an OPERAND's type (`binOp`, `unaryOp`, `arrayGet`);
    /// it defaults to this core. <paramref name="primArrayElem"/> maps a SPECIALIZED array FQN to its element
    /// (`kotlin.IntArray` -> `kotlin.Int`) — a Kotlin fact this file deliberately does not restate, so the caller
    /// passes the one table the toolchain already keeps (bir2cir's <c>BirTypeLowering.PrimArrayElem</c>).
    /// </summary>
    ///
    /// PRECEDENCE of the three result-type stamps — `sty`, then `ret`, then `dynRet` — stated ONCE, here, for every
    /// reader in the toolchain (#199; spec §2.7 *One deriver, two layers*):
    ///
    ///   `sty` is the FRONTEND's INSTANTIATED static type, stamped per CALL SITE at kotc's `expr()` chokepoint, so
    ///   where it exists it is the precise answer to "what does THIS node produce".
    ///   `ret` is emitted only when the callee or its owner is GENERIC (`retHintStr`) — that is, exactly where it may
    ///   name the UNinstantiated DECLARED type (`T`, not `kotlin.Int`). Reading it first typed every generic-owner
    ///   call by its declaration instead of by its use.
    ///   `dynRet` (the @Clr dynamic-dispatch return) is last: on a kotc-emitted `callInstance` it is a copy of the
    ///   same instantiated type as `sty`, and a bir2cir synthesizer that stamps only `dynRet` means it.
    ///
    /// This rests on one INVARIANT, which is a contract on every pass and not on this file: A PASS THAT CHANGES A
    /// NODE'S RESULT TYPE REWRITES OR DELETES ITS `sty` (spec §2.7). A stale `sty` surviving on a retyped node is a
    /// bug in the pass that retyped it — never a reason to demote the stamp back below `ret`.
    public static TypeNode? Of(JsonNode? n, Func<JsonNode?, TypeNode?>? recurse = null,
                               Func<string, string?>? primArrayElem = null)
    {
        if (n is not JsonObject o) return null;
        recurse ??= x => Of(x, null, primArrayElem);
        if (Stamp(o) is TypeNode stamped) return stamped;
        switch (Str(o["k"]))
        {
            case "const": case "cast": case "new": case "newClr": case "var": case "enumValue": case "default":
                // A structural kind carries its own type in `type` — including a `cast`, whose TARGET is what the
                // node produces (the boxing/narrowing conversion is the point of the node).
                return TypeJson.Read(o["type"]);
            case "nullableValue": case "safeCastValue":
                // The UNWRAPPED value: `Nullable<T>.Value` and `x as? V` both produce the ELEMENT, and `elem` is the
                // only type slot their producers write. (Reading `type` here — a slot nothing writes on them — is why
                // a value-nullable unwrap left of a suspension had no type and aborted the compile.)
                return TypeJson.Read(o["elem"]);
            case "nullableWrap":
            case "nullableNull":
                // The inverse: a bare `elem` value lifted into `Nullable<elem>` — and the ABSENT arm of a safe-call
                // wrap, which carries no value at all, so only its `elem` says what the wrap produces.
                return TypeJson.Read(o["elem"]) is TypeNode nwe ? new TypeNode.Nullable(nwe) : null;
            case "cond":
            {
                // An expression-level ternary (`if`-expr / elvis / when-expr): its unified branch type when the
                // producer stamped one — and kotc's `!!`, elvis and safe-call desugars stamp NONE — else a BRANCH's.
                // A branch that never yields a value (`x!!`'s `throw`, an elvis `return`) says nothing about the type
                // of the value the OTHER branch produces, so it may not answer while the other one can: the value of
                // `{ var __nn = x; __nn != null ? __nn : throw }` is an `x`, not a `Nothing`.
                if (TypeJson.Read(o["type"]) is TypeNode ct) return ct;
                var thenT = recurse(o["then"]);
                if (thenT is not null && !IsNothing(thenT)) return thenT;
                return recurse(o["else"]) ?? thenT;
            }
            case "callEval":
                // A call under its evaluation plan (§2.7, BIR-only): the bindings are evaluated ahead of it, so the
                // node's value — and its type — is the wrapped call's.
                return TypeJson.Read(o["type"]) ?? recurse(o["expr"]);
            case "valueBlock":
                // The `type` stamp is OPTIONAL on a block, so both arms are live. The inline splice emits none — what
                // a spliced call produces is its RESULT's own type, which can be strictly more derived than the
                // callee's declared return — while a plan lowered into a block carries the call's static type, which
                // its enclosing merge preserves. Absent a stamp the type is the RESULT's, resolved with the block's
                // own `var`s in scope (an `apply`-splice's result is a local the block itself declares).
                return TypeJson.Read(o["type"]) ?? BlockResultType(o, recurse, primArrayElem);
            case "stackGet": case "byrefLoad":
                return TypeJson.Read(o["elem"]);
            case "arrayGet":
                // A bir2cir-authored read carries `elem`; kotc's does NOT (BirEmitterCalls emits the faithful
                // `{array,index}` intrinsic and leaves the element to be derived), so fall back to the ARRAY's own
                // type — which is where the element genuinely lives.
                return TypeJson.Read(o["elem"]) ?? ElementOf(recurse(o["array"]), primArrayElem);
            case "conv":
                return TypeJson.Read(o["to"]);
            case "binOp":
                // A comparison or a short-circuit yields Boolean; every other operator yields its OPERANDS' type,
                // which either side reports — so an `lhs` the caller's deriver cannot answer falls through to the
                // `rhs` rather than to null.
                return Str(o["op"]) is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||"
                    ? BoolTn : recurse(o["lhs"]) ?? recurse(o["rhs"]);
            case "unaryOp":
                return Str(o["op"]) == "!" ? BoolTn : recurse(o["e"]);
            case "objEq": case "isInst": case "isInstRef": case "nullableHasValue":
                return BoolTn;
            case "throwExpr": case "returnExpr":
                // A TERMINAL expression: control leaves and never comes back, so it produces no value — which in
                // Kotlin is exactly `Nothing`. Answering it (rather than null) is what lets a caller tell "this
                // expression has no value" apart from "I could not work out what its value is", and the two want
                // opposite responses: the first is ordinary code to emit in place, the second a dropped type to
                // report. `break`/`continue` in expression position arrive as a `valueBlock` whose result is one
                // of these (kotc's `breakContinueExpr`), so the block arm below answers `Nothing` for them too.
                return NothingTn;
            case "arrayLen": case "enumOrdinal":
                return IntTn;
            case "concat":
                return StringTn;
            case "newArray": case "newArrayInit": case "newArraySized": case "spreadConcat":
                // Every array-producing construction names its ELEMENT and nothing else — including the vararg
                // `spreadConcat`, which flattens its `parts` into one fresh `Array<elem>`.
                return TypeJson.Read(o["elem"]) is TypeNode ae ? new TypeNode.Array(ae) : null;
            case "enumValues":
                // `enumValues<T>()` / `T.entries` -> `Array<T>`. `type` is the structured enum type both producers
                // (kotc's direct `.values()`/`.entries` recognition and EnumIntrinsicLowering's re-emission) clone.
                return TypeJson.Read(o["type"]) is TypeNode evt ? new TypeNode.Array(evt) : null;
            case "classRef": case "getType":
                // `Foo::class` (`ldtoken` + `Type.GetTypeFromHandle`) and `x::class` (`object.GetType()`) both
                // produce a `System.Type`. NOT the class they name: `classRef`'s `type` slot is the SUBJECT of the
                // reflection, not what the node leaves on the stack (ilemit Emitter.ClrInterop.cs).
                return new TypeNode.Fqn("System.Type");
            case "stackAsSpan":
                // `new System.Span<elem>(ptr, len)` over a stack buffer.
                return TypeJson.Read(o["elem"]) is TypeNode sse
                    ? new TypeNode.Fqn("System.Span", new[] { sse }) : null;
            case "stackAlloc":
                // A raw `localloc` pointer, which this backend spells with the marker FQN its own `var` declarations
                // use (kotc BirEmitterInline).
                return new TypeNode.Fqn("dotkt$stackptr");
            case "newClosure": case "newDelegate": case "newSam": case "newSuspendLambda":
            case "newBoundDelegate": case "newBoundClrDelegate": case "newClrStaticDelegate":
                // The FUNCTION type when the producer knew one; else the synthesized class the node constructs,
                // which each of these names in its own slot — `newSam` the SAM implementation it lifted
                // (`samType`), the others the closure class (`closureType`). The value IS an instance of that
                // class, so it is the node's type even though the reader usually sees it as the interface.
                if (TypeJson.Read(o["funcType"]) is TypeNode ft) return ft;
                if (TypeJson.Read(o["samType"]) is TypeNode st) return st;
                return TypeJson.OwnerName(o["closureType"]) is string cn ? new TypeNode.Fqn(cn) : null;
            case "delegateInvoke":
                // The RESULT of invoking the delegate, not the delegate itself.
                return TypeJson.Read(o["funcType"]) is TypeNode.Fn dfn ? dfn.Ret : null;
            case "objMethod":
                // The three `Any` slots, by their Kotlin contract.
                return Str(o["method"]) switch
                {
                    "toString" or "ToString" => StringTn,
                    "hashCode" or "GetHashCode" => IntTn,
                    "equals" or "Equals" => BoolTn,
                    _ => null,
                };
            case "this":
                // The enclosing instance: only the OWNER knows its type, and both callers have it in hand.
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The type a `valueBlock` produces: its `result`, derived with the block's OWN `var` declarations in scope. The
    /// block IS their declaration site, so reading them keeps this node-local — and it is the only way the `!!`,
    /// elvis and safe-call desugars can be typed at all, since kotc stamps neither the block, nor the `cond` it
    /// results in, nor the `local` read of the temp the block declares: `{ var __nn = e; __nn != null ? __nn : throw }`
    /// is typed only by `__nn`'s own declaration, one level below the result. A node the block does not declare falls
    /// back to the caller's FULL deriver, so an index-only kind (a call carrying no `sty`) still resolves there.
    /// </summary>
    static TypeNode? BlockResultType(JsonObject block, Func<JsonNode?, TypeNode?> recurse, Func<string, string?>? primArrayElem)
    {
        var vars = BlockVars(block);
        if (vars is null) return recurse(block["result"]);
        TypeNode? InBlock(JsonNode? x)
            => x is JsonObject xo && Str(xo["k"]) == "local" && Str(xo["name"]) is string nm
               && vars.TryGetValue(nm, out var vt)
                ? vt
                : Of(x, InBlock, primArrayElem) ?? recurse(x);
        return InBlock(block["result"]);
    }

    /// <summary>The `var` declarations a block makes, name -> declared type; null when it declares none.</summary>
    static Dictionary<string, TypeNode>? BlockVars(JsonObject block)
    {
        Dictionary<string, TypeNode>? vars = null;
        foreach (var key in new[] { "stmts", "body" })
            if (block[key] is JsonArray arr)
                foreach (var st in arr)
                    if (st is JsonObject so && Str(so["k"]) == "var" && Str(so["name"]) is string nm
                        && TypeJson.Read(so["type"]) is TypeNode vt)
                        (vars ??= new Dictionary<string, TypeNode>(StringComparer.Ordinal))[nm] = vt;
        return vars;
    }

    /// <summary>The ELEMENT of an array type: a structural `Array(E)`, or a specialized `kotlin.IntArray`-style FQN
    /// resolved through the caller's table. Nullability/obliviousness on the array itself is not the element's.</summary>
    public static TypeNode? ElementOf(TypeNode? arrayType, Func<string, string?>? primArrayElem = null) => arrayType switch
    {
        TypeNode.Array a => a.Elem,
        TypeNode.Nullable nl => ElementOf(nl.Of, primArrayElem),
        TypeNode.Oblivious ob => ElementOf(ob.Of, primArrayElem),
        TypeNode.Fqn f when primArrayElem?.Invoke(f.Name) is string e => new TypeNode.Fqn(e),
        _ => null,
    };

    static string? Str(JsonNode? n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
