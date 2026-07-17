using System.Collections.Generic;
using System.Text.Json.Nodes;

// INHERITED kotlin.Any universal-method rebind for REFERENCE types (issue #96) — the reference-type sibling of
// EnumMemberBinding (which closes the same gap for value-type enums).
//
// A user reference `class`/`interface` receiver that does NOT declare its own `toString`/`hashCode`/`equals` INHERITS
// them from `kotlin.Any` (== System.Object). kotc emits a plain `callInstance ownerType=<UserType> method=hashCode
// anySlot:true` (the fake override is a member of the receiver's static type, so that type IS the owner), and
// ObjectSlotRename renames it to the BCL slot (`hashCode`->GetHashCode / `toString`->ToString / `equals`->Equals). But
// <UserType> declares no such slot and its `base` is the IMPLICIT kotlin.Any (the field is absent — and an interface
// never has a base), so ilemit's FindMethod — which only reflects a base when `BaseName != null` — dead-ends at
// "method <UserType>.GetHashCode not found" (Emitter.Resolve.cs). The `(x as Any).hashCode()` form works only because
// kotc keeps the kotlin.Any owner and MemberCallSubstitution substitutes it to System.Object; a bare user-type receiver
// never reaches that substitution.
//
// Rebind such a CALL to an `objMethod` (a box-free `callvirt instance object::Slot`, i.e. virtual dispatch to the
// receiver's runtime type) — the exact shape EmitObjMethod services and always-correct Kotlin semantics for a
// reference receiver. The same dead-end reaches a bound method REFERENCE (`p::hashCode`, a `newBoundDelegate` /
// `newBoundClrDelegate` ilemit `ldftn`s against the owner); no `objMethod` form exists for a reference, so its
// `ownerType` is instead RETARGETED to `kotlin.Any` — the universal-owner token the frontend already emits for
// `(p as Any)::hashCode` — and the standard substitution binds System.Object's slot.
//
// It fires ONLY where ilemit would throw: the owner is a locally-declared reference type whose own declaration AND
// resolvable base chain (a local base that declares it, or ANY external/.NET base whose inherited slot ilemit reflects)
// provide no such slot. A type declaring its own override (`Box.toString`) keeps its working direct call/reference; a
// `super.M()` (its own kotlin.Any owner + `super:true`, handled by MemberCallSubstitution as a non-virtual base call)
// never reaches this owner shape. For an interface receiver whose slot ilemit COULD resolve via a super-interface's
// abstract redeclaration (FindInInterfaces), the rebind is a benign no-op-equivalent: both `callvirt <iface>::Slot` and
// `callvirt object::Slot` are virtual dispatch to the runtime type.
static class AnySlotRebind
{
    // BCL Object-slot spellings (ObjectSlotRename renames a call's `method` to these before this pass runs).
    static readonly HashSet<string> ObjectSlots = new(System.StringComparer.Ordinal) { "ToString", "GetHashCode", "Equals" };

    // Kotlin universal-method name -> its BCL Object slot, so CollectLocalTypes reads a declaration whether or not
    // ObjectSlotRename has renamed it yet (the module-wide collection runs before the per-file rename loop).
    static readonly Dictionary<string, string> KotlinToSlot = new(System.StringComparer.Ordinal)
    {
        ["toString"] = "ToString", ["hashCode"] = "GetHashCode", ["equals"] = "Equals",
    };

    public readonly struct TypeInfo
    {
        public TypeInfo(HashSet<string> declaredSlots, string baseName) { DeclaredSlots = declaredSlots; BaseName = baseName; }
        public HashSet<string> DeclaredSlots { get; }   // the universal slots this type DECLARES (BCL-spelled)
        public string BaseName { get; }                 // the base's bare FQN name; null == the implicit kotlin.Any
    }

