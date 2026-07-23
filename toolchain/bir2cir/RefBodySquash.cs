using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// REFERENCE-build body squashing. The pure-Kotlin reference stdlib (DotKt.Private.Stdlib.dll) is a METADATA-ONLY
// surface: every declaration keeps its full signature/type/supertype/generic/attribute metadata, but its BODY is
// replaced with a single `throw NotImplementedException()` statement. The ref dll is never executed (it is loaded
// compile-time only and substituted away at app-emit), so a thrown stub is the correct, minimal body.
//
// WHY this is a prerequisite for kotc emitting bare `kotlin.Int`: in the reference build bir2cir keeps `kotlin.*`
// primitive tokens VERBATIM (they are not lowered to the CLR primitive). If a real method body were emitted, IL
// operating on such a bare-value `kotlin.Int` (arithmetic / box / conv) would have no valid CLR primitive to act
// on. Squashing every body to a throw guarantees no such IL is ever produced — the signature carries `kotlin.Int`
// purely as metadata.
//
// Mutates the (already deep-cloned) lowered tree in place. Only the declaration hierarchy that ilemit emits as IL
// bodies is touched: file-level methods, and per-type methods + constructors, recursively through nested types.
// Property accessors are already lowered to `get_X`/`set_X` methods, so they are covered by the method pass.
static class RefBodySquash
{
    public static void Squash(JsonNode root)
    {
        if (root is not JsonObject file) return;
        SquashMethods(file["methods"] as JsonArray, interfaceMembers: false);
        SquashTypes(file["types"] as JsonArray);
    }

    static void SquashTypes(JsonArray types)
    {
        if (types == null) return;
        foreach (var t in types)
        {
            if (t is not JsonObject type) continue;
            SquashMethods(type["methods"] as JsonArray, Str(type["kind"]) == "interface");
            SquashCtors(type["ctors"] as JsonArray);
            SquashTypes(type["types"] as JsonArray);   // nested types (local/object/companion)
        }
    }

    static void SquashMethods(JsonArray methods, bool interfaceMembers)
    {
        if (methods == null) return;
        foreach (var m in methods)
        {
            if (m is not JsonObject method) continue;
            // Abstract/interface members have NO IL body — ilemit refuses a body for them; adding one would be
            // emitted-as-nothing at best and is semantically wrong. A suspend member carries `steps`/`cpsFields`
            // and NO `body` (ilemit emits its own throwing stub under stdlib-compile); leave it untouched. We only
            // squash a member that actually carries a `body` statement array.
            if (IsAbstract(method)) continue;
            if (method["body"] is JsonArray body)
            {
                // Kotlin interface declarations encode an abstract slot as an empty body array and a DIM as a
                // non-empty one.  Preserve that distinction in the reference assembly: body stripping must never
                // turn an abstract slot into a concrete throw-stub DIM, because downstream hierarchy lowering reads
                // MethodInfo.IsAbstract as ABI metadata.
                if (interfaceMembers && body.Count == 0) continue;
                method["body"] = ThrowStubBody();
            }
        }
    }

    static void SquashCtors(JsonArray ctors)
    {
        if (ctors == null) return;
        foreach (var c in ctors)
        {
            if (c is not JsonObject ctor) continue;
            // Squash ONLY the body. Keep `baseArgs`/`thisArgs`: ilemit always emits the base/this constructor call
            // from that metadata before the body, and a base without a default constructor would make a nulled-out
            // base call un-resolvable. The chain-up is the minimal structurally-required prologue; the body throws.
            if (ctor["body"] is JsonArray) ctor["body"] = ThrowStubBody();
        }
    }

    static bool IsAbstract(JsonObject method) =>
        method["abstract"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    // A one-statement body: `throw new System.NotImplementedException()`. Mirrors the existing throw-statement
    // shape ilemit already consumes (see the stdlib's NotSupportedException intrinsic stubs); the same shape kotc
    // emits for `kotlin.TODO()`, only as a statement rather than an expression.
    static JsonArray ThrowStubBody() => new()
    {
        new JsonObject
        {
            ["k"] = "throw",
            ["value"] = new JsonObject
            {
                ["k"] = "newClr",
                ["type"] = TypeJson.Fqn("System.NotImplementedException"),
                ["argTypes"] = new JsonArray(),
                ["args"] = new JsonArray(),
            },
        },
    };
}
