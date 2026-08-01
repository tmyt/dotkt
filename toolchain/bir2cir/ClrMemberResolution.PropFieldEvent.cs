using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// W1-S3 (#46 / #121) — the RESOLVED-CLR-IR carry extended to the remaining un-carried member axes: PROPERTY accessors
// (clrPropGet/clrPropSet), FIELDS (an external private-backing-field property read as a `field`/`setFieldExpr`), and
// EVENTS (clrEventAdd/clrEventRemove). Until now ilemit RE-DERIVED the member KIND at codegen (a clrPropGet reclassified
// into real-property vs `get_X` method vs public FIELD; a `field` on an external type reinterpreted into a `get_`
// accessor with a field fallback; an event's add/remove resolved via an unchecked `GetEvent`), and RE-DERIVED the
// call/callvirt/constrained dispatch from the reflected accessor. This pass makes bir2cir the sole resolver: it reads
// the owner off the ref.dll MLC (ResolveOwnerType — null for a LOCAL emitted owner, whose backing field is directly
// accessible, so that node is LEFT untouched), decides the member KIND, and stamps a `member` discriminator plus, for an
// ACCESSOR, the resolved accessor NAME + `memberSig` + `dispatch`. ilemit then LINKS the exact accessor (LinkClrMethod,
// shared with S2) and consumes the carried dispatch — zero member-kind derivation, zero first-pick.
static partial class ClrMemberResolution
{
    // ---- property get / set --------------------------------------------------------------------

