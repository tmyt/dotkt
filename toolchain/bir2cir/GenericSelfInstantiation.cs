using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// bir2cir — GenericSelfInstantiation (bundle-6 P5 BUG A part-2): a lifted GENERIC anon-object / closure class
// (`<>dotkt_obj*<T>`) emits its SELF instance accesses with the BARE type name as `ownerType`
// (`<>dotkt_obj144`, no type args) — so at runtime .NET throws InvalidOperationException "the method itself or
// the containing type is not fully instantiated": the emitted `this.get_nextState()` targets the OPEN generic
// type definition instead of the constructed self `<>dotkt_obj144<!0>`.
//
// A NORMAL generic class already emits its self-calls with the constructed token `Owner[gp:T]` (IndexingIterable
// etc. resolve fine); the lifted-anon-object lowering path is the one that leaves the self owner OPEN. Per the
// BIR-type-token contract (kotc emits the FQN IDENTITY, bir2cir/ilemit DERIVE the CLR resolution — the `[gp:T]`
// instantiation), deriving the constructed self here is exactly bir2cir's job.
//
// Rewrite, within any generic local type T (typeParams p1..pn): every callInstance/field/setField whose
// `ownerType` bare-equals T's own name (no existing `[`) -> `T[gp:p1,...,gp:pn]`. Codex-confirmed guardrails:
// only inside a generic declaration, only the executable instance-access shapes, never a double-instantiation,
// never a static `owner`/base/interface token. BIR types are top-level (lifted classes are extracted flat), so
// a per-type walk is lexically sound. Runs BEFORE BirTypeLowering (which then lowers `T[gp:...]` to the CLR
// constructed generic).
static class GenericSelfInstantiation
{
    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static List<string> TypeParamNames(JsonNode tps)
    {
        var names = new List<string>();
        if (tps is JsonArray a)
            foreach (var t in a)
                if (t is JsonValue v && v.TryGetValue<string>(out var s)) names.Add(s);
                else if (t is JsonObject o && Str(o["name"]) is string n) names.Add(n);
        return names;
    }

    public static void ApplyAll(IReadOnlyList<JsonNode> roots)
    {
        foreach (var r in roots)
            if (r is JsonObject f && f["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to && Str(to["name"]) is string self)
                    {
                        var tps = TypeParamNames(to["typeParams"]);
                        if (tps.Count == 0) continue;
                        // The constructed self: `Self<!0,…,!n-1>` — the type-scope generic params by FLATTENED position
                        // (a lifted anon-object is extracted flat, so its own params are indices 0..n-1). bir2cir derives
                        // this CLR instantiation from the FQN identity kotc emitted.
                        var inst = new TypeNode.Fqn(self,
                            Enumerable.Range(0, tps.Count).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
                        Walk(to, self, inst);
                    }
    }

    static void Walk(JsonNode n, string self, TypeNode.Fqn inst)
    {
        if (n is JsonObject o)
        {
            switch (Str(o["k"]))
            {
                case "callInstance":
                case "field":
                case "setField":
                    // Only the BARE open self owner (`Self`, no args) — a call already carrying args stays put.
                    if (TypeJson.Read(o["ownerType"]) is TypeNode.Fqn { Args: null } f && f.Name == self)
                        o["ownerType"] = TypeJson.Write(inst);
                    break;
            }
            foreach (var kv in o) if (kv.Value != null) Walk(kv.Value, self, inst);
        }
        else if (n is JsonArray a)
            foreach (var it in a) if (it != null) Walk(it, self, inst);
    }
}
