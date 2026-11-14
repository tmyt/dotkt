using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

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
// The local path fires where ilemit would throw: the owner is a locally-declared reference type whose own declaration AND
// resolvable base chain (a local base that declares it, or ANY external/.NET base whose inherited slot ilemit reflects)
// provide no such slot. A type declaring its own override (`Box.toString`) keeps its working direct call/reference; a
// `super.M()` remains nonvirtual; a retained Kotlin Any-slot fact also lets an inherited fake declaration bind to
// its actual Object root. For an interface receiver whose slot ilemit COULD resolve via a super-interface's
// abstract redeclaration (FindInInterfaces), the rebind is a benign no-op-equivalent: both `callvirt <iface>::Slot` and
// `callvirt object::Slot` are virtual dispatch to the runtime type. An imported existential carrier likewise does
// not own the Object virtual slot: use the carried Any fact and the reference ABI, not the carrier's name or members.
static partial class AnySlotRebind
{
    // BCL Object-slot spellings (ObjectSlotRename renames a call's `method` to these before this pass runs).
    static readonly HashSet<string> ObjectSlots = new(System.StringComparer.Ordinal) { "ToString", "GetHashCode", "Equals" };

    internal static bool IsObjectSlot(string method) => ObjectSlots.Contains(method);
    internal static bool HasObjectSlotArity(string method, int argumentCount)
        => IsObjectSlot(method) && argumentCount == (method == "Equals" ? 1 : 0);

    // The explicit Kotlin override fact selects its CLR Object slot. An already-canonical declaration name
    // passes through unchanged when collecting the slots the emitted type declares.
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
                                    var emitted = EmittedObjectSlot(mn, isOverride);
                                    if (ObjectSlots.Contains(emitted)) slots.Add(emitted);
                                }
                        map[n] = new TypeInfo(slots, to["base"] != null ? TypeJson.OwnerName(to["base"]) : null);
                    }
        return map;
    }

    static string EmittedObjectSlot(string name, bool objectOverride)
        => objectOverride && KotlinToSlot.TryGetValue(name, out var slot) ? slot : name;

    public static Dictionary<string, TypeInfo> CollectLocalTypes(IEnumerable<BirFileDeclaration> roots)
    {
        var result = new Dictionary<string, TypeInfo>(System.StringComparer.Ordinal);
        foreach (var file in roots)
            foreach (var type in file.Types.OrEmpty())
            {
                if (type.Kind.OrDefault() is not ("class" or "interface") || !type.Name.HasValue) continue;
                var slots = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var method in type.Methods.OrEmpty())
                    if (method.Name.HasValue)
                    {
                        var emitted = EmittedObjectSlot(method.Name.Value, method.ObjectOverride.OrDefault());
                        if (ObjectSlots.Contains(emitted)) slots.Add(emitted);
                    }
                result[type.Name.Value] = new TypeInfo(slots, (type.Base.OrDefault() as TypeNode.Fqn)?.Name);
            }
        return result;
    }

    // Unbound method-reference lifts carry the selected Any slot without a virtual flag.
    internal static bool IsVirtualToString(BirCallInstance call) =>
        IsVirtualToString(call.Tag, call.Method.OrDefault(), call.AnySlot.OrDefault(),
            call.Virtual.HasValue ? call.Virtual.Value : null, call.Super.OrDefault());

    internal static bool IsVirtualToString(string kind, string method, bool anySlot, bool? virtualCall, bool superCall) =>
        kind == "callInstance" && method == "ToString" && anySlot && virtualCall != false && !superCall;

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

}
