using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// bir2cir — CrossClassPrivateWidening (bundle-6 P5 BUG A): widen enclosing-class PRIVATE members that a
// LIFTED anon-object / closure class reaches cross-class, so a separate top-level CLR class can access them.
//
// A Kotlin `object : I { … }` inside a class body (or a capturing lambda/closure) is emitted by kotc as a
// SEPARATE top-level CLR class (`dotkt_obj*`) that captures its enclosing instance as an `__outer` field
// and reads the enclosing class's members through it — e.g. FilteringSequence.iterator()'s anon Iterator
// reads the private `sequence`/`predicate`/`sendWhen` getters via `__outer`. On the JVM those are legal
// (the anon is a NESTED class with private access); on the CLR a separate top-level class canNOT touch
// another class's `private` member -> System.MethodAccessException at runtime.
//
// This generalizes SuspendColdLowering.WidenPrivatesAccessedBySm (which widens only what a synthesized SM
// touches via its `$this` field) to ANY cross-class access: for every local type T, walk its bodies; for a
// call (callInstance/callStatic), a bound method reference (newBoundDelegate, an `ldftn` over the member), or a
// member field-access of the full node family (field/setField/setFieldExpr/lateinitGet/staticField/staticFieldSet)
// whose owner (generics stripped) names a DIFFERENT local type C, record (C, member); then relax any matching
// PRIVATE member to `internal`, or PROTECTED member to `protectedInternal`. The latter preserves access for
// external subclasses while granting the lifted sibling/state-machine its lost lexical access. SOUNDNESS:
// valid Kotlin source can never have an unrelated top-level class access another class's private/protected
// member, so every such access in emitted BIR came from lifting a lexically privileged declaration.
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
                    // A synthesized/lifted state machine or closure can construct a Kotlin-private nested/local class.
                    // Once both are separate CLR types that constructor is a cross-class access just like a private
                    // method call; record a synthetic member key and widen only the reached constructors.
                    case "new":
                        if (BareOwner(TypeJson.OwnerName(o["type"])) is string cn && cn != selfType)
                            Record(TypeJson.OwnerName(o["type"]), ReferenceMetadataIndex.CtorKeyName);
                        break;
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
                    // A bound method reference `obj::method` — its delegate does an `ldftn` over (ownerType, method).
                    // A `::privateMethod` captured inside a lifted lambda emits this over the enclosing class's private
                    // method from the separate closure class -> MethodAccessException, the same fault class as the
                    // field case. (The .NET-owner variant carries a System.* ownerType, filtered by the local-type
                    // guard in Record; the unbound `Class::method` form lowers to a lifted __mref + callInstance,
                    // already covered above.)
                    case "newBoundDelegate":
                        if (BareOwner(TypeJson.OwnerName(o["ownerType"])) is string bd && bd != selfType)
                            Record(TypeJson.OwnerName(o["ownerType"]), Str(o["method"]));
                        break;
                    // The full member field-access node family — every kind that names a class member by
                    // (ownerType, name): instance read/write (`field`/`setField`/`setFieldExpr`), the null-checked
                    // lateinit read (`lateinitGet`), and the static read/write (`staticField`/`staticFieldSet`).
                    // A lifted PropRef/closure class reaching a private `lateinit var`/`@ClrField` backing field of
                    // its enclosing class does so through `lateinitGet`/`setFieldExpr` (BirEmitterLifts.fieldAccess),
                    // not the accessor call — so these MUST widen too or the emitted IL keeps an inaccessible
                    // private-field reference and throws FieldAccessException at runtime (#155).
                    case "field":
                    case "setField":
                    case "setFieldExpr":
                    case "lateinitGet":
                    case "staticField":
                    case "staticFieldSet":
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
        // An inline member body can be spliced into a TOP-LEVEL function (file class), where it still legally reaches
        // the declaring class's private implementation detail in Kotlin. The file-class methods are not entries in
        // `types`, so scan them explicitly with their own owner identity; otherwise that private accessor remains
        // inaccessible after the inline body crosses the CLR type boundary.
        foreach (var root in roots.OfType<JsonObject>())
        {
            var fileClass = Str(root["fileClass"]);
            if (root["methods"] is JsonNode methods) Walk(methods, fileClass);
            if (root["fields"] is JsonNode fields) Walk(fields, fileClass);
        }
        if (accessed.Count == 0) return;

        // 3. Relax each accessed lexically-visible member (method / field / property accessor) just enough
        // for its lifted CLR sibling. Keep protected reachability for external subclasses.
        static void Relax(JsonObject member)
        {
            if (member == null) return;
            switch (Str(member["vis"]))
            {
                case "private":
                    member["vis"] = "internal";
                    break;
                case "protected":
                    member["vis"] = "protectedInternal";
                    break;
            }
        }
        foreach (var (owner, members) in accessed)
        {
            var t = types[owner];
            // Accessing a public/internal member is still illegal when its nested declaring TYPE is private. A lifted
            // sibling (state machine/closure) has lost Kotlin's lexical nesting privilege, so widen the reached private
            // type itself alongside the exact members. `internal` maps to NestedAssembly for a nested CLR type.
            Relax(t);
            if (members.Contains(".ctor") && t["ctors"] is JsonArray ctors)
                foreach (var ctor in ctors.OfType<JsonObject>()) Relax(ctor);
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

        // CLR forbids an override from reducing accessibility. If a lifted sibling forced a protected virtual
        // base member to protectedInternal, carry that already-resolved CLR visibility through every local override
        // in the hierarchy. This is a bir2cir hierarchy decision; ilemit must not rediscover it while defining slots.
        static int Arity(JsonObject method, string key) =>
            method[key] is JsonArray items ? items.Count : 0;
        static bool IsOverride(JsonObject method) =>
            method["override"] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
        static bool SameSlotShape(JsonObject candidate, JsonObject derived) =>
            Str(candidate["name"]) == Str(derived["name"])
            && Arity(candidate, "params") == Arity(derived, "params")
            && Arity(candidate, "typeParams") == Arity(derived, "typeParams");

        bool HasWidenedBaseSlot(JsonObject type, JsonObject method)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var baseName = BareOwner(TypeJson.OwnerName(type["base"]));
            while (baseName != null && seen.Add(baseName) && types.TryGetValue(baseName, out var baseType))
            {
                if (baseType["methods"] is JsonArray baseMethods
                    && baseMethods.OfType<JsonObject>().Any(candidate =>
                        Str(candidate["vis"]) == "protectedInternal" && SameSlotShape(candidate, method)))
                    return true;
                baseName = BareOwner(TypeJson.OwnerName(baseType["base"]));
            }
            return false;
        }

        foreach (var type in types.Values)
        {
            if (type["methods"] is not JsonArray methods) continue;
            foreach (var method in methods.OfType<JsonObject>())
            {
                if (Str(method["vis"]) == "protected" && IsOverride(method) && HasWidenedBaseSlot(type, method))
                    method["vis"] = "protectedInternal";
            }
        }
    }
}