    // A clrPropGet/clrPropSet on a .NET (or referenced-DotKt) owner. Resolve the owner's OPEN def off the ref.dll and
    // classify: a real .NET property OR a DotKt custom-accessor `get_X`/`set_X` METHOD (no PropertyDef, emitted by our
    // own backend) -> an ACCESSOR (carry accessor name + memberSig + dispatch); a public FIELD surfaced as a Kotlin
    // property -> `member:"field"` (ilemit does the ldsfld/ldfld + const-literal inline, a mechanical value fetch, not a
    // KIND decision). 0 members = a hard ABI error.
    static void ResolveProp(JsonObject node, bool write)
    {
        if (ReadOwnerNode(node["type"]) is not TypeNode.Fqn ownerFqn)
            throw new InvalidOperationException($"bir2cir: clrProp{(write ? "Set" : "Get")} owner is not a .NET FQN slot ({TypeNode.ToJson(ReadOwnerNode(node["type"]))}) — #46 W1-S3");
        var open = ResolveOwnerType(ownerFqn);
        if (open == null)
            throw new InvalidOperationException($"bir2cir: clrProp{(write ? "Set" : "Get")} owner '{ownerFqn.Name}' does not resolve to a .NET type (#46 W1-S3 memberRef carry)");
        var name = (node["name"] as JsonValue)?.GetValue<string>();
        var isStatic = (node["static"] as JsonValue)?.GetValue<bool>() ?? false;
        var superCall = (node["super"] as JsonValue)?.GetValue<bool>() ?? false;
        // FlattenHierarchy surfaces a base-class STATIC accessor/field accessed through the derived owner (a static
        // property has no inherited-instance auto-flatten). Harmless for the instance flavor (instance already flattens).
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy | (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var acc = FindPropAccessor(open, name, write, flags);
        if (acc != null)
        {
            RetargetToBaseInterface(node, "type", open, acc, ownerFqn);
            node["member"] = "accessor";
            node["accessor"] = acc.Name;
            node["memberSig"] = MemberSig(acc.GetParameters());
            StampMemberRet(node, acc.ReturnType);
            if (!isStatic) node["dispatch"] = Dispatch(acc, open, superCall);
            return;
        }
        // A genuine public CLR FIELD is a foreign declaration too — `public List<int?> Items` is the same crossing a
        // parameter of that type is — so its declared type is stamped like any other. It reads through `ldfld`
        // rather than an accessor, which is why it has no `memberSig` and why nothing else here states its type.
        if (FindFieldMember(open, name, flags) is FieldInfo fld)
        {
            node["member"] = "field";
            StampMemberRet(node, fld.FieldType);
            return;
        }
        throw new InvalidOperationException($"bir2cir: no readable/writable property, accessor method, or field '{name}' on .NET type '{open}' (clrProp{(write ? "Set" : "Get")} — #46 W1-S3)");
    }

    // The property accessor MethodInfo for `name`: (1) a real .NET PropertyDef's authoritative get_/set_ accessor
    // (GetProperty walks base CLASSES for a class owner); else (2) a conventionally-named `get_X`/`set_X` METHOD (a DotKt
    // custom-accessor property, emitted by our backend WITHOUT a PropertyDef). Reflection does not expose an explicitly
    // implemented property on its class under the interface name, nor does interface GetProperty traverse base
    // interfaces, so both probes fall back to the implemented/base-interface walk (mirrors S2 Candidates). null when
    // the name is not an accessor (a public field, or absent).
    static MethodInfo FindPropAccessor(Type open, string name, bool write, BindingFlags flags)
    {
        try
        {
            var pi = open.GetProperty(name, flags);
            var m = write ? pi?.GetSetMethod() : pi?.GetGetMethod();
            if (m != null) return m;
        }
        catch { }
        return FindAccessorMethod(open, (write ? "set_" : "get_") + name, write ? 1 : 0, flags);
    }

    // Find the UNIQUE method named `accName` with `argc` params on the owner (own members incl. inherited class members),
    // else on its implemented/base interfaces (class GetMethods hides private explicit MethodImpl bodies under their
    // qualified CLR names; interface GetMethods excludes base-interface slots). Most-derived-declaring-type wins
    // (shared MostDerived), matching S2's Candidates. A >1 survivor set is AMBIGUOUS -> hard error (never a first-pick,
    // the pass charter). null when absent.
    static MethodInfo FindAccessorMethod(Type open, string accName, int argc, BindingFlags flags)
    {
        MethodInfo[] Named(Type t) { try { return t.GetMethods(flags).Where(m => m.Name == accName && m.GetParameters().Length == argc).ToArray(); } catch { return Array.Empty<MethodInfo>(); } }
        var own = MostDerived(Named(open).ToList());
        if (own.Count > 0) return UniqueAccessor(own, open, accName);
        if ((flags & BindingFlags.Instance) == 0) return null;
        var baseHits = MostDerived(SafeInterfaces(open).SelectMany(Named).GroupBy(m => (m.Module, m.MetadataToken)).Select(g => g.First()).ToList());
        return baseHits.Count > 0 ? UniqueAccessor(baseHits, open, accName) : null;
    }

    static MethodInfo UniqueAccessor(List<MethodInfo> hits, Type open, string accName)
    {
        if (hits.Count == 1) return hits[0];
        throw new InvalidOperationException($"bir2cir: accessor '{accName}' on '{open}' is AMBIGUOUS — {hits.Count} members match (malformed): {string.Join("; ", hits.Select(m => m.ToString()))} (#46 W1-S3)");
    }

    // A public property surfaced as a FIELD (a .NET public/static/const field, or a Kotlin backing-field property).
    // GetField walks base classes. null when absent.
    static FieldInfo FindFieldMember(Type open, string name, BindingFlags flags)
    {
        try { return open.GetField(name, flags); } catch { return null; }
    }

    // When the resolved accessor lives on a GENERIC base INTERFACE of the owner (`IReadOnlyCollection<T>.get_Count`
    // accessed on `IReadOnlyList<T>`), retarget the node's owner slot to that constructed base interface, exactly as the
    // old ilemit PropAccessor re-anchored it via SubstituteIfaceArgs. The receiver is assignable to the base interface
    // (it is a base, no cast needed), and ilemit then links `get_Count` DIRECTLY on the substituted `IReadOnlyCollection
    // <X>` owner — sidestepping LinkClrMethod's base-interface fallback, which returns the OPEN base method (an unbound
    // `!0` token -> BadImageFormat in a generic method body). Only fires for an INTERFACE owner whose accessor is declared
    // on a DIFFERENT (base) interface; a base-CLASS accessor already re-anchors correctly through reflection.
    static void RetargetToBaseInterface(JsonObject node, string ownerSlot, Type open, MethodInfo acc, TypeNode.Fqn ownerFqn)
    {
        if (!open.IsInterface) return;
        var decl = acc.DeclaringType;
        if (decl == null || SafeDef(decl) == SafeDef(open)) return;
        node[ownerSlot] = TypeJson.Write(SubstOwnerParams(decl, ownerFqn.Args ?? Array.Empty<TypeNode>()));
    }

    // An MLC Type (a base interface instance `IReadOnlyCollection<T_open>`, its args naming the OWNER def's generic
    // params) -> a lowered TypeNode with each owner-def generic PARAM replaced by the owner's actual TypeNode arg at that
    // position (`T_open@i` -> ownerArgs[i]). @ClrTypeAlias'd base interfaces resolve to their BCL twin (AliasResolve).
    static TypeNode SubstOwnerParams(Type t, TypeNode[] ownerArgs)
    {
        t = AliasResolve(t);
        if (t.IsGenericParameter)
            return t.GenericParameterPosition < ownerArgs.Length ? ownerArgs[t.GenericParameterPosition]
                 : new TypeNode.Tv("type", t.GenericParameterPosition);
        if (t.IsArray) return new TypeNode.Array(SubstOwnerParams(t.GetElementType(), ownerArgs));
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(a => SubstOwnerParams(a, ownerArgs)).ToArray();
            if (def.FullName == "System.Nullable`1") return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(StripArity(Dotted(def.FullName ?? def.Name)), args);
        }
        return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));
    }

    // ---- events --------------------------------------------------------------------------------

    // A clrEventAdd/clrEventRemove on a .NET owner. Resolve the owner off the ref.dll, find the EventInfo, and stamp its
    // add/remove accessor NAME + `memberSig` (the [handlerDelegate] param) + `dispatch`. Replaces ilemit's unchecked
    // `GetEvent(...).GetAddMethod()` (a NullReferenceException on a missing/value-type/constructed-generic event — #113):
    // a missing event is now a hard ABI error here, and the handler delegate type flows from the resolved accessor param.
    static void ResolveEvent(JsonObject node)
    {
        if (ReadOwnerNode(node["type"]) is not TypeNode.Fqn ownerFqn)
            throw new InvalidOperationException($"bir2cir: clrEvent owner is not a .NET FQN slot ({TypeNode.ToJson(ReadOwnerNode(node["type"]))}) — #46 W1-S3");
        var open = ResolveOwnerType(ownerFqn);
        if (open == null)
            throw new InvalidOperationException($"bir2cir: clrEvent owner '{ownerFqn.Name}' does not resolve to a .NET type (#46 W1-S3)");
        var name = (node["event"] as JsonValue)?.GetValue<string>();
        var isStatic = (node["static"] as JsonValue)?.GetValue<bool>() ?? false;
        var add = (node["k"] as JsonValue)?.GetValue<string>() == "clrEventAdd";
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var ev = FindEventMember(open, name, flags);
        if (ev == null)
            throw new InvalidOperationException($"bir2cir: no event '{name}' on .NET type '{open}' (clrEvent{(add ? "Add" : "Remove")} — #46 W1-S3)");
        var acc = add ? ev.GetAddMethod() : ev.GetRemoveMethod();
        if (acc == null)
            throw new InvalidOperationException($"bir2cir: event '{name}' on '{open}' has no {(add ? "add" : "remove")} accessor (#46 W1-S3)");
        RetargetToBaseInterface(node, "type", open, acc, ownerFqn);
        node["accessor"] = acc.Name;
        node["memberSig"] = MemberSig(acc.GetParameters());
        StampMemberRet(node, acc.ReturnType);
        if (!isStatic) node["dispatch"] = Dispatch(acc, open, superCall: false);
    }

    // The EventInfo for `name`: own (incl. inherited class events), else base interfaces (interface GetEvent excludes
    // base-interface events). null when absent.
    static EventInfo FindEventMember(Type open, string name, BindingFlags flags)
    {
        try { var ev = open.GetEvent(name, flags); if (ev != null) return ev; } catch { }
        if (!open.IsInterface) return null;
        foreach (var bi in SafeInterfaces(open))
            try { var ev = bi.GetEvent(name, flags); if (ev != null) return ev; } catch { }
        return null;
    }

    // ---- external field access (`field` / `setFieldExpr`) --------------------------------------

    // A Kotlin field read/write (`this.x`, a destructuring `component1()` that kotc lowers to a backing-field access) on
    // an EXTERNAL owner. A cross-assembly backing field is PRIVATE (a direct ldfld -> FieldAccessException), so the read
    // must go through the public `get_X`/`set_X` accessor when one exists. That KIND choice (accessor vs direct field)
    // was ilemit's ExternalPropAccessor; move it here: resolve the owner off the ref.dll (null = a LOCAL owner whose
    // field IS directly accessible -> leave the plain `field` node), and when the external owner exposes the accessor,
    // stamp `member:"accessor"` + accessor name + memberSig + dispatch so ilemit LINKS it. A genuine public field
    // (@ClrField) has no accessor -> left as a plain `field` for ilemit's direct ldfld/stfld.
    static void ResolveFieldAccess(JsonObject node, bool write)
    {
        if (ReadOwnerNode(node["ownerType"]) is not TypeNode.Fqn ownerFqn) return;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null) return;   // LOCAL emitted owner (ref.dll returns null) -> direct backing-field access in ilemit
        var name = (node["name"] as JsonValue)?.GetValue<string>();
        if (name == null) return;
        var acc = FindAccessorMethod(open, (write ? "set_" : "get_") + name, write ? 1 : 0, BindingFlags.Public | BindingFlags.Instance);
        if (acc == null)
        {
            // No accessor. A genuine public @ClrField (the field really is declared there) -> direct ldfld/stfld in
            // ilemit, unchanged. But if the owner declares NEITHER an accessor NOR a field of that name, the node names
            // storage that does not exist in the referenced assembly — an accessor-routed property's storage is emitted
            // under its compiler-generated name (BackingFieldRename), reachable only through get_/set_. Reaching here
            // means a carrier (a cross-module [KotlinInline] payload) named the Kotlin identity for storage that is not
            // cross-assembly-addressable. Fail with a breadcrumb rather than let ilemit's ResolveField return null and
            // NRE at Emit(Ldfld, null).
            // Deliberately the WIDEST probe (any visibility, flattened, field OR accessor): the throw must fire only
            // when the member is absent outright, never merely because the narrow public probe above missed it.
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var direct = FindFieldMember(open, name, Any);
            if (direct == null && FindAccessorMethod(open, (write ? "set_" : "get_") + name, write ? 1 : 0, Any) == null)
                throw new InvalidOperationException(
                    $"bir2cir: '{ownerFqn.Name}.{name}' is neither a field nor a get_/set_ accessor on the referenced owner — "
                    + "a cross-assembly property's storage is reachable only through its accessors");
            // A direct external FIELD read/write: its declared type is the foreign declaration this node stands for.
            if (direct != null) StampMemberRet(node, direct.FieldType);
            return;
        }
        RetargetToBaseInterface(node, "ownerType", open, acc, ownerFqn);
        node["member"] = "accessor";
        node["accessor"] = acc.Name;
        node["memberSig"] = MemberSig(acc.GetParameters());
        StampMemberRet(node, acc.ReturnType);
        node["dispatch"] = Dispatch(acc, open, superCall: false);
    }
}
