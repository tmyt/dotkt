using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// DECLARATION-NAME RENAME (clrName migration, Step 2a). kotc tags each emitted method/accessor with a pure-Kotlin
// `overrides` marker (the transitive override closure, in Kotlin terms). This pass derives the BCL slot name from the
// ref.dll @ClrIntrinsic on the FIRST overridden member that carries one (a `size` getter override of
// Collection.size@ClrIntrinsic("Count") -> get_Count; resumeWith -> ResumeWith) — replacing what kotc's clrName/annClr
// resolves today. While annClr still runs in kotc the rename is IDEMPOTENT (it reproduces the existing name), so the
// emit stays byte-identical; once annClr is removed (Step 3) this becomes the sole source of the slot name. Mutates the
// method nodes in place; the `overrides` marker is stripped later by BirTypeLowering. (Object-method names like ToString
// and the hardcoded close->Dispose map are NOT @ClrIntrinsic, so TryMemberIntrinsic returns false and the kotc-supplied
// name is left untouched — those stay kotc's concern.)
static class DeclarationRename
{
    // Recursively rename to the BCL slot every node carrying an `overrides` marker: a method/accessor DECLARATION (its
    // `name`) and a CALL node (`callInstance`'s `method`) alike, so the implementor-side call `AbstractList.get_size`
    // tracks the renamed declaration `get_Count`. Runs BEFORE MemberCallSubstitution so a now-`get_Count` call on a
    // CLR-bound owner still falls through to clrPropGet. Idempotent while annClr is active (reproduces the kotc name).
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) => Walk(root, refs, false);

    static void Walk(JsonNode node, ReferenceMetadataIndex refs, bool inIface)
    {
        if (node is JsonObject obj)
        {
            // Track whether we're inside an INTERFACE type def: kotc's ifaceMethod hardcodes `override:false` for
            // interface members (even ones that bind a CLR slot), so bir2cir must NOT stamp override:true there.
            if ((obj["kind"] as JsonValue)?.GetValue<string>() is string k) inIface = k == "interface";
            if (obj["overrides"] is JsonArray ovs)
            {
                // Property rows retain only their semantic association here. Their exact physical getter/setter links
                // are allocated later from the renamed accessor declarations, so this pass has no second Property-link
                // naming rule and cannot leave a stale link behind.
                var semanticPropertyCall = (obj["k"] as JsonValue)?.GetValue<string>() == "callInstance"
                    && KotlinPropertyAccessors.TryCallIdentity(obj, out _, out _);
                if (obj[KotlinPropertyAccessors.PropertyRolesKey] is not JsonArray && !semanticPropertyCall
                    && ResolveSlot(obj, ovs, refs) is string slot)
                {
                    if ((obj["k"] as JsonValue)?.GetValue<string>() == "callInstance")
                    {
                        // SKIP the BCL-slot rename when the call targets a rule-3 member on a @ClrTypeAlias CLASS
                        // owner (an intrinsic-less concrete override carrying a real body that AliasHelperHoist lifts
                        // into a dotkt$ClrH_* helper — String.compareTo's ordinal body must NOT resolve to the
                        // culture-sensitive System.String.CompareTo slot). Leaving it the Kotlin name lets
                        // MemberCallSubstitution's Rule 3 route it to that helper. Mirrors Rule 3's own gate exactly:
                        // a CLR-bound NON-interface owner whose member is rule-3. (An INTERFACE owner is excluded —
                        // the ref.dll mis-reports its abstract members as non-abstract, so IsRule3Member false-positives
                        // there; and a REAL non-alias class like ArrayDeque.size -> the emitted Count slot still renames.)
                        // ownerType is a STRUCTURED `{t:fqn,name:…}` node after the m1 TYPE FLIP (was a legacy string) —
                        // read it via OwnerName so `ot` is non-null; a stale `as JsonValue` read left it null, so the
                        // rule-3 guard below never fired and String.compareTo was WRONGLY renamed to the culture-sensitive
                        // System.String.CompareTo slot (il-cmpord: ordinal comparison must win).
                        var ot = TypeJson.OwnerName(obj["ownerType"]);
                        var mn = (obj["method"] as JsonValue)?.GetValue<string>();
                        var otFqn = ot != null ? ReferenceMetadataIndex.BareOwnerFqn(ot) : null;
                        var isRule3Alias = otFqn != null && mn != null
                            && refs.TryResolveClrOwner(ot, out _, out var otKind) && otKind != "interface"
                            && refs.IsRule3Member(otFqn, mn);
                        if (!isRule3Alias) obj["method"] = slot;
                    }
                    else if (obj.ContainsKey("name"))
                    {
                        obj["name"] = slot;
                        // A CLASS member that overrides a @ClrIntrinsic ancestor is a CLR override -> `override:true` AND
                        // `vis:public` (the flags kotc's `clrIfaceName != null` set via method()/accessorMethod: an
                        // interface impl must be a public virtual). Without annClr kotc emits override:false / vis:visOf(fn)
                        // for this case, so bir2cir restores them here, exactly when the rename fires. NOT in an interface
                        // (kotc's ifaceMethod keeps override:false and emits no vis). isOverride/objName keep kotc's.
                        if (!inIface)
                        {
                            if (obj.ContainsKey("override")) obj["override"] = true;
                            if (obj.ContainsKey("vis")) obj["vis"] = "public";
                            // #73 M4-c: an accessor overriding a reference-KLIB-projected .NET base CLASS virtual property
                            // needs the `clrOverride` field so ilemit's DefineMethodOverride reuses the base slot (an
                            // INTERFACE member binds by name at type-load, so it needs no clrOverride). kotc emits ONLY
                            // the plain override method + its `overrides` marker (its `clrAccessorMethod` producer was
                            // retired in #73 M4); this is the SOLE source of the clrOverride field, derived off the refs.
                            // The guard is defensive (no kotc producer remains to double-stamp).
                            if (!obj.ContainsKey("clrOverride")
                                && ResolveNetClassOwner(obj, ovs, refs) is string clrBase)
                                obj["clrOverride"] = TypeJson.Fqn(clrBase);
                        }
                    }
                }
            }
            foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, refs, inIface);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Walk(it, refs, inIface);
    }

    // #73 M4-c — the .NET base CLASS owner FQN in an accessor's override closure (a virtual property whose declaring
    // .NET type is a real CLASS, not an interface/struct), else null. Used to stamp `clrOverride` so ilemit's
    // DefineMethodOverride binds the base virtual slot. A property mapping alone is NOT sufficient: Kotlin's open
    // Throwable.cause maps to the NON-virtual Exception.InnerException getter, so a subclass `override val cause`
    // must remain a Kotlin virtual newslot rather than attempt an impossible CLR .override.
    static string ResolveNetClassOwner(JsonObject declaration, JsonArray ovs, ReferenceMetadataIndex refs)
    {
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if (TypeJson.OwnerName(oo["owner"]) is not string owner) continue;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var overrideKind = (oo["kind"] as JsonValue)?.GetValue<string>();
            var bare = ReferenceMetadataIndex.BareOwnerFqn(owner);
            if (refs.ResolveNetType(bare) is not Type nt || !nt.IsClass) continue;   // IsClass excludes interface + struct
            if (!HasOverridableAccessor(nt, member, overrideKind)) continue;
            return bare;
        }
        // @ClrProperty on a @ClrTypeAlias base (issue #24): the override's ancestor is a kotlin.* alias (kotlin.Throwable)
        // that ResolveNetType above deliberately SKIPS, yet it binds a real BCL CLASS property via @ClrProperty (message
        // -> System.Exception.get_Message). Return the ALIASED BCL owner so ilemit's DefineMethodOverride reuses the base
        // virtual slot instead of emitting a fresh newslot (else the substituted callvirt binds the base value). Class
        // only (an interface member binds by name at type-load, needing no clrOverride), mirroring the IsClass gate above.
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if (TypeJson.OwnerName(oo["owner"]) is not string owner) continue;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var overrideKind = (oo["kind"] as JsonValue)?.GetValue<string>();
            if (refs.TryResolveClrOwner(owner, out var bcl, out var ownerKind) && ownerKind == "class"
                && TryExactPropertySlot(declaration, refs, owner, member, overrideKind,
                    out _, out var bclProperty, out _)
                && refs.ResolveNetType(bcl) is Type nt && HasOverridableAccessor(nt, bclProperty, overrideKind))
                return bcl;
        }
        return null;
    }

    static bool HasOverridableAccessor(Type owner, string propertyName, string overrideKind)
    {
        try
        {
            var p = owner.GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            var accessor = overrideKind == "setter" ? p?.GetSetMethod(true) : p?.GetGetMethod(true);
            return accessor?.IsVirtual == true && !accessor.IsFinal;
        }
        catch { return false; }
    }

    // The first override entry whose (owner, Kotlin member name, arity) carries an @ClrIntrinsic in the ref.dll, mapped
    // to its CLR slot: a getter/setter -> get_/set_ + the intrinsic; a method -> the intrinsic verbatim. null = no
    // CLR-bound member in the closure (leave the kotc name).
    internal static string ResolveSlot(JsonObject declaration, JsonArray ovs, ReferenceMetadataIndex refs)
    {
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if (TypeJson.OwnerName(oo["owner"]) is not string owner) continue;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var kind = (oo["kind"] as JsonValue)?.GetValue<string>();
            var arity = (oo["arity"] as JsonValue)?.GetValue<int>() ?? 0;
            // The @ClrIntrinsic lives on the EMITTED member as the ref.dll exposes it: for a property it is on the
            // get_<name>/set_<name> ACCESSOR METHOD (not the property), and its value is the BCL PROPERTY name ("Count"),
            // so the slot is get_/set_ + that. A plain method's intrinsic is the BCL method name verbatim. EXACT arity
            // overload-matching (getter=arity 0, setter=arity 1) so `add(element)`->Add never grabs `add(i,e)`->Insert.
            // A property's @ClrIntrinsic lives on the get_<name> accessor (arity 0) in the ref.dll — for a SETTER too
            // (a `var` overriding a `val` base has no set_<name> to key on), so look up the getter and re-prefix. A plain
            // method's intrinsic is on the method itself by exact arity.
            if (kind is "getter" or "setter")
            {
                if (TryExactPropertySlot(declaration, refs, owner, member, kind,
                        out _, out _, out var accessorMethod)) return accessorMethod;
                continue;
            }
            if (refs.TryMemberIntrinsicExact(owner, member, arity, out var intr)) return intr;
        }
        // REFERENCE-KLIB-PROJECTED .NET interface/base (A2 step 5): the override owner resolves to a REAL .NET Type off the
        // refs (NOT a stdlib ref.dll alias — ResolveNetType excludes kotlin.*/dotkt$ synthetics and locals
        // type).
        // A Kotlin class implementing/overriding such a member binds the .NET slot HERE (kotc no longer bakes it). Because
        // dll2klib injects the Kotlin member identity EQUAL to the .NET name, the slot is the identity: a method ->
        // `member`; a property accessor -> get_/set_ + the .NET property name (confirmed to be a real .NET property/
        // field). This reproduces exactly what kotc's get_/set_+name / method-name fallback already emits (so it is a
        // no-op rename for a name-matching override), but routes the resolution through bir2cir + restores the
        // override:true/vis:public flags the Walk caller stamps for a CLR-bound member declaration.
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if (TypeJson.OwnerName(oo["owner"]) is not string owner) continue;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var kind = (oo["kind"] as JsonValue)?.GetValue<string>();
            if (refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(owner)) is not Type nt) continue;
            if (kind is "getter" or "setter") continue;
            if (NetInteropBinding.DeclaresPublicMethodNamed(nt, member)) return member;
        }
        return null;
    }

    // The overriding declaration already carries the frontend-resolved accessor signature. Pair it with the exact
    // override edge's source owner/property/role; never select a referenced sibling by physical name or arity alone.
    // A setter overriding a getter-only `val` is handled inside the reference index by removing the value parameter
    // and resolving the same exact getter association.
    static bool TryExactPropertySlot(JsonObject declaration, ReferenceMetadataIndex refs,
        string owner, string member, string overrideKind,
        out string physicalOwner, out string physicalProperty, out string physicalMethod)
    {
        physicalOwner = null;
        physicalProperty = null;
        physicalMethod = null;
        if (overrideKind is not ("getter" or "setter") || declaration["params"] is not JsonArray parameters)
            return false;
        var signature = new List<TypeNode>(parameters.Count);
        foreach (var parameter in parameters)
        {
            var type = TypeJson.Read((parameter as JsonObject)?["type"]);
            if (type == null) return false;
            signature.Add(type);
        }
        var methodArity = (declaration["typeParams"] as JsonArray)?.Count ?? 0;
        return refs.TryExternalPropertyAccessor(owner, member,
            overrideKind == "setter" ? "set" : "get", parameters.Count, methodArity, signature,
            Array.Empty<TypeNode>(), out physicalOwner, out physicalProperty, out physicalMethod);
    }
}
