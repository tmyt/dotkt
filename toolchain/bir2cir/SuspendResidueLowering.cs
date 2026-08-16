using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// bir2cir — the PHYSICAL BODY of a suspend declaration the cold-core lowering deliberately does not lower, and the
// disposal of the Kotlin `suspend` modifier at the CIR boundary.
//
// SuspendColdLowering rewrites every declared `suspend fun` into its cold Continuation shape (SM class + cold entry +
// Task bridge). Two mechanisms in that pass leave the `suspend` modifier on a declaration that still reaches this
// point, and BOTH are stdlib-self-build only:
//
//   * the self-build RETAINS the original declaration beside its cold entry (ApplyAll's `!baseIsLocal` removal gate) —
//     kotc's pre-ignition @RestrictsSuspension builder path (`sequence{}`/`iterator{}`) still calls SequenceScope's
//     yield/yieldAll by name, so the Kotlin-shaped slot must stay declared;
//   * the admit gate excludes an `inline` suspend fun outside an app build (IsColdCandidate / IsMemberColdCandidate) —
//     that is how the coroutine PRIMITIVES, whose call sites are reconstructed inline instead, keep their standalone
//     declarations.
//
// Neither category has a state-machine form: the body kotc emitted for it suspends over a continuation that was never
// materialized, so there is no CIL that means it. Its physical body is therefore a call-time throw, and bir2cir STATES
// it here as ordinary CIR — the same `throw new NotSupportedException(msg)` shape SuspendColdLowering's ColdEntryStub
// uses for a concrete-but-not-segmentable member. `bodyTerminates` records that the authored body cannot fall through,
// so no synthetic trailing return follows it.
//
// The REFERENCE build does not run this pass (Program.cs): RefBodySquash replaces EVERY body there with the
// metadata-only `throw NotImplementedException()`, which is already the physical body of a declaration that cannot be
// executed, and the ref build keeps kotlin.* type tokens verbatim — an authored `NotSupportedException(kotlin.String)`
// would have no CLR constructor to bind.
//
// An APP build has neither mechanism — every suspend declaration is lowered, unconditionally, and a non-segmentable
// shape gets a call-time-throw COLD ENTRY rather than a retained original. A survivor there is a cold-lowering MISS, so
// this pass fails loud naming the declaration, at the layer that owns the transform.
//
// Finally, `mods.suspend` itself is Kotlin frontend vocabulary with no CLR meaning. Its consumers all live in bir2cir —
// the cold lowering above, and RoundtripMetadata's [KotlinFunction(Suspend)] stamp — so DropModifier removes it once
// that stamp is written. CIR describes a physical CLR graph; ilemit has no coroutine semantics to apply to the flag,
// and IrSanity refuses one that survives.
static class SuspendResidueLowering
{
    // Author the physical body of every un-lowered suspend declaration (or refuse one in an app build). Runs after
    // BOTH suspend phases and before type lowering, so its nodes flow through BirTypeLowering and ClrMemberResolution
    // like any other bir2cir-authored call.
    public static void ApplyAll(IReadOnlyList<JsonNode> roots, bool appBuild)
    {
        foreach (var root in roots)
        {
            if (root is not JsonObject file) continue;
            StubMethods(file["methods"] as JsonArray, Str(file["fileClass"]) ?? "?", appBuild);
            StubTypes(file["types"] as JsonArray, appBuild);
        }
    }

    static void StubTypes(JsonArray types, bool appBuild)
    {
        if (types == null) return;
        foreach (var t in types)
        {
            if (t is not JsonObject type) continue;
            StubMethods(type["methods"] as JsonArray, Str(type["name"]) ?? "?", appBuild);
            StubTypes(type["types"] as JsonArray, appBuild);   // nested types (local/object/companion)
        }
    }

    static void StubMethods(JsonArray methods, string owner, bool appBuild)
    {
        if (methods == null) return;
        foreach (var m in methods)
        {
            if (m is not JsonObject method || !IsSuspend(method)) continue;
            var name = Str(method["name"]) ?? "?";
            if (appBuild)
                throw new NotSupportedException(
                    $"bir2cir: suspend-lowering: '{owner}.{name}' reached CIR still carrying the `suspend` modifier. "
                    + "Every suspend declaration in an application build is transformed into its cold Continuation "
                    + "shape (state machine + `$dotkt_suspend` cold entry + public Task bridge), and a shape the v1 "
                    + "classifier refuses gets a call-time-throw COLD ENTRY rather than a retained original — so this "
                    + "is a cold-lowering MISS, not a supported residue.");
            // An abstract declaration owns no body slot; there is nothing to author and ilemit emits no IL for it.
            if (method["body"] is not JsonArray) continue;
            method["body"] = ThrowStubBody(
                $"{owner}.{name}: this suspend declaration is left un-lowered by bir2cir's cold-core coroutine "
                + "lowering (docs/design-coroutine-cold-core-task-bridge.md §11), so it has no state-machine body "
                + "and cannot be invoked; its cold entry is the callable ABI.");
            method["bodyTerminates"] = true;
        }
    }

    // Remove the Kotlin-only `suspend` modifier from every declaration of a lowered file. Called once per file, after
    // RoundtripMetadata has stamped the Kotlin round-trip attributes that carry the fact into metadata.
    public static void DropModifier(JsonNode root)
    {
        if (root is not JsonObject file) return;
        DropInMethods(file["methods"] as JsonArray);
        DropInTypes(file["types"] as JsonArray);
    }

    static void DropInTypes(JsonArray types)
    {
        if (types == null) return;
        foreach (var t in types)
        {
            if (t is not JsonObject type) continue;
            DropInMethods(type["methods"] as JsonArray);
            DropInTypes(type["types"] as JsonArray);
        }
    }

    static void DropInMethods(JsonArray methods)
    {
        if (methods == null) return;
        foreach (var m in methods)
            if (m is JsonObject method && method["mods"] is JsonObject mods)
                mods.Remove("suspend");
    }

    // `throw new System.NotSupportedException("<msg>")` — one statement, in the pre-lowering vocabulary the rest of the
    // pipeline expects: BirTypeLowering lowers the kotlin.String slots and ClrMemberResolution binds the exact ctor.
    static JsonArray ThrowStubBody(string msg) => new()
    {
        new JsonObject
        {
            ["k"] = "throw",
            ["value"] = new JsonObject
            {
                ["k"] = "newClr",
                ["type"] = TypeJson.Fqn("System.NotSupportedException"),
                ["argTypes"] = new JsonArray { TypeJson.Fqn("kotlin.String") },
                ["args"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "const",
                        ["type"] = TypeJson.Fqn("kotlin.String"),
                        ["value"] = msg,
                    },
                },
            },
        },
    };

    static bool IsSuspend(JsonObject method) =>
        method["mods"] is JsonObject mods && mods["suspend"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
