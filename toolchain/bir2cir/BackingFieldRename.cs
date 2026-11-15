using System;
using System.Collections.Generic;

// AUTO-PROPERTY BACKING-FIELD RENAME (#228). An ACCESSOR-ROUTED Kotlin property becomes a REAL CLR property (dedicated
// accessor methods plus explicit links in a `properties` record) and the storage that backs it. kotc names that
// storage with the KOTLIN identity — the property's own name — because kotc emits Kotlin facts and decides no CLR
// member name. The resulting CLR type
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
// DISCRIMINATOR: a field whose owner also declares a RECEIVERLESS `properties` record of the same name. A Kotlin
// extension/context property may legally share its source name with an independent field-backed property, so a
// receiver-bearing Property row does not own that field. The exact accessor descriptors already allocated by bir2cir
// distinguish those shapes without parsing either member's physical name. This covers bir2cir's synthesized adapters too
// (StringCharSequenceBridge's
// `dotkt$StringCharSequence` declares both a `value` field and a `value` property, and had the same clash).
// (DeclarationRename, which runs earlier, rewrites a property record's `get`/`set` but never its `name`, so the
// pairing key is stable.)
//
// STATIC storage is in scope on exactly the same terms. A Kotlin 2.4 `companion { val v = … }` is an accessor-routed
// property of its class whose storage happens to be static, and ECMA-335 name uniqueness within a TypeDef does not
// care whether the clashing pair is static: a static field `v` alongside a property `v` is the same broken metadata.
//
// OUT OF SCOPE — every property whose storage IS the user-visible member emits NO property record, so none of them is
// touched and every user-visible field keeps its name: a `@ClrField` property (the opt-out that deliberately emits a
// plain field), a `const`, a delegated property's `<p>$delegate`, a default-only top-level property,
// an `object`'s `INSTANCE`, an enum entry's singleton and every capture/synthetic field. A property with a CUSTOM
// accessor that still has a backing field (`val x = 7; get() = field + 1`) IS accessor-routed and IS renamed.
//
// Runs GLOBALLY over all staged roots (a `byref(obj.prop)` addresses a sibling file's backing field directly) and in
// EVERY build — ref, runtime and app agree on the emitted shape. Placed at the end of the global structural phase:
// after inline splicing, closure/suspend synthesis and inherited-owner binding (so every body that reads the field
// exists and names its declaring owner), and before per-file type lowering (owner tokens are still Kotlin FQNs).
static partial class BackingFieldRename
{
    const string CompilerGeneratedAttr = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    // The CLR metadata name for the backing store of property `prop`.
    internal static string Mangle(string prop) => "<" + prop + ">k__BackingField";

    static void Merge(Dictionary<string, Dictionary<string, string>> into, string owner, Dictionary<string, string> map)
    {
        if (map == null) return;
        if (into.TryGetValue(owner, out var existing))
            foreach (var kv in map) existing[kv.Key] = kv.Value;
        else into[owner] = map;
    }

    // Consult the owner first and then its base chain: the nearest field declaration wins, whether or not
    // it owns a property. null means no declaration of the addressed storage kind exists in the local chain.
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

}
