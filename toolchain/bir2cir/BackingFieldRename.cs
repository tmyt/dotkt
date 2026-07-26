using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

// AUTO-PROPERTY BACKING-FIELD RENAME (#228). An ACCESSOR-ROUTED Kotlin property becomes a REAL CLR property (get_/set_
// + a `properties` record) plus the storage that backs it. kotc names that storage with the KOTLIN identity — the
// property's own name — because kotc emits Kotlin facts and decides no CLR member name. The resulting CLR type
// therefore carried a property `Value` AND a field `Value`: two same-named members, which reflection-driven libraries
// cannot resolve (Newtonsoft's member grouping keys on the name and drops the pair, so `SerializeObject` silently
// yields `{}`).
//
// bir2cir owns the Kotlin->CLR representation, so the CLR metadata name is minted HERE: `<Name>k__BackingField`, the
// C# auto-property convention. It cannot be written in Kotlin — a backtick-quoted `` `<Value>k__BackingField` `` is
// rejected by the frontend ("name contains illegal characters: <>") — so it can never collide with a user declaration
// nor be referenced from source; and it is per-property unique within its owner, so two properties never share one.
// The field is additionally stamped [System.Runtime.CompilerServices.CompilerGenerated], the standard CLR "this member
// is not user-authored" signal that debuggers, analyzers and serializers key on (C# stamps the same attribute on the
// same field).
//
// DISCRIMINATOR: an INSTANCE field whose owner also declares a `properties` record of the same name. That pairing is a
// CLR-representation fact read off the CIR type declaration — the property record and the field list are exactly the
// inputs to "how is this property's storage named on the CLR", which is this layer's decision; it is deliberately NOT
// a Kotlin-frontend flag. It covers bir2cir's OWN synthesized adapters too (StringCharSequenceBridge's
// `dotkt$StringCharSequence` declares both a `value` field and a `value` property, and had the same clash).
// (DeclarationRename, which runs earlier, rewrites a property record's `get`/`set` but never its `name`, so the
// pairing key is stable.)
//
// OUT OF SCOPE — every property whose storage IS the user-visible member emits NO property record, so none of them is
// touched and every user-visible field keeps its name: a `@ClrField` property (the opt-out that deliberately emits a
// plain field), a `const`, a `lateinit var`, a delegated property's `<p>$delegate`, a companion/top-level static field
// and every capture/synthetic field. A property with a CUSTOM accessor that still has a backing field
// (`val x = 7; get() = field + 1`) IS accessor-routed and IS renamed.
//
// Runs GLOBALLY over all staged roots (a `byref(obj.prop)` addresses a sibling file's backing field directly) and in
// EVERY build — ref, runtime and app agree on the emitted shape. Placed at the end of the global structural phase:
// after inline splicing, closure/suspend synthesis and inherited-owner binding (so every body that reads the field
// exists and names its declaring owner), and before per-file type lowering (owner tokens are still Kotlin FQNs).
static class BackingFieldRename
{
    const string CompilerGeneratedAttr = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    // The instance-field-addressing node kinds: a read, a statement write and an expression write. `staticField`/
    // `staticFieldSet` are deliberately absent — a static field is never an accessor-routed property's storage.
    static readonly HashSet<string> FieldNodeKinds = new(StringComparer.Ordinal) { "field", "setField", "setFieldExpr" };

