using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// bir2cir — CrossClassPrivateWidening (bundle-6 P5 BUG A): widen enclosing-class PRIVATE members that a
// LIFTED anon-object / closure class reaches cross-class, so a separate top-level CLR class can access them.
//
// A Kotlin `object : I { … }` inside a class body (or a capturing lambda/closure) is emitted by kotc as a
// SEPARATE top-level CLR class (`<>dotkt_obj*`) that captures its enclosing instance as an `__outer` field
// and reads the enclosing class's members through it — e.g. FilteringSequence.iterator()'s anon Iterator
// reads the private `sequence`/`predicate`/`sendWhen` getters via `__outer`. On the JVM those are legal
// (the anon is a NESTED class with private access); on the CLR a separate top-level class canNOT touch
// another class's `private` member -> System.MethodAccessException at runtime.
//
// This generalizes SuspendColdLowering.WidenPrivatesAccessedBySm (which widens only what a synthesized SM
// touches via its `$this` field) to ANY cross-class access: for every local type T, walk its bodies; for a
// callInstance/callStatic/field/setField whose owner (generics stripped) names a DIFFERENT local type C,
// record (C, member); then relax any matching PRIVATE member (method, field, or property get_/set_ accessor)
// on C to `internal` (assembly-visible). SOUNDNESS: valid Kotlin source can never have a top-level class
// access another top-level class's private member, so ANY cross-class private access in emitted BIR came
// from a lost nesting/lifting relationship — widening exactly those is minimal and correct (Codex-confirmed).
// `internal` keeps the member off the public surface (the enclosing types here are internal stdlib machinery).
//
// Runs GLOBALLY across all files, in NON-ref builds (rt + app), AFTER SuspendColdLowering/SuspendLambdaLowering
// (so synthesized SM types are present and covered too) and BEFORE BirTypeLowering (owner tokens are still the
// kotlin.* FQN that match local type names). Only same-compilation local types are touched; external/ref/CLR
// owners are ignored.
static class CrossClassPrivateWidening
{
    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // Strip a generic instantiation suffix (`Box[gp:T]` -> `Box`) and a leading `@` marker so an owner token
    // matches a local type node's bare FQN name.
    static string BareOwner(string s)
    {
        if (s == null) return null;
        if (s.Length > 0 && s[0] == '@') s = s.Substring(1);
        var i = s.IndexOf('[');
        return i >= 0 ? s.Substring(0, i) : s;
    }

    public static void ApplyAll(IReadOnlyList<JsonNode> roots)
    {
        // 1. Index every local type by its bare FQN name.
        var types = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var r in roots)
            if (r is JsonObject f && f["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to && Str(to["name"]) is string tn)
                        types[tn] = to;
        if (types.Count == 0) return;

        // 2. Collect cross-class member accesses: (ownerClass -> {member names}) reached from a DIFFERENT local
        //    type's bodies.
        var accessed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Record(string owner, string member)
        {
            if (member == null) return;
            var bare = BareOwner(owner);
            if (bare == null || !types.ContainsKey(bare)) return;
            if (!accessed.TryGetValue(bare, out var set))
                accessed[bare] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(member);
        }

        void Walk(JsonNode n, string selfType)
        {
            if (n is JsonObject o)
            {
                switch (Str(o["k"]))
                {
                    // Owner slots are structured Type nodes now — read the Fqn identity directly (BareOwner is then a
                    // no-op on the already-bare name, kept only as a defensive strip for a legacy string owner).
                    case "callInstance":
                        if (BareOwner(TypeJson.OwnerName(o["ownerType"])) is string ci && ci != selfType)
                            Record(TypeJson.OwnerName(o["ownerType"]), Str(o["method"]));
                        break;
                    case "callStatic":
                        if (BareOwner(TypeJson.OwnerName(o["owner"])) is string cs && cs != selfType)
                            Record(TypeJson.OwnerName(o["owner"]), Str(o["method"]));
                        break;
                    case "field":
                    case "setField":
                        if (BareOwner(TypeJson.OwnerName(o["ownerType"])) is string fo && fo != selfType)
                            Record(TypeJson.OwnerName(o["ownerType"]), Str(o["name"]));
                        break;
                }
                foreach (var kv in o) if (kv.Value != null) Walk(kv.Value, selfType);
            }
            else if (n is JsonArray a)
                foreach (var it in a) if (it != null) Walk(it, selfType);
        }

        foreach (var (name, node) in types)
            Walk(node, name);
        if (accessed.Count == 0) return;

        // 3. Relax each accessed PRIVATE member (method / field / property accessor) to internal.
        static void Relax(JsonObject member)
        {
            if (member != null && Str(member["vis"]) == "private") member["vis"] = "internal";
        }
        foreach (var (owner, members) in accessed)
        {
            var t = types[owner];
            if (t["methods"] is JsonArray ms)
                foreach (var m in ms)
                    if (m is JsonObject mo && Str(mo["name"]) is string mn && members.Contains(mn)) Relax(mo);
            if (t["fields"] is JsonArray fs)
                foreach (var f in fs)
                    if (f is JsonObject fo && Str(fo["name"]) is string fn && members.Contains(fn)) Relax(fo);
            if (t["properties"] is JsonArray ps)
                foreach (var p in ps)
                    if (p is JsonObject po && Str(po["name"]) is string pn)
                    {
                        // A property accessed directly by name, or via its get_/set_ accessor method-name convention.
                        if (members.Contains(pn)) Relax(po);
                        if (members.Contains("get_" + pn)) Relax(po["getter"] as JsonObject);
                        if (members.Contains("set_" + pn)) Relax(po["setter"] as JsonObject);
                    }
        }
    }
}
