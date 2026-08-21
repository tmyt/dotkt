using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CLR OVERRIDE ALLOCATION. kotc tags each emitted declaration/call with a pure-Kotlin `overrides` marker (the
// transitive override closure). This pass resolves the external slot from reference metadata. Ordinary functions may
// adopt that physical name; Kotlin property accessors retain their dedicated physical name and record the external
// base slot separately for MethodImpl wiring. The `overrides` marker is stripped later by BirTypeLowering.
static class DeclarationRename
{
    // bir2cir-internal hand-off from semantic declaration identity to physical allocation. Ordinary methods may
    // adopt an external CLR name in this pass, but later representation passes still have to consume frontend facts
    // expressed in Kotlin identity. Preserve that identity explicitly until BirTypeLowering strips the carrier;
    // never recover it from the physical spelling or an override hierarchy.
    internal const string SourceMemberKey = "kotlinSourceMember";

    // Recursively consume every `overrides` marker. Calls and ordinary functions may adopt the resolved CLR slot;
    // accessor declarations keep their dedicated name while receiving override flags and an explicit base slot.
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
                var semanticPropertyDeclaration = obj.ContainsKey("name")
                    && KotlinPropertyAccessors.TryIdentity(obj, out _, out _);
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
                        // ownerType is a structured `{t:fqn,name:…}` node. Read it via OwnerName so `ot` is non-null;
                        // reading it as JsonValue left it null, so the
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
                        // A Kotlin accessor always stays in the dedicated property naming domain. External CLR
                        // property slots retain their native spelling and are linked explicitly below/through the
                        // interface MethodImpl descriptors authored by KotlinOverrideSlotBridge.
                        if (!semanticPropertyDeclaration)
                        {
                            if (obj["name"] is JsonValue sourceName
                                && sourceName.TryGetValue<string>(out var sourceMember)
                                && sourceMember != slot)
                            {
                                obj[SourceMemberKey] ??= sourceMember;
                                RoundtripMetadata.AddSourceMethodIdentity(obj, sourceMember);
                            }
                            obj["name"] = slot;
                        }
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
                            // needs the `pendingOverrideOwner` field so ilemit's DefineMethodOverride reuses the base slot (an
                            // INTERFACE member binds by name at type-load, so it needs no pendingOverrideOwner). kotc emits ONLY
                            // the plain override method + its `overrides` marker (its `clrAccessorMethod` producer was
                            // retired in #73 M4); this is the SOLE source of the pendingOverrideOwner field, derived off the refs.
                            // The guard is defensive (no kotc producer remains to double-stamp).
                            if (ResolveNetClassOwner(obj, ovs, refs, out var clrBaseReturn) is TypeNode.Fqn clrBase)
                            {
                                obj["pendingOverrideOwner"] ??= TypeJson.Write(clrBase);
                                obj["pendingOverrideReturn"] ??= TypeJson.Write(clrBaseReturn);
                                if (semanticPropertyDeclaration)
                                {
                                    if (obj["pendingOverrideMember"] is JsonValue existing
                                        && existing.TryGetValue<string>(out var existingMember)
                                        && existingMember != slot)
                                        throw new InvalidOperationException(
                                            $"conflicting CLR base property slots '{existingMember}' and '{slot}'");
                                    obj["pendingOverrideMember"] = slot;
                                }
                            }
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
    // .NET type is a real CLASS, not an interface/struct), else null. Used to stamp `pendingOverrideOwner` so ilemit's
    // DefineMethodOverride binds the base virtual slot. A property mapping alone is NOT sufficient: Kotlin's open
    // Throwable.cause maps to the NON-virtual Exception.InnerException getter, so a subclass `override val cause`
    // must remain a Kotlin virtual newslot rather than attempt an impossible CLR .override.
    static TypeNode.Fqn ResolveNetClassOwner(JsonObject declaration, JsonArray ovs, ReferenceMetadataIndex refs,
        out TypeNode slotReturn)
    {
        slotReturn = null;
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if (TypeJson.Read(oo["owner"]) is not TypeNode.Fqn ownerSpec) continue;
            var owner = ownerSpec.Name;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var overrideKind = (oo["kind"] as JsonValue)?.GetValue<string>();
            var reflected = ReferenceMetadataIndex.ReflectedOwnerFqn(owner);
            if (refs.ResolveNetType(reflected, ownerSpec.Args?.Length ?? 0) is not Type nt || !nt.IsClass) continue;   // IsClass excludes interface + struct
            if (!TryExactPropertySlot(declaration, refs, ownerSpec, member, overrideKind,
                    out _, out var physicalProperty, out _, out var exactReturn)
                || !HasOverridableAccessor(nt, physicalProperty, overrideKind)) continue;
            slotReturn = exactReturn;
            return new TypeNode.Fqn(reflected, ownerSpec.Args);
        }
        // @ClrProperty on a @ClrTypeAlias base (issue #24): the override's ancestor is a kotlin.* alias (kotlin.Throwable)
        // that ResolveNetType above deliberately SKIPS, yet it binds a real BCL CLASS property via @ClrProperty (message
        // -> System.Exception.get_Message). Return the ALIASED BCL owner so ilemit's DefineMethodOverride reuses the base
        // virtual slot instead of emitting a fresh newslot (else the substituted callvirt binds the base value). Class
        // only (an interface member binds by name at type-load, needing no pendingOverrideOwner), mirroring the IsClass gate above.
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if (TypeJson.Read(oo["owner"]) is not TypeNode.Fqn ownerSpec) continue;
            var owner = ownerSpec.Name;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var overrideKind = (oo["kind"] as JsonValue)?.GetValue<string>();
            if (refs.TryResolveClrOwner(owner, out var bcl, out var ownerKind) && ownerKind == "class"
                && TryExactPropertySlot(declaration, refs, ownerSpec, member, overrideKind,
                    out _, out var bclProperty, out _, out var exactReturn)
                && refs.ResolveNetType(bcl) is Type nt && HasOverridableAccessor(nt, bclProperty, overrideKind))
            {
                slotReturn = exactReturn;
                return new TypeNode.Fqn(bcl, ownerSpec.Args);
            }
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

    // The first override entry whose (owner, Kotlin member name, arity) resolves to a CLR slot. Property accessors use
    // the referenced Property/MethodSemantics association; ordinary methods use their exact intrinsic/native name.
    // null = no CLR-bound member in the closure (leave the kotc name).
    internal static string ResolveSlot(JsonObject declaration, JsonArray ovs, ReferenceMetadataIndex refs)
    {
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if (TypeJson.Read(oo["owner"]) is not TypeNode.Fqn ownerSpec) continue;
            var owner = ownerSpec.Name;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var kind = (oo["kind"] as JsonValue)?.GetValue<string>();
            var arity = (oo["arity"] as JsonValue)?.GetValue<int>() ?? 0;
            // A property annotation names the target CLR Property, not an accessor spelling. Resolve the exact
            // MethodSemantics association from reference metadata. A setter overriding a getter-only `val` still uses
            // the getter to establish the property allocation, then resolves the external setter if one exists. Plain
            // methods use their exact intrinsic name. Arity remains part of ordinary-method overload selection.
            if (kind is "getter" or "setter")
            {
                if (TryExactPropertySlot(declaration, refs, ownerSpec, member, kind,
                        out _, out _, out var accessorMethod, out _)) return accessorMethod;
                continue;
            }
            if (refs.TryMemberIntrinsicExact(owner, member, arity, out var intr)) return intr;
        }
        // REFERENCE-KLIB-PROJECTED .NET interface/base (A2 step 5): the override owner resolves to a REAL .NET Type off the
        // refs (NOT a stdlib ref.dll alias — ResolveNetType excludes kotlin.*/dotkt$ synthetics and locals
        // type).
        // A Kotlin class implementing/overriding such a member binds the .NET slot HERE (kotc no longer bakes it). Because
        // dll2klib injects an ordinary method identity equal to the .NET name, that method's slot is the identity.
        // Properties were already handled above through their explicit Property/MethodSemantics association. This
        // also restores the override:true/vis:public flags the Walk caller stamps for a CLR-bound declaration.
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if (TypeJson.Read(oo["owner"]) is not TypeNode.Fqn ownerSpec) continue;
            var owner = ownerSpec.Name;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var kind = (oo["kind"] as JsonValue)?.GetValue<string>();
            if (refs.ResolveNetType(ReferenceMetadataIndex.ReflectedOwnerFqn(owner), ownerSpec.Args?.Length ?? 0) is not Type nt) continue;
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
        TypeNode.Fqn owner, string member, string overrideKind,
        out string physicalOwner, out string physicalProperty, out string physicalMethod,
        out TypeNode slotReturn)
    {
        physicalOwner = null;
        physicalProperty = null;
        physicalMethod = null;
        slotReturn = null;
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
        if (!refs.TryExternalPropertyAccessor(owner.Name, member,
            overrideKind == "setter" ? "set" : "get", parameters.Count, methodArity, signature,
            owner.Args ?? Array.Empty<TypeNode>(), out physicalOwner, out physicalProperty, out physicalMethod)
            || !refs.TryNullableGenericPropertySlot(owner.Name, member,
                overrideKind == "setter" ? "set" : "get", isStatic: false, parameters.Count, methodArity,
                signature, owner.Args ?? Array.Empty<TypeNode>(), out var declaredReturn, out _, out var refused,
                includeUnchanged: true)
            || declaredReturn == null || refused?.Any(value => value) == true)
            return false;
        slotReturn = SupertypeGraph.SubstOwnerTvs(declaredReturn, owner.Args ?? Array.Empty<TypeNode>());
        return true;
    }
}