    public static void ApplyAll(IReadOnlyList<JsonNode> roots)
    {
        // owner FQN -> (Kotlin property name -> mangled backing-field name), and owner FQN -> its declared base, so a
        // field node whose `ownerType` names a SUBCLASS (kotc spells a fake-override property's owner as the receiver's
        // class) still resolves to the base that declares the storage.
        var renames = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots) CollectTypes(root, renames, bases);
        if (renames.Count == 0) return;
        foreach (var root in roots) RewriteUses(root, renames, bases);
    }

    // The CLR metadata name for the backing store of property `prop`.
    internal static string Mangle(string prop) => "<" + prop + ">k__BackingField";

    static void CollectTypes(JsonNode node, Dictionary<string, Dictionary<string, string>> renames,
        Dictionary<string, string> bases)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var t in types)
            if (t is JsonObject td)
            {
                RenameDecls(td, renames, bases);
                CollectTypes(td, renames, bases);   // a nested `types` array, mirroring the other type walkers
            }
    }

    static void RenameDecls(JsonObject td, Dictionary<string, Dictionary<string, string>> renames,
        Dictionary<string, string> bases)
    {
        if (Str(td["name"]) is not string rawName) return;
        var owner = ReferenceMetadataIndex.BareOwnerFqn(rawName);
        if (TypeJson.OwnerName(td["base"]) is string rawBase)
            bases[owner] = ReferenceMetadataIndex.BareOwnerFqn(rawBase);
        if (td["fields"] is not JsonArray fields || td["properties"] is not JsonArray props) return;

        var propNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in props)
            if (p is JsonObject po && Str(po["name"]) is string pn) propNames.Add(pn);
        if (propNames.Count == 0) return;

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fields)
            if (f is JsonObject fo && Str(fo["name"]) is string fn) declared.Add(fn);

        Dictionary<string, string> map = null;
        foreach (var f in fields)
        {
            if (f is not JsonObject fo) continue;
            if ((fo["static"] as JsonValue)?.GetValue<bool>() == true) continue;
            if (Str(fo["name"]) is not string fieldName || !propNames.Contains(fieldName)) continue;
            var mangled = Mangle(fieldName);
            // Belt-and-braces. The frontend already rejects the spelling outright — a backtick-quoted
            // `` `<Value>k__BackingField` `` fails kotc with "name contains illegal characters: <>" — so no user
            // declaration can reach here; this catches a bir2cir producer minting the same name.
            if (declared.Contains(mangled))
                throw new InvalidOperationException(
                    $"bir2cir: '{owner}' already declares a field named '{mangled}'; cannot rename the backing field of property '{fieldName}'");
            fo["name"] = mangled;
            StampCompilerGenerated(fo);
            (map ??= new Dictionary<string, string>(StringComparer.Ordinal))[fieldName] = mangled;
        }
        if (map == null) return;
        // MERGE, never replace: `owner` is the arity-stripped FQN, so two decls could in principle share the key —
        // dropping the earlier map would silently leave its use sites pointing at the old name.
        if (renames.TryGetValue(owner, out var existing))
            foreach (var kv in map) existing[kv.Key] = kv.Value;
        else renames[owner] = map;
    }

    // Append the standard [CompilerGenerated] to the field's applied-attribute array. `attrExternal` is required: the
    // attribute type lives in the BCL, not in the emitted assembly, so ilemit binds its ctor off the references.
    static void StampCompilerGenerated(JsonObject field)
    {
        var attrs = field["attrs"] as JsonArray;
        if (attrs == null) field["attrs"] = attrs = new JsonArray();
        attrs.Add(new JsonObject
        {
            ["attr"] = TypeJson.Fqn(CompilerGeneratedAttr),
            ["attrExternal"] = true,
            ["argTypes"] = new JsonArray(),
            ["args"] = new JsonArray(),
        });
    }

    static void RewriteUses(JsonNode node, Dictionary<string, Dictionary<string, string>> renames,
        Dictionary<string, string> bases)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) is string k && FieldNodeKinds.Contains(k)
                && Str(obj["name"]) is string name
                && TypeJson.OwnerName(obj["ownerType"]) is string rawOwner
                && Resolve(ReferenceMetadataIndex.BareOwnerFqn(rawOwner), name, renames, bases) is string mangled)
                obj["name"] = mangled;
            foreach (var kv in obj) if (kv.Value != null) RewriteUses(kv.Value, renames, bases);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) RewriteUses(it, renames, bases);
    }

    // The mangled name for (owner, field), consulting the owner first and then its base chain — an `override var`
    // re-declares its OWN storage, so the nearest declaring owner wins. null = not an auto-property backing field of
    // any local type (a referenced owner, a `@ClrField`, a delegate/capture/static field).
    static string Resolve(string owner, string name, Dictionary<string, Dictionary<string, string>> renames,
        Dictionary<string, string> bases)
    {
        // Depth-bounded so a malformed self-referential base link cannot spin (a real chain is a handful deep).
        for (var depth = 0; owner != null && depth < 64; depth++)
        {
            if (renames.TryGetValue(owner, out var map) && map.TryGetValue(name, out var mangled)) return mangled;
            owner = bases.TryGetValue(owner, out var b) && b != owner ? b : null;
        }
        return null;
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
