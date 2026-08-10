using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CHAR.CODE + FUNCTION.INVOKE LOWERING (#73 Phase 2b-2): two single-node Kotlin<->CLR recognitions relocated out of
// kotc (BirEmitter) so kotc emits ONLY the faithful Kotlin fact and bir2cir realizes the CLR node.
//
//  (1) `c.code` (Char -> Int code point). kotc emits the FAITHFUL top-level extension-property getter call by the
//      property's BARE identity + a `"prop":"get"` accessor-KIND marker (#81; the same convention every top-level
//      extension-property accessor now uses): `callStatic owner:null method:code prop:get sig:[kotlin.Char]
//      args:[<char>]` (the stdlib `val Char.code: Int` in kotlin.CharCodeKt). This pass re-emits the `{k:conv,
//      to:kotlin.Int, e:<char>}` node kotc used to synthesize — the char value AS an int (a genuine primitive IL
//      op), distinct from `.toInt()`'s @ClrConv routing. Recognized by the bare name + the `get` marker + the Char
//      receiver in `sig`; the owner (kotlin.CharCodeKt) + Int return are confirmed against the ref.dll when available.
//
//  (2) `f(x)` invoking a function-typed value. kotc emits the FAITHFUL `callInstance ownerType:kotlin.FunctionN[..]`
//      (or `kotlin.reflect.KFunctionN[..]`) `method:invoke recv:<f> args:[..]` member call. This pass re-emits the
//      `{k:delegateInvoke, funcType, recv, args}` node kotc used to synthesize (a function value IS a delegate at the
//      CLR level). `funcType` = the fn type reconstructed from the FunctionN owner's type args (params = all but the
//      last, ret = the last) — byte-identical to the former `birType(recv.type)`.
//
// Runs EARLY (before NetInteropBinding / MemberCallSubstitution / any type-erasing pass) and UNCONDITIONALLY (ref +
// app), reproducing the flow that existed when kotc emitted `conv`/`delegateInvoke` directly — so every downstream
// pass (type lowering, the suspend/closure passes that CONSUME delegateInvoke) sees the exact same tree shape.
static class CharCodeInvokeLowering
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs = null) => Walk(root, refs);

    // Bottom-up: lower a node's CHILDREN first, then the node itself.
    static void Walk(JsonNode node, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonNode child) Walk(child, refs);
                    if (o[key] is JsonObject co && Lower(co, refs) is JsonNode r) o[key] = r;
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonNode child) Walk(child, refs);
                    if (a[i] is JsonObject co && Lower(co, refs) is JsonNode r) a[i] = r;
                }
                break;
        }
    }

    static JsonNode Lower(JsonObject o, ReferenceMetadataIndex refs)
    {
        var k = (o["k"] as JsonValue)?.GetValue<string>();
        if (k == "callStatic") return LowerCharCode(o, refs);
        if (k == "callInstance") return LowerInvoke(o);
        return null;
    }

    // `callStatic owner:null method:code prop:get sig:[kotlin.Char] args:[<char>]` -> `{k:conv, to:kotlin.Int, e:<char>}`.
    static JsonNode LowerCharCode(JsonObject o, ReferenceMetadataIndex refs)
    {
        // Only a TOP-LEVEL call (`owner:null`, no `ownerType`); a member/.NET call carries `ownerType`.
        if (o.ContainsKey("ownerType")) return null;
        if (!o.ContainsKey("owner") || o["owner"] != null) return null;
        // #81/#397: the getter arrives as the bare property identity `code` plus an explicit get role. Consume that
        // semantic shape before the physical binding boundary; no MethodDef spelling participates in this intrinsic.
        if ((o["method"] as JsonValue)?.GetValue<string>() != "code"
            || (o["prop"] as JsonValue)?.GetValue<string>() != "get") return null;
        if (o["args"] is not JsonArray args || args.Count != 1) return null;
        // The extension receiver is `sig[0]` — must be kotlin.Char (the sole `.code` extension property is on Char).
        if (o["sig"] is not JsonArray sig || sig.Count < 1
            || TypeJson.Read(sig[0]) is not TypeNode.Fqn recvT
            || ReferenceMetadataIndex.BareOwnerFqn(recvT.Name) != "kotlin.Char") return null;
        // The frontend-resolved property call already carries its return type. Use that semantic fact directly; the
        // referenced MethodDef spelling is a later physical concern and is not a lookup key here.
        var convTo = TypeJson.Read(o["ret"]) is TypeNode.Fqn ret
            && ReferenceMetadataIndex.BareOwnerFqn(ret.Name) == "kotlin.Int"
            ? TypeJson.Write(ret) : TypeJson.Fqn("kotlin.Int");
        return new JsonObject { ["k"] = "conv", ["to"] = convTo, ["e"] = args[0]?.DeepClone() };
    }

    // `callInstance ownerType:kotlin.FunctionN[T1..Tn,R]/kotlin.reflect.KFunctionN[..] method:invoke recv:<f> args:[..]`
    // -> `{k:delegateInvoke, funcType:{t:fn,suspend:false,ret:R,params:[T1..Tn]}, recv:<f>, args:[..]}`.
    static JsonNode LowerInvoke(JsonObject o)
    {
        if ((o["method"] as JsonValue)?.GetValue<string>() != "invoke") return null;
        if (o["recv"] is not JsonNode recv) return null;
        if (TypeJson.Read(o["ownerType"]) is not TypeNode.Fqn owner) return null;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(owner.Name);
        if (!(bare.StartsWith("kotlin.Function", StringComparison.Ordinal)
              || bare.StartsWith("kotlin.reflect.KFunction", StringComparison.Ordinal))) return null;
        // FunctionN[T1..Tn, R] -> params = all but the last, ret = the last. A raw FunctionN with no args cannot be a
        // real function value -> leave the call untouched.
        if (owner.Args == null || owner.Args.Length < 1) return null;
        var n = owner.Args.Length - 1;
        var funcType = TypeNode.Write(new TypeNode.Fn(false, owner.Args[n], owner.Args.Take(n).ToArray()));
        var outArgs = new JsonArray();
        if (o["args"] is JsonArray a) foreach (var arg in a) outArgs.Add(arg?.DeepClone());
        return new JsonObject
        {
            ["k"] = "delegateInvoke",
            ["funcType"] = funcType,
            ["recv"] = recv.DeepClone(),
            ["args"] = outArgs,
        };
    }
}
