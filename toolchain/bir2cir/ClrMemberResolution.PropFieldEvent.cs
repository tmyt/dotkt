using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// W1-S3 (#46 / #121) — the RESOLVED-CLR-IR carry extended to the remaining un-carried member axes: PROPERTY accessors
// (clrPropGet/clrPropSet), FIELDS (an external private-backing-field property read as a `field`/`setFieldExpr`), and
// EVENTS (clrEventAdd/clrEventRemove). Until now ilemit RE-DERIVED the member KIND at codegen (a clrPropGet reclassified
// into real-property vs public FIELD; a `field` on an external type reinterpreted into a property accessor
// accessor with a field fallback; an event's add/remove resolved via an unchecked `GetEvent`), and RE-DERIVED the
// call/callvirt/constrained dispatch from the reflected accessor. This pass makes bir2cir the sole resolver: it reads
// the owner off the ref.dll MLC (ResolveOwnerType — null for a LOCAL emitted owner, whose backing field is directly
// accessible, so that node is LEFT untouched), decides the member KIND, and authors the exact scalar memberRef plus
// dispatch. ilemit consumes those facts one-to-one — zero member-kind derivation, zero first-pick.
static partial class ClrMemberResolution
{
    // ---- property get / set --------------------------------------------------------------------

    // A clrPropGet/clrPropSet on a .NET (or referenced-DotKt) owner. Resolve the owner's OPEN def off the ref.dll and
    // classify: a real .NET property -> an ACCESSOR (carry its scalar memberRef + dispatch); a public FIELD surfaced as a Kotlin
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
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var acc = FindPropAccessor(open, name, write, flags);
        if (acc != null)
        {
            RetargetToBaseInterface(node, "type", open, acc, ownerFqn);
            node["member"] = "accessor";
            node["accessor"] = acc.Name;
            node["memberRef"] = MemberRefJson(acc, MemberRefNode.Kinds.PropertyAccessor, open, ownerFqn.Args);
            StampResolvedMemberReturn(node, acc.ReturnType);
            if (!isStatic) node["dispatch"] = Dispatch(acc, open, superCall);
            // A WRITE's value fills the setter's parameter, which is an ordinary delegate slot when the property is
            // delegate-typed. The declaring owner may have been projected onto a base interface just above, so read
            // the slot in that final frame.
            if (write) MarkWrittenDelegateSlot(node, SubstOwnerParams(acc.GetParameters()[^1].ParameterType,
                (ReadOwnerNode(node["type"]) as TypeNode.Fqn ?? ownerFqn).Args ?? Array.Empty<TypeNode>()));
            return;
        }
        // A genuine public CLR FIELD is a foreign declaration too — `public List<int?> Items` is the same crossing a
        // parameter of that type is — so its declared type is stamped like any other. It reads through `ldfld`
        // rather than an accessor, which is why it has no `resolvedMemberParams` and why nothing else here states its type.
        if (FindFieldMember(open, name, flags) is FieldInfo fld)
        {
            node["member"] = "field";
            node["memberRef"] = FieldRefJson(fld, open, ownerFqn.Args);
            StampResolvedMemberReturn(node, fld.FieldType);
            if (write) MarkWrittenDelegateSlot(node,
                SubstOwnerParams(fld.FieldType, ownerFqn.Args ?? Array.Empty<TypeNode>()));
            return;
        }
        throw new InvalidOperationException($"bir2cir: no readable/writable property, accessor method, or field '{name}' on .NET type '{open}' (clrProp{(write ? "Set" : "Get")} — #46 W1-S3)");
    }

    // A WRITE's value node fills the storage it is written into, exactly as an argument fills a parameter. A
    // delegate-typed setter parameter or field is therefore an ordinary delegate slot, and a literal lambda written
    // into it must construct THAT delegate. The nodes spell their value under two keys.
    static void MarkWrittenDelegateSlot(JsonObject node, TypeNode slotType)
    {
        if (node["value"] is JsonObject value) MarkDelegateSlot(value, slotType);
        else if (node["e"] is JsonObject expression) MarkDelegateSlot(expression, slotType);
    }

    // The property accessor MethodInfo for `name`: a real .NET PropertyDef's authoritative MethodSemantics accessor
    // (GetProperty walks base CLASSES for a class owner). Reflection does not expose an explicitly
    // implemented property on its class under the interface name, nor does interface GetProperty traverse base
    // interfaces, so both probes fall back to the implemented/base-interface walk (mirrors S2 Candidates). null when
    // the name is not an accessor (a public field, or absent).
    static MethodInfo FindPropAccessor(Type open, string name, bool write, BindingFlags flags)
    {
        MethodInfo Accessor(Type type, bool declaredOnly)
        {
            try
            {
                var property = type.GetProperty(name,
                    declaredOnly ? flags | BindingFlags.DeclaredOnly : flags);
                var method = write
                    ? property?.GetSetMethod(nonPublic: true)
                    : property?.GetGetMethod(nonPublic: true);
                return method != null && IsPublicOrProtected(method) ? method : null;
            }
            catch { return null; }
        }

        // PropertyInfo/MethodSemantics is authoritative. Reflection does not inherit PropertyInfo across interface
        // edges, so walk those edges explicitly while retaining the exact associated accessor MethodInfo.
        var own = Accessor(open, declaredOnly: false);
        if (own != null) return own;
        if ((flags & BindingFlags.Instance) == 0) return null;
        try
        {
            var hits = MostDerived(SafeInterfaces(open).Select(type => Accessor(type, declaredOnly: true))
                .Where(method => method != null)
                .GroupBy(method => (method.Module, method.MetadataToken)).Select(group => group.First()).ToList());
            return hits.Count == 0 ? null : UniqueAccessor(hits, open, name);
        }
        catch { return null; }
    }

    // Find the UNIQUE method named `accName` with `argc` params on the owner (own members incl. inherited class members),
    // else on its implemented/base interfaces (class GetMethods hides private explicit MethodImpl bodies under their
    // qualified CLR names; interface GetMethods excludes base-interface slots). Most-derived-declaring-type wins
    // (shared MostDerived), matching S2's Candidates. A >1 survivor set is AMBIGUOUS -> hard error (never a first-pick,
    // the pass charter). null when absent.
    static MethodInfo FindAccessorMethod(Type open, string accName, int argc, BindingFlags flags)
    {
        MethodInfo[] Named(Type t)
        {
            try
            {
                return t.GetMethods(flags).Where(m =>
            m.Name == accName && m.GetParameters().Length == argc && IsPublicOrProtected(m)).ToArray();
            }
            catch { return Array.Empty<MethodInfo>(); }
        }
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
        try
        {
            var field = open.GetField(name, flags);
            return field != null && (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly) ? field : null;
        }
        catch { return null; }
    }

    static bool IsPublicOrProtected(MethodBase method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

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
        // The metadata universe resolves an open generic owner through a harmless object-closed probe in a few
        // reflection paths.  An accessor obtained from that probe may therefore report
        // `IReadOnlyCollection<object>` even though the owner's actual base edge is
        // `IReadOnlyList<T> : IReadOnlyCollection<T>`.  Recover the declaration edge from the OPEN owner's own
        // interface graph before substituting the call site's arguments; the accessor identifies WHICH interface,
        // while the graph identifies HOW its type parameters relate to the owner.  Using acc.DeclaringType directly
        // would bake the probe's object into every generic property call.
        var openDefinition = SafeDef(open) ?? open;
        var declarationEdge = SafeInterfaces(openDefinition)
            .Where(iface => SafeDef(iface) == SafeDef(decl))
            .SingleOrDefault() ?? decl;
        node[ownerSlot] = TypeJson.Write(SubstOwnerParams(
            declarationEdge, ownerFqn.Args ?? Array.Empty<TypeNode>()));
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

    // A clrEventAdd/clrEventRemove on a .NET owner. Resolve the owner off the ref.dll, find the EventInfo, and stamp the
    // add/remove accessor's complete memberRef plus `dispatch`. Replaces ilemit's unchecked
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
        var acc = FindEventAccessor(open, name, add, flags);
        if (acc == null)
            throw new InvalidOperationException($"bir2cir: no event '{name}' on .NET type '{open}' (clrEvent{(add ? "Add" : "Remove")} — #46 W1-S3)");
        RetargetToBaseInterface(node, "type", open, acc, ownerFqn);
        // A stored Kotlin function value is re-wrapped in the event's own delegate type.  The accessor declaration
        // fixes that target type; carry its constructor here so ilemit does not rediscover a member from the delegate
        // shape.  RetargetToBaseInterface may have projected the owner onto a declaring base interface, so substitute
        // the handler slot in that final owner frame.
        var declaringSpec = ReadOwnerNode(node["type"]) as TypeNode.Fqn ?? ownerFqn;
        var handlerType = SubstOwnerParams(acc.GetParameters().Single().ParameterType,
            declaringSpec.Args ?? Array.Empty<TypeNode>());
        ResolveDelegateCtor(node, handlerType);
        // Subscription lowering spills the handler into a local so add/remove reuse the same callable value. The
        // event node therefore carries a stored function value plus the exact target constructor above; direct
        // delegate constructions are normalized at their own declared slots, never guessed here.
        node["accessor"] = acc.Name;
        node["memberRef"] = MemberRefJson(acc, MemberRefNode.Kinds.EventAccessor, open, ownerFqn.Args);
        StampResolvedMemberReturn(node, acc.ReturnType);
        if (!isStatic) node["dispatch"] = Dispatch(acc, open, superCall: false);
    }

    // The authoritative add/remove accessor for `name`: own (including inherited class events), else the implemented
    // or base interfaces. A class's private explicit MethodImpl body is intentionally not surfaced as an EventInfo,
    // but its public interface declaration is the callable slot; resolving that declaration makes a subscription call
    // the existing body instead of inventing a second event store. Reflection also excludes base-interface EventInfo
    // rows, so class and interface owners share this fallback. Own declarations win; a surviving interface collision
    // is a hard ambiguity, never a first-pick.
    static MethodInfo FindEventAccessor(Type open, string name, bool add, BindingFlags flags)
    {
        MethodInfo Accessor(Type type, bool declaredOnly)
        {
            try
            {
                var ev = type.GetEvent(name,
                    declaredOnly ? flags | BindingFlags.DeclaredOnly : flags);
                var method = add
                    ? ev?.GetAddMethod(nonPublic: true)
                    : ev?.GetRemoveMethod(nonPublic: true);
                return method != null && IsPublicOrProtected(method) ? method : null;
            }
            catch { return null; }
        }

        var own = Accessor(open, declaredOnly: false);
        if (own != null) return own;
        if ((flags & BindingFlags.Instance) == 0) return null;
        List<MethodInfo> hits;
        try
        {
            hits = MostDerived(SafeInterfaces(open).Select(type => Accessor(type, declaredOnly: true))
                .Where(method => method != null)
                .GroupBy(method => (method.Module, method.MetadataToken)).Select(group => group.First()).ToList());
        }
        catch { return null; }
        return hits.Count == 0 ? null : UniqueAccessor(hits, open, (add ? "add_" : "remove_") + name);
    }

    // ---- external field access (`field` / `setFieldExpr`) --------------------------------------

    // A Kotlin field read/write (`this.x`, a destructuring `component1()` that kotc lowers to a backing-field access) on
    // an EXTERNAL owner. A cross-assembly backing field is PRIVATE (a direct ldfld -> FieldAccessException), so the read
    // must go through the public Property/MethodSemantics accessor when one exists. That KIND choice (accessor vs direct field)
    // was ilemit's ExternalPropAccessor; move it here: resolve the owner off the ref.dll (null = a LOCAL owner whose
    // field IS directly accessible -> leave the plain `field` node), and when the external owner exposes the accessor,
    // stamp `member:"accessor"` + its memberRef + dispatch. A genuine public field
    // (@ClrField) has no accessor -> left as a plain `field` for ilemit's direct ldfld/stfld.
    static void ResolveFieldAccess(JsonObject node, bool write)
    {
        if (ReadOwnerNode(node["ownerType"]) is not TypeNode.Fqn ownerFqn) return;
        // A generated support type may have a reference twin from an earlier ProjectReference while also being
        // re-emitted in this compilation unit. Same-emission ownership wins: its internal/private storage is legal
        // local IL and must not be rejected by inspecting the stale external twin.
        if (_localTypes.Contains(ownerFqn.Name)) return;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null) return;   // LOCAL emitted owner (ref.dll returns null) -> direct backing-field access in ilemit
        var name = (node["name"] as JsonValue)?.GetValue<string>();
        if (name == null) return;
        var acc = FindPropAccessor(open, name, write, BindingFlags.Public | BindingFlags.Instance);
        if (acc == null)
        {
            // No accessor. A genuine public @ClrField (the field really is declared there) -> direct ldfld/stfld in
            // ilemit, unchanged. But if the owner declares NEITHER an accessor NOR a field of that name, the node names
            // storage that does not exist in the referenced assembly — an accessor-routed property's storage is emitted
            // under its compiler-generated name (BackingFieldRename), reachable only through its accessors. Reaching here
            // means a carrier (a cross-module [KotlinInline] payload) named the Kotlin identity for storage that is not
            // cross-assembly-addressable. Fail with a breadcrumb rather than let ilemit's ResolveField return null and
            // NRE at Emit(Ldfld, null).
            // Deliberately the WIDEST probe (any visibility, flattened, field OR accessor): the throw must fire only
            // when the member is absent outright, never merely because the narrow public probe above missed it.
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var direct = FindFieldMember(open, name, Any);
            if (direct == null && FindPropAccessor(open, name, write, Any) == null)
                throw new InvalidOperationException(
                    $"bir2cir: '{ownerFqn.Name}.{name}' is neither a field nor a property accessor on the referenced owner — "
                    + "a cross-assembly property's storage is reachable only through its accessors");
            // A direct external FIELD read/write: its declared type is the foreign declaration this node stands for.
            if (direct != null)
            {
                node["memberRef"] = FieldRefJson(direct, open, ownerFqn.Args);
                StampResolvedMemberReturn(node, direct.FieldType);
                if (write) MarkWrittenDelegateSlot(node,
                    SubstOwnerParams(direct.FieldType, ownerFqn.Args ?? Array.Empty<TypeNode>()));
            }
            return;
        }
        RetargetToBaseInterface(node, "ownerType", open, acc, ownerFqn);
        node["member"] = "accessor";
        node["accessor"] = acc.Name;
        node["memberRef"] = MemberRefJson(acc, MemberRefNode.Kinds.PropertyAccessor, open, ownerFqn.Args);
        StampResolvedMemberReturn(node, acc.ReturnType);
        node["dispatch"] = Dispatch(acc, open, superCall: false);
        if (write) MarkWrittenDelegateSlot(node, SubstOwnerParams(acc.GetParameters()[^1].ParameterType,
            (ReadOwnerNode(node["ownerType"]) as TypeNode.Fqn ?? ownerFqn).Args ?? Array.Empty<TypeNode>()));
    }
}