    // Collect every local reference `class`/`interface` (module-wide — a call site may live in a different .bir.json
    // than the declaration, and ilemit's emitted-type table is assembly-wide) -> the universal slots it DECLARES + its
    // base's bare name (an interface has no base -> null). Value-type enums (`kind:"enum"`) are EnumMemberBinding's.
    public static Dictionary<string, TypeInfo> CollectLocalTypes(IEnumerable<JsonNode> roots)
    {
        var map = new Dictionary<string, TypeInfo>(System.StringComparer.Ordinal);
        foreach (var root in roots)
            if (root is JsonObject ro && ro["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to
                        && (to["kind"] as JsonValue)?.TryGetValue<string>(out var kind) == true && (kind == "class" || kind == "interface")
                        && (to["name"] as JsonValue)?.TryGetValue<string>(out var n) == true)
                    {
                        var slots = new HashSet<string>(System.StringComparer.Ordinal);
                        if (to["methods"] is JsonArray ms)
                            foreach (var m in ms)
                                if (m is JsonObject mo && (mo["name"] as JsonValue)?.TryGetValue<string>(out var mn) == true)
                                {
                                    // The slot ilemit's name-keyed method table will hold = ObjectSlotRename's EMITTED
                                    // name: an `objectOverride` decl (a genuine kotlin.Any universal override —
                                    // arity/receiver-gated by kotc's isAnySlotMethod) is renamed to its BCL slot; every
                                    // other method keeps its name. So a class "declares" a slot iff its EMITTED name is a
                                    // universal slot. A same-name NON-override overload (`fun toString(pretty: Boolean)`,
                                    // arity 1) is NOT renamed (stays `toString`) and does NOT shadow the arity-0 universal
                                    // call, so it must NOT count — else a real `p.toString()` is left un-rebound and
                                    // dead-ends. (A method literally named `ToString`/`GetHashCode`/`Equals` DOES emit
                                    // under that name, so ilemit resolves it -> count it.)
                                    var isOverride = (mo["objectOverride"] as JsonValue)?.TryGetValue<bool>(out var ov) == true && ov;
                                    var emitted = isOverride && KotlinToSlot.TryGetValue(mn, out var slot) ? slot : mn;
                                    if (ObjectSlots.Contains(emitted)) slots.Add(emitted);
                                }
                        map[n] = new TypeInfo(slots, to["base"] != null ? TypeJson.OwnerName(to["base"]) : null);
                    }
        return map;
    }

    public static void Apply(JsonNode root, Dictionary<string, TypeInfo> types) => Walk(root, types);

    static void Walk(JsonNode node, Dictionary<string, TypeInfo> types)
    {
        switch (node)
        {
            case JsonObject obj:
                Rebind(obj, types);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, types);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, types);
                break;
        }
    }

    static void Rebind(JsonObject obj, Dictionary<string, TypeInfo> types)
    {
        if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) != true) return;
        var isCall = k == "callInstance";
        var isDelegate = k is "newBoundDelegate" or "newBoundClrDelegate";
        if (!isCall && !isDelegate) return;
        if ((obj["method"] as JsonValue)?.TryGetValue<string>(out var method) != true || !ObjectSlots.Contains(method)) return;
        // A `super.M()` rides in with its own kotlin.Any owner + `super:true` and must stay a non-virtual base call
        // (a callvirt would re-dispatch to THIS type's override and infinite-loop, #14). Belt-and-braces: its owner
        // (kotlin.Any) is never in `types` anyway, so the owner gate below already excludes it.
        if ((obj["super"] as JsonValue)?.TryGetValue<bool>(out var sup) == true && sup) return;
        var owner = TypeJson.OwnerName(obj["ownerType"]);
        if (owner == null || !types.ContainsKey(owner)) return;     // non-local owner -> resolved by another binder
        if (ResolvesSlot(types, owner, method)) return;             // declared here or on a resolvable base

        if (isDelegate)
        {
            // A bound method reference `p::hashCode` on a non-resolving reference type — a `newBoundDelegate` /
            // `newBoundClrDelegate` that ilemit `ldftn`s against the owner, so it dead-ends identically. No `objMethod`
            // form exists for a method reference; instead RETARGET the owner to `kotlin.Any` — the exact universal-owner
            // token the frontend emits for `(p as Any)::hashCode` (verified to compile), so the standard substitution
            // binds System.Object::GetHashCode and ilemit `ldvirtftn`s the runtime slot with the unchanged `virtual`.
            obj["ownerType"] = TypeJson.Fqn("kotlin.Any");
            return;
        }

        if (obj["recv"] is not JsonNode recv) return;
        // Object-slot arity gate (matches EmitObjMethod): Equals takes exactly one arg (moved args[0] -> `arg`),
        // ToString/GetHashCode take none. A mismatch falls through to the original `callInstance` (a clear "method not
        // found") rather than being miscast into an `objMethod` EmitObjMethod can't service.
        var args = obj["args"] as JsonArray;
        var argc = args?.Count ?? 0;
        if (method == "Equals" ? argc == 1 : argc == 0)
            ToObjMethod(obj, recv, method, method == "Equals" ? args[0] : null);
    }

    // A local type RESOLVES the slot (so ilemit's FindMethod already finds it — leave the call alone) iff it or a local
    // ancestor declares the override, or its chain reaches an EXTERNAL base (a non-local `base`, incl. a `.NET` base)
    // whose inherited slot ilemit reflects (Emitter.Resolve.cs, `BaseName != null` fallback). A chain terminating at the
    // implicit kotlin.Any (`base` absent -> null, ALWAYS for an interface) with no declaration does NOT resolve -> the
    // call dead-ends -> rebind. (A super-interface abstract redeclaration that ilemit could reach via FindInInterfaces
    // is NOT walked here; the resulting rebind is dispatch-equivalent — both callvirt the runtime type's slot.)
    static bool ResolvesSlot(Dictionary<string, TypeInfo> types, string typeName, string slot)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var cur = typeName;
        while (cur != null && seen.Add(cur))
        {
            if (!types.TryGetValue(cur, out var info)) return true;    // external/BCL base -> reflection finds the slot
            if (info.DeclaredSlots.Contains(slot)) return true;
            cur = info.BaseName;
        }
        return false;                                                  // terminated at kotlin.Any (or a cycle) -> unresolved
    }

    // Rewrite `obj` in place into an `objMethod` node (ilemit callvirt's the System.Object virtual slot). Mirrors
    // EnumMemberBinding.ToObjMethod; strips every callInstance-only key so no stale hint survives.
    static void ToObjMethod(JsonObject obj, JsonNode recv, string method, JsonNode arg)
    {
        var newNode = new JsonObject
        {
            ["k"] = "objMethod",
            ["method"] = method,
            ["recv"] = recv.DeepClone(),
        };
        if (arg != null) newNode["arg"] = arg.DeepClone();
        foreach (var key in new[] { "ownerType", "virtual", "method", "args", "arg", "sig", "dynRet", "ret", "overrides",
                                    "recv", "typeArgs", "shapeTypes", "argTypes", "anySlot", "super" })
            obj.Remove(key);
        foreach (var kv in newNode) obj[kv.Key] = kv.Value.DeepClone();
    }
}
