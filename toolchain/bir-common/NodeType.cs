// THE NODE-LOCAL STATIC TYPE of an expression node: "what type does this node's own content say it produces?"
//
// Two bir2cir sites mint a local for a value taken out of an expression — the suspend lowering's evaluation-order
// spill, and the call-evaluation plan's address pins — and a local without a type is not a lesser local, it is
// unverifiable IL. Both need the same answer, derived the same way, so the derivation lives here once.
//
// SCOPE: node-local facts only. An explicit `ret`/`dynRet`/`sty` stamp, then whatever slot the kind carries its own
// result type in (`arrayGet.elem`, `conv.to`, `delegateInvoke.funcType.ret`, …). A kind whose type is only knowable
// from an INDEX — a `callStatic`/`callInstance` with no `sty`, a raw `field` read — returns null here; a caller that
// owns such an index (SuspendColdLowering does) supplies it and passes itself as `recurse`, so an operand of a
// `binOp` still resolves through the caller's full deriver rather than falling back to this core.
//
// Returning NULL is a real answer: the caller decides whether that is an error. Neither caller may substitute
// `kotlin.Any` — that boxes a value type and hides a type the CLR would refuse.

#nullable enable
using System;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

public static class NodeType
{
    static readonly TypeNode IntTn = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode BoolTn = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode StringTn = new TypeNode.Fqn("kotlin.String");

    /// <summary>
    /// The node's own static type, or null when only an index could answer. <paramref name="recurse"/> is the
    /// caller's FULL deriver, used for the kinds whose type is an OPERAND's type (`binOp`, `unaryOp`, `arrayGet`);
    /// it defaults to this core. <paramref name="primArrayElem"/> maps a SPECIALIZED array FQN to its element
    /// (`kotlin.IntArray` -> `kotlin.Int`) — a Kotlin fact this file deliberately does not restate, so the caller
    /// passes the one table the toolchain already keeps (bir2cir's <c>BirTypeLowering.PrimArrayElem</c>).
    /// </summary>
    public static TypeNode? Of(JsonNode? n, Func<JsonNode?, TypeNode?>? recurse = null,
                               Func<string, string?>? primArrayElem = null)
    {
        if (n is not JsonObject o) return null;
        recurse ??= x => Of(x, null, primArrayElem);
        if (TypeJson.Read(o["ret"]) is TypeNode t0) return t0;
        if (TypeJson.Read(o["dynRet"]) is TypeNode t2) return t2;
        if (TypeJson.Read(o["sty"]) is TypeNode ts) return ts;
        switch (Str(o["k"]))
        {
            case "const": case "cast": case "new": case "newClr": case "var": case "cond":
            case "nullableWrap": case "nullableValue": case "safeCastValue": case "default":
                return TypeJson.Read(o["type"]);
            case "valueBlock":
                // A spliced inline call is a `valueBlock {stmts, result}` and carries NO `type` stamp — its type is its
                // RESULT's, resolved with the block's own `var`s in scope (an `apply`-splice's result is a local the
                // block itself declares). Mirrors StaticType.Surface's arm.
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
                return Str(o["op"]) is "==" or "!=" or "<" or ">" or "<=" or ">=" ? BoolTn : recurse(o["lhs"]);
            case "unaryOp":
                return Str(o["op"]) == "!" ? BoolTn : recurse(o["e"]);
            case "objEq": case "isInst": case "isInstRef": case "nullableHasValue":
                return BoolTn;
            case "arrayLen": case "enumOrdinal":
                return IntTn;
            case "concat":
                return StringTn;
            case "newArray": case "newArrayInit": case "newArraySized":
                return TypeJson.Read(o["elem"]) is TypeNode ae ? new TypeNode.Array(ae) : null;
            case "newClosure": case "newDelegate": case "newSam": case "newSuspendLambda":
            case "newBoundDelegate": case "newBoundClrDelegate":
                if (TypeJson.Read(o["funcType"]) is TypeNode ft) return ft;
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

    /// <summary>The type a `valueBlock` produces: its `result`, resolved against the `var`s the block declares.</summary>
    static TypeNode? BlockResultType(JsonObject block, Func<JsonNode?, TypeNode?> recurse, Func<string, string?>? primArrayElem)
    {
        var result = block["result"];
        if (result is JsonObject r && Str(r["k"]) == "local" && Str(r["name"]) is string want)
            foreach (var key in new[] { "stmts", "body" })
                if (block[key] is JsonArray arr)
                    foreach (var st in arr)
                        if (st is JsonObject so && Str(so["k"]) == "var" && Str(so["name"]) == want
                            && TypeJson.Read(so["type"]) is TypeNode vt) return vt;
        return recurse(result);
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
