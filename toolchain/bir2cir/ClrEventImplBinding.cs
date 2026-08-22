using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// .NET EVENT IMPLEMENT/RAISE binding (§4.2/§4.3 of design-clr-event-model.md) — the Kotlin↔CLR relation for a Kotlin
// class that IMPLEMENTS a CLR interface event (`class ViewModelBase : INotifyPropertyChanged { override val
// PropertyChanged by clrEvent() }`) or DECLARES a new one (`val clicked: ClrEvent<D> by clrEvent()`). Sibling of the
// CONSUME pass ClrEventSubscriptionBinding.
//
// kotc emitted PURE-KOTLIN identities: a per-event `clrEventBacking{name, handlerType}` directive (the handler as a
// Kotlin FUNCTION type) in the type's `clrEvents` array, plus three synthesized accessor methods add_/remove_/raise_<E>
// whose bodies are tagged `clrEventAccessor{kind,event}` and whose add_/remove_ carry an `overrides` closure naming the
// interface event slot by Kotlin identity (owner FQN + event name). This pass — the ONLY layer that reads the ref.dll —
// resolves the concrete delegate `D` (the interface event's EventHandlerType, or an Action/Func synthesized from the
// handler fn for a new event) and:
//   * inserts a real backing field `<E>$delegate : D`;
//   * rewrites each accessor's param signature to `D` (add/remove) / D.Invoke's params (raise) and its tagged body to a
//     `clrEventAccessorImpl{kind, name:<field>, delegateType:D}` CIR directive (ilemit emits the CAS loop / raise);
//   * replaces `clrEventBacking` with a type-level `clrEventDecl{name, delegateType:D}` (ilemit emits the `.event` row).
// The interface MethodImpl wiring is FREE: ilemit's referenced-interface binding pass matches the public-virtual
// add_/remove_<E> to the interface's add_/remove_ event-accessor slots by name+signature (DefineMethodOverride).
// A RAISE (`clrEventRaise`) is bound separately below to a `callInstance raise_<E>` on the receiver's declaring type,
// with the §6 guard: raise is legal ONLY for a Kotlin-DECLARED event (one that has a synthesized `raise_<E>`).
static class ClrEventImplBinding
{
    sealed record LocalEventBinding(
        TypeNode DelegateType,
        TypeNode.Fqn AccessorOwner,
        JsonArray AddSignature,
        JsonArray RemoveSignature);

    public static JsonNode BindImplementations(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is JsonObject obj && obj["types"] is JsonArray types)
            foreach (var t in types.OfType<JsonObject>())
                BindType(t, refs);
        return root;
    }

    // Raise sites and their declaring event may live in different source files. Resolve them only after every local
    // event implementation has its final synthesized raise_<E> declaration, then copy that declaration's exact
    // parameter vector onto the call. ilemit links this descriptor; it never selects a local member by name/arity.
    public static void BindRaisesAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        var signatures = new Dictionary<(string Owner, string Method), JsonArray>();
        foreach (var root in rootList.OfType<JsonObject>())
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                {
                    var owner = Str(type["name"]);
                    if (owner == null || type["methods"] is not JsonArray methods) continue;
                    foreach (var method in methods.OfType<JsonObject>())
                    {
                        var name = Str(method["name"]);
                        if (name == null || !name.StartsWith("raise_", StringComparison.Ordinal)
                            || method["params"] is not JsonArray parameters) continue;
                        signatures[(owner, name)] = new JsonArray(parameters.OfType<JsonObject>()
                            .Select(p => p["type"]?.DeepClone()).ToArray());
                    }
                }
        foreach (var root in rootList) BindRaises(root, refs, signatures);
    }

    // A subscription can live in a different source file from the Kotlin event declaration.  Once every
    // `clrEventBacking` has become a concrete `clrEventDecl`, bind add/remove sites whose owner is emitted in this
    // compilation to that declaration.  Referenced events deliberately remain untouched for
    // ClrMemberResolution's EventInfo path.
    public static void BindLocalSubscriptionsAll(IEnumerable<JsonNode> roots)
    {
        var rootList = roots.ToList();
        var definitions = SupertypeGraph.Collect(rootList);
        var scopes = new Dictionary<JsonObject, (JsonArray TypeParams, JsonArray MethodParams)>();
        var empty = new JsonArray();

        void IndexCallable(JsonObject callable, JsonArray typeParams) =>
            scopes[callable] = (typeParams, callable["typeParams"] as JsonArray ?? empty);

        void IndexType(JsonObject type)
        {
            var typeParams = TypeParameterFrame.CloneDeclarations(type);
            scopes[type] = (typeParams, empty);
            if (type["ctors"] is JsonArray ctors)
                foreach (var ctor in ctors.OfType<JsonObject>()) IndexCallable(ctor, typeParams);
            if (type["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>()) IndexCallable(method, typeParams);
            if (type["types"] is JsonArray nested)
                foreach (var child in nested.OfType<JsonObject>()) IndexType(child);
        }

        foreach (var root in rootList.OfType<JsonObject>())
        {
            scopes[root] = (empty, empty);
            if (root["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>()) IndexCallable(method, empty);
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) IndexType(type);
        }

        JsonArray AccessorSignature(SupertypeGraph.Def definition, string name)
        {
            var candidates = definition.Methods.OfType<JsonObject>()
                .Where(method => Str(method["name"]) == name
                    && method["specialName"] is JsonValue special
                    && special.TryGetValue<bool>(out var isSpecial) && isSpecial
                    && method["params"] is JsonArray)
                .ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    $"bir2cir: synthesized local event accessor '{definition.Name}.{name}' resolves to "
                    + $"{candidates.Count} declarations");
            return new JsonArray(((JsonArray)candidates[0]["params"]).OfType<JsonObject>()
                .Select(parameter => parameter["type"]?.DeepClone()).ToArray());
        }

        LocalEventBinding ResolveFromOwner(TypeNode.Fqn owner, string eventName)
        {
            if (!definitions.ContainsKey(owner.Name)) return null;
            var pending = new Queue<TypeNode.Fqn>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(owner);
            while (pending.Count > 0)
            {
                var levelCount = pending.Count;
                var matches = new List<LocalEventBinding>();
                for (var i = 0; i < levelCount; i++)
                {
                    var spec = pending.Dequeue();
                    if (!seen.Add(SupertypeGraph.TypeKey(spec))
                        || !definitions.TryGetValue(spec.Name, out var definition)) continue;
                    var ownerArgs = SupertypeGraph.EffectiveArgs(spec, definition.Arity);
                    if (ownerArgs == null)
                        throw new InvalidOperationException(
                            $"bir2cir: local clrEvent owner '{spec.Name}' has {definition.Arity} type parameter(s), "
                            + "but the subscription owner does not carry that constructed argument vector");
                    var declarations = definition.Node["clrEvents"] as JsonArray;
                    foreach (var declaration in declarations?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    {
                        if (Str(declaration["k"]) != "clrEventDecl"
                            || Str(declaration["name"]) != eventName) continue;
                        var declaredDelegate = TypeJson.Read(declaration["delegateType"])
                            ?? throw new InvalidOperationException(
                                $"bir2cir: local clrEvent '{spec.Name}.{eventName}' has no concrete delegate type");
                        matches.Add(new LocalEventBinding(
                            SupertypeGraph.SubstOwnerTvs(declaredDelegate, ownerArgs),
                            spec,
                            AccessorSignature(definition, "add_" + eventName),
                            AccessorSignature(definition, "remove_" + eventName)));
                    }
                    foreach (var parent in definition.Interfaces)
                        if (SupertypeGraph.SubstOwnerTvs(parent, ownerArgs) is TypeNode.Fqn constructed)
                            pending.Enqueue(constructed);
                    if (definition.Base != null
                        && SupertypeGraph.SubstOwnerTvs(definition.Base, ownerArgs) is TypeNode.Fqn constructedBase)
                        pending.Enqueue(constructedBase);
                }
                if (matches.Count == 0) continue;
                var distinct = matches.GroupBy(match => SupertypeGraph.TypeKey(match.AccessorOwner), StringComparer.Ordinal)
                    .Select(group => group.First()).ToList();
                if (distinct.Count != 1)
                    throw new InvalidOperationException(
                        $"bir2cir: local clrEvent '{owner.Name}.{eventName}' resolves to {distinct.Count} "
                        + "different accessor declarations at the same inheritance depth");
                return distinct[0];
            }
            return null;
        }

        LocalEventBinding Resolve(JsonObject node, JsonArray typeParams, JsonArray methodParams)
        {
            var eventName = Str(node["event"]);
            if (eventName == null) return null;
            var owner = TypeJson.Read(node["type"]);
            if (owner is TypeNode.Fqn fqn) return ResolveFromOwner(fqn, eventName);
            if (owner is not TypeNode.Tv tv) return null;
            var frame = tv.Scope == "type" ? typeParams : tv.Scope == "method" ? methodParams : null;
            if (frame == null || tv.I < 0 || tv.I >= frame.Count || frame[tv.I] is not JsonObject descriptor
                || descriptor["constraints"] is not JsonArray constraints) return null;
            var matches = constraints.Select(TypeJson.Read).OfType<TypeNode.Fqn>()
                .Select(constraint => ResolveFromOwner(constraint, eventName))
                .Where(binding => binding != null)
                .GroupBy(binding => SupertypeGraph.TypeKey(binding.AccessorOwner), StringComparer.Ordinal)
                .Select(group => group.First()).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"bir2cir: local clrEvent '{eventName}' on '{tv.Scope}' type parameter {tv.I} resolves to "
                    + $"{matches.Count} different accessor declarations");
            return matches.SingleOrDefault();
        }

        TypeNode RemapForClosure(TypeNode type, JsonArray free) => type switch
        {
            TypeNode.Tv tv => free.Select(TypeJson.Read).OfType<TypeNode.Tv>()
                .Select((original, index) => (original, index))
                .Where(item => item.original.Scope == tv.Scope && item.original.I == tv.I)
                .Select(item => (TypeNode)new TypeNode.Tv("type", item.index))
                .FirstOrDefault() ?? type,
            TypeNode.Fqn f when f.Args is not null =>
                new TypeNode.Fqn(f.Name, f.Args.Select(arg => RemapForClosure(arg, free)).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(RemapForClosure(n.Of, free)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(RemapForClosure(o.Of, free)),
            TypeNode.Array a => new TypeNode.Array(RemapForClosure(a.Elem, free)),
            TypeNode.ByRef r => new TypeNode.ByRef(RemapForClosure(r.Of, free)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, RemapForClosure(fn.Ret, free),
                fn.Params.Select(param => RemapForClosure(param, free)).ToArray(),
                fn.Recv == null ? null : RemapForClosure(fn.Recv, free), fn.Clr,
                fn.Ctx?.Select(param => RemapForClosure(param, free)).ToArray()),
            _ => type,
        };

        LocalEventBinding RemapBindingForClosure(LocalEventBinding binding, JsonArray free) => new(
            RemapForClosure(binding.DelegateType, free),
            (TypeNode.Fqn)RemapForClosure(binding.AccessorOwner, free),
            new JsonArray(binding.AddSignature.Select(item =>
                TypeJson.Write(RemapForClosure(TypeJson.Read(item), free))).ToArray()),
            new JsonArray(binding.RemoveSignature.Select(item =>
                TypeJson.Write(RemapForClosure(TypeJson.Read(item), free))).ToArray()));

        var bySubscription = new Dictionary<string, LocalEventBinding>(StringComparer.Ordinal);
        void Collect(JsonNode node, JsonArray typeParams, JsonArray methodParams)
        {
            if (node is JsonObject obj)
            {
                if (scopes.TryGetValue(obj, out var scope))
                    (typeParams, methodParams) = scope;
                var kind = Str(obj["k"]);
                if (kind is "clrEventAdd" or "clrEventRemove"
                    && Str(obj["eventSubscriptionKey"]) is string key
                    && obj["eventBindingFree"] == null
                    && Resolve(obj, typeParams, methodParams) is LocalEventBinding binding)
                    bySubscription[key] = binding;
                foreach (var child in obj.ToList())
                    if (child.Value != null) Collect(child.Value, typeParams, methodParams);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToList())
                    if (child != null) Collect(child, typeParams, methodParams);
        }

        void Stamp(JsonNode node, JsonArray typeParams, JsonArray methodParams)
        {
            if (node is JsonObject obj)
            {
                if (scopes.TryGetValue(obj, out var scope))
                    (typeParams, methodParams) = scope;
                var kind = Str(obj["k"]);
                if (kind is "clrEventAdd" or "clrEventRemove")
                {
                    LocalEventBinding binding;
                    // The remove callback is deliberately a new physical generic frame. Its binding is the add
                    // site's already-selected declaration remapped into that frame, never a second semantic lookup.
                    if (obj["eventBindingFree"] is JsonArray free
                        && Str(obj["eventSubscriptionKey"]) is string key
                        && bySubscription.TryGetValue(key, out var shared))
                        binding = RemapBindingForClosure(shared, free);
                    else
                        binding = Resolve(obj, typeParams, methodParams);
                    if (binding != null)
                    {
                        var eventName = Str(obj["event"])
                            ?? throw new InvalidOperationException($"bir2cir: local {kind} is missing its event name");
                        obj["accessor"] = (kind == "clrEventAdd" ? "add_" : "remove_") + eventName;
                        obj["accessorOwner"] = TypeJson.Write(binding.AccessorOwner);
                        obj["delegateType"] = TypeJson.Write(binding.DelegateType);
                        obj["sig"] = (kind == "clrEventAdd"
                            ? binding.AddSignature : binding.RemoveSignature).DeepClone();
                        obj["localAccessor"] = true;
                        // A type-parameter receiver requires an address plus constrained. dispatch.  A concrete local
                        // owner uses the synthesized public-virtual accessor normally.
                        obj["dispatch"] = TypeJson.Read(obj["type"]) is TypeNode.Tv ? "constrained" : "callvirt";
                    }
                    obj.Remove("eventSubscriptionKey");
                    obj.Remove("eventBindingFree");
                }
                foreach (var child in obj.ToList())
                    if (child.Value != null) Stamp(child.Value, typeParams, methodParams);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToList())
                    if (child != null) Stamp(child, typeParams, methodParams);
        }

        foreach (var root in rootList) Collect(root, empty, empty);
        foreach (var root in rootList) Stamp(root, empty, empty);
    }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // Resolve every field-like `clrEvents` directive and class-delegation `clrEventForwarders` directive on one type.
    static void BindType(JsonObject type, ReferenceMetadataIndex refs)
    {
        var backings = type["clrEvents"] as JsonArray;
        var forwarders = type["clrEventForwarders"] as JsonArray;
        if ((backings == null || backings.Count == 0) && (forwarders == null || forwarders.Count == 0)) return;
        var methods = type["methods"] as JsonArray ?? new JsonArray();
        var fields = type["fields"] as JsonArray;
        if (fields == null) { fields = new JsonArray(); type["fields"] = fields; }
        var decls = new JsonArray();
        foreach (var backing in backings?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var name = Str(backing["name"]);
            if (name == null) continue;
            var addM = FindAccessor(methods, "add_" + name, "add");
            var remM = FindAccessor(methods, "remove_" + name, "remove");
            var raiseM = FindAccessor(methods, "raise_" + name, "raise");
            if (addM == null || remM == null || raiseM == null)
                throw new InvalidOperationException($"bir2cir: clrEvent '{name}' is missing a synthesized add/remove/raise accessor — kotc synthesis defect");

            // The concrete delegate `D` + its Invoke param nodes (raise's signature).
            var (delegateNode, invokeParamNodes) = ResolveDelegate(name, addM, backing["handlerType"], refs);

            var fieldName = name + "$delegate";
            fields.Add(new JsonObject { ["name"] = fieldName, ["type"] = delegateNode.DeepClone(), ["vis"] = "private" });

            RewriteFieldLike(addM, "add", fieldName, delegateNode);
            RewriteFieldLike(remM, "remove", fieldName, delegateNode);
            RewriteRaise(raiseM, fieldName, delegateNode, invokeParamNodes);

            decls.Add(new JsonObject { ["k"] = "clrEventDecl", ["name"] = name, ["delegateType"] = delegateNode.DeepClone() });
        }
        foreach (var forwarder in forwarders?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var name = Str(forwarder["name"]);
            if (name == null) continue;
            var addM = FindAccessor(methods, "add_" + name, "add");
            var remM = FindAccessor(methods, "remove_" + name, "remove");
            if (addM == null || remM == null)
                throw new InvalidOperationException($"bir2cir: delegated clrEvent '{name}' is missing a synthesized add/remove forwarder — kotc synthesis defect");
            var (delegateNode, _) = ResolveDelegate(name, addM, null, refs, forwarder["ownerType"]);
            RewriteForwarder(addM, "add", forwarder, delegateNode);
            RewriteForwarder(remM, "remove", forwarder, delegateNode);
            decls.Add(new JsonObject { ["k"] = "clrEventDecl", ["name"] = name, ["delegateType"] = delegateNode.DeepClone() });
        }
        type["clrEvents"] = decls;
        type.Remove("clrEventForwarders");
    }

    // Select the kotc-synthesized shell by its semantic marker, never a user overload that merely shares `add_E`.
    static JsonObject FindAccessor(JsonArray methods, string name, string kind) =>
        methods.OfType<JsonObject>().FirstOrDefault(m =>
            Str(m["name"]) == name
            && m["body"] is JsonArray { Count: 1 } body
            && body[0] is JsonObject marker
            && Str(marker["k"]) == "clrEventAccessor"
            && Str(marker["kind"]) == kind);

    // add_/remove_: param[0].type := D; body := the CAS-loop directive; drop the (consumed) overrides closure.
    static void RewriteFieldLike(JsonObject m, string kind, string fieldName, JsonNode delegateNode)
    {
        if (m["params"] is JsonArray ps && ps.Count > 0 && ps[0] is JsonObject p0)
            p0["type"] = delegateNode.DeepClone();
        m["body"] = new JsonArray { AccessorImpl(kind, fieldName, delegateNode) };
        m["specialName"] = true;   // ECMA-335 event-accessor convention (ilemit stamps MethodAttributes.SpecialName)
        m.Remove("overrides");
    }

    // raise_: params := D.Invoke's params (so `field?.Invoke(args)` forwards them); body := the raise directive.
    static void RewriteRaise(JsonObject m, string fieldName, JsonNode delegateNode, List<JsonNode> invokeParamNodes)
    {
        var ps = new JsonArray();
        for (int i = 0; i < invokeParamNodes.Count; i++)
            ps.Add(new JsonObject { ["name"] = "arg" + i, ["type"] = invokeParamNodes[i].DeepClone() });
        m["params"] = ps;
        m["body"] = new JsonArray { AccessorImpl("raise", fieldName, delegateNode) };
        m["specialName"] = true;   // ECMA-335 event-accessor convention (the .event's Fire)
        m.Remove("overrides");
    }

    static JsonObject AccessorImpl(string kind, string fieldName, JsonNode delegateNode) => new()
    {
        ["k"] = "clrEventAccessorImpl",
        ["kind"] = kind,
        ["name"] = fieldName,
        ["delegateType"] = delegateNode.DeepClone(),
    };

    // Class delegation forwards to the exact delegate expression captured by kotc. The referenced interface owns the
    // CLR relation: resolve D above, then lower the pure BIR directive to the ordinary consume nodes.
    static void RewriteForwarder(JsonObject method, string kind, JsonObject forwarder, JsonNode delegateNode)
    {
        if (method["params"] is JsonArray ps && ps.Count > 0 && ps[0] is JsonObject p0)
            p0["type"] = delegateNode.DeepClone();
        var eventAccess = new JsonObject
        {
            ["k"] = kind == "add" ? "clrEventAdd" : "clrEventRemove",
            ["type"] = forwarder["ownerType"]?.DeepClone()
                ?? throw new InvalidOperationException("bir2cir: delegated clrEvent forwarder is missing its delegate owner type"),
            ["event"] = forwarder["name"]?.DeepClone(),
            ["static"] = false,
            ["recv"] = forwarder["recv"]?.DeepClone()
                ?? throw new InvalidOperationException("bir2cir: delegated clrEvent forwarder is missing its delegate receiver"),
            ["handler"] = new JsonObject
            {
                ["sty"] = delegateNode.DeepClone(), ["k"] = "local", ["name"] = "value",
            },
            // The synthesized accessor parameter was rewritten to the event's exact delegate D above. Passing it
            // through unchanged preserves delegate identity across add/remove; wrapping D.Invoke again would make
            // EventSubscription.close unable to remove the instance installed by add.
            ["handlerExact"] = true,
        };
        method["body"] = new JsonArray
        {
            new JsonObject { ["k"] = "exprStmt", ["expr"] = eventAccess },
        };
        method["specialName"] = true;
        method.Remove("overrides");
    }

    // The concrete delegate type node for an event + the type nodes of its Invoke's params (raise's signature). For an
    // OVERRIDE (the add_ accessor's `overrides` closure names the interface event slot) the delegate is the interface
    // event's `EventHandlerType`, read off the ref.dll — bir2cir OWNS this resolution (§9). For a NEW event (no override)
    // the handler Kotlin FUNCTION type maps to a System.Action/Func delegate of the same shape.
    static (JsonNode delegateNode, List<JsonNode> invokeParams) ResolveDelegate(
        string name, JsonObject addM, JsonNode handlerType, ReferenceMetadataIndex refs,
        JsonNode constructedOwner = null)
    {
        if (addM["overrides"] is JsonArray ovs)
            foreach (var o in ovs.OfType<JsonObject>())
            {
                var owner = TypeJson.OwnerName(o["owner"]);
                var member = Str(o["member"]);
                if (owner == null || member == null) continue;
                var ownerSpec = TypeJson.Read(constructedOwner) as TypeNode.Fqn;
                Type iface;
                TypeNode[] ownerArgs;
                if (ownerSpec != null
                    && TryResolveConstructedInterface(ownerSpec, owner, refs, out var constructedIface, out var constructedArgs))
                {
                    iface = constructedIface;
                    ownerArgs = constructedArgs;
                }
                else
                {
                    var overrideSpec = TypeJson.Read(o["owner"]) as TypeNode.Fqn;
                    var ownerArity = overrideSpec?.Args?.Length ?? 0;
                    iface = refs.ResolveNetType(ReferenceMetadataIndex.ReflectedOwnerFqn(owner), ownerArity);
                    ownerArgs = overrideSpec?.Args;
                }
                // Kotlin's override closure can name the immediate delegated interface even when the event is inherited
                // from one of its CLR base interfaces. Locate the declaration while composing the generic arguments at
                // each interface edge; Type.GetEvent does not itself include inherited interface events.
                if (iface == null
                    || !TryResolveDeclaredEvent(iface, ownerArgs ?? Array.Empty<TypeNode>(), member,
                        out var ev, out var declaringArgs)) continue;
                ownerArgs = declaringArgs;
                var d = ev.EventHandlerType;
                if (d == null) continue;
                var invoke = d.GetMethod("Invoke");
                var invokeParams = invoke?.GetParameters()
                    .Select(p => TypeJson.Write(SubstituteOwnerTypeVariables(NetTypeToNode(p.ParameterType), ownerArgs))).ToList()
                                   ?? new List<JsonNode>();
                return (TypeJson.Write(SubstituteOwnerTypeVariables(NetTypeToNode(d), ownerArgs)), invokeParams);
            }

        // A NEW event: the overrides closure was empty (no interface slot). Map the handler fn -> Action/Func.
        if (handlerType is JsonObject fn && Str(fn["t"]) == "fn")
            return FnToDelegate(fn);

        throw new InvalidOperationException(
            $"bir2cir: cannot resolve the delegate type for event '{name}' — it neither overrides a resolvable .NET interface "
            + "event nor carries an inferable handler function type");
    }

    // Follow a constructed delegated interface (for example IChild<T>) to the interface that actually declares the
    // override slot (IBase<T>), composing the generic arguments at every edge. Reflection exposes inherited interface
    // types in the current definition's generic-parameter frame; substituting each edge before descending keeps the
    // result in the Kotlin wrapper's type-parameter frame (IChild<T> -> IBase<T>, not IBase<!0-of-IBase>).
    static bool TryResolveConstructedInterface(
        TypeNode.Fqn rootSpec, string targetOwner, ReferenceMetadataIndex refs,
        out Type targetInterface, out TypeNode[] targetArgs)
    {
        targetInterface = null;
        targetArgs = null;
        var root = refs.ResolveNetType(
            ReferenceMetadataIndex.ReflectedOwnerFqn(rootSpec.Name), rootSpec.Args?.Length ?? 0);
        if (root == null) return false;
        return Walk(root, rootSpec.Args ?? Array.Empty<TypeNode>(), 0, out targetInterface, out targetArgs);

        bool Walk(Type current, TypeNode[] currentArgs, int depth, out Type found, out TypeNode[] foundArgs)
        {
            found = null;
            foundArgs = null;
            if (depth > 64) return false; // defensive only; valid CLR interface inheritance is acyclic.
            var definition = current.IsGenericType && !current.IsGenericTypeDefinition
                ? current.GetGenericTypeDefinition()
                : current;
            var currentName = StripArity(Dotted(definition.FullName ?? definition.Name));
            if (ReferenceMetadataIndex.BareOwnerFqn(currentName)
                == ReferenceMetadataIndex.BareOwnerFqn(targetOwner))
            {
                found = definition;
                foundArgs = currentArgs;
                return true;
            }
            foreach (var inherited in definition.GetInterfaces())
            {
                var inheritedDefinition = inherited.IsGenericType
                    ? inherited.GetGenericTypeDefinition()
                    : inherited;
                var inheritedArgs = inherited.IsGenericType
                    ? inherited.GetGenericArguments()
                        .Select(a => SubstituteOwnerTypeVariables(NetTypeToNode(a), currentArgs)).ToArray()
                    : Array.Empty<TypeNode>();
                if (Walk(inheritedDefinition, inheritedArgs, depth + 1, out found, out foundArgs)) return true;
            }
            return false;
        }
    }

    static bool TryResolveDeclaredEvent(
        Type current, TypeNode[] currentArgs, string member,
        out EventInfo foundEvent, out TypeNode[] foundArgs)
    {
        foundEvent = null;
        foundArgs = null;
        return Walk(current, currentArgs, 0, out foundEvent, out foundArgs);

        bool Walk(Type candidate, TypeNode[] candidateArgs, int depth, out EventInfo found, out TypeNode[] args)
        {
            found = null;
            args = null;
            if (depth > 64) return false;
            var definition = candidate.IsGenericType && !candidate.IsGenericTypeDefinition
                ? candidate.GetGenericTypeDefinition()
                : candidate;
            var declared = definition.GetEvent(
                member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (declared != null)
            {
                found = declared;
                args = candidateArgs;
                return true;
            }
            foreach (var inherited in definition.GetInterfaces())
            {
                var inheritedDefinition = inherited.IsGenericType
                    ? inherited.GetGenericTypeDefinition()
                    : inherited;
                var inheritedArgs = inherited.IsGenericType
                    ? inherited.GetGenericArguments()
                        .Select(a => SubstituteOwnerTypeVariables(NetTypeToNode(a), candidateArgs)).ToArray()
                    : Array.Empty<TypeNode>();
                if (Walk(inheritedDefinition, inheritedArgs, depth + 1, out found, out args)) return true;
            }
            return false;
        }
    }

    static TypeNode SubstituteOwnerTypeVariables(TypeNode type, TypeNode[] ownerArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when ownerArgs != null && tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Fqn { Args: { Length: > 0 } args } f =>
            new TypeNode.Fqn(f.Name, args.Select(a => SubstituteOwnerTypeVariables(a, ownerArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteOwnerTypeVariables(n.Of, ownerArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteOwnerTypeVariables(o.Of, ownerArgs)),
        TypeNode.Array a => new TypeNode.Array(SubstituteOwnerTypeVariables(a.Elem, ownerArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteOwnerTypeVariables(b.Of, ownerArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(
            fn.Suspend,
            SubstituteOwnerTypeVariables(fn.Ret, ownerArgs),
            fn.Params.Select(p => SubstituteOwnerTypeVariables(p, ownerArgs)).ToArray(),
            fn.Recv == null ? null : SubstituteOwnerTypeVariables(fn.Recv, ownerArgs),
            fn.Clr,
            fn.Ctx?.Select(c => SubstituteOwnerTypeVariables(c, ownerArgs)).ToArray()),
        _ => type,
    };

    // A handler Kotlin function type `(A,B)->R` -> `System.Action<A,B>` (R = Unit/void) or `System.Func<A,B,R>`, plus the
    // Invoke param nodes (= the handler's params). The natural CLR lowering of a Kotlin fn type for a brand-new event.
    static (JsonNode, List<JsonNode>) FnToDelegate(JsonObject fn)
    {
        var pars = (fn["params"] as JsonArray)?.OfType<JsonNode>().Select(p => p.DeepClone()).ToList() ?? new List<JsonNode>();
        var ret = fn["ret"];
        bool voidRet = ret == null || (ret is JsonObject ro && Str(ro["name"]) is "kotlin.Unit" or "void");
        var args = new JsonArray();
        foreach (var p in pars) args.Add(p.DeepClone());
        JsonNode delegateNode;
        if (voidRet)
            delegateNode = args.Count == 0 ? TypeJson.Fqn("System.Action")
                : new JsonObject { ["t"] = "fqn", ["name"] = "System.Action", ["args"] = args };
        else
        {
            args.Add(ret.DeepClone());
            delegateNode = new JsonObject { ["t"] = "fqn", ["name"] = "System.Func", ["args"] = args };
        }
        return (delegateNode, pars);
    }

    // An MLC Type -> its concrete lowered TypeNode (BCL FullName spelling, generic args recursed, Nullable<T> -> T?). Keeps
    // the delegate's CONCRETE Fqn (a named event delegate like PropertyChangedEventHandler must link by name, not as a `fn`).
    static TypeNode NetTypeToNode(Type t)
    {
        if (t.IsGenericParameter) return new TypeNode.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        if (t.IsByRef) return new TypeNode.ByRef(NetTypeToNode(t.GetElementType()));
        if (t.IsArray) return new TypeNode.Array(NetTypeToNode(t.GetElementType()));
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(NetTypeToNode).ToArray();
            if (def.FullName == "System.Nullable`1") return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(StripArity(Dotted(def.FullName ?? def.Name)), args);
        }
        return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));
    }

    static string Dotted(string s) => s.Replace('+', '.');
    static string StripArity(string s) { var i = s.IndexOf('`'); return i >= 0 ? s[..i] : s; }

    // ---- RAISE call sites -----------------------------------------------------------------------

    // Bind every `clrEventRaise{type,event,recv,args}` (a Kotlin-declared event handle `.invoke(...)`) to a
    // `callInstance raise_<E>` on the receiver's static type — the type that declares the synthesized `raise_<E>`. §6
    // GUARD: raise is legal ONLY for a Kotlin-declared event; a `clrEventRaise` whose owner is a CONSUMED foreign .NET
    // event (no synthesized raise_) is caught in ilemit as a missing method — but here we keep the shape honest.
    static void BindRaises(JsonNode node, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<(string Owner, string Method), JsonArray> signatures)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
                if (kv.Value != null) BindRaises(kv.Value, refs, signatures);
            if (Str(obj["k"]) == "clrEventRaise")
            {
                var owner = obj["type"]?.DeepClone();
                // §6 GUARD: raise is legal ONLY for a Kotlin-DECLARED event. If the receiver's owner resolves to a real
                // .NET type off the ref.dll, this is a CONSUMED foreign event (no synthesized raise_) — a hard error (you
                // raise on the declaring instance, and only Kotlin-declared events have a raise_ accessor).
                if (TypeJson.OwnerName(owner) is string ownerName
                    && refs.ResolveNetType(ReferenceMetadataIndex.ReflectedOwnerFqn(ownerName)) != null)
                    throw new InvalidOperationException(
                        $"bir2cir: cannot raise the .NET event '{Str(obj["event"])}' on '{ownerName}' — you can only raise an "
                        + "event you DECLARE in Kotlin (`override val E by clrEvent()`), not a consumed foreign .NET event");
                var raiseName = "raise_" + Str(obj["event"]);
                var localOwner = TypeJson.OwnerName(owner)
                    ?? throw new InvalidOperationException($"bir2cir: event raise '{raiseName}' has no owner type");
                if (!signatures.TryGetValue((localOwner, raiseName), out var signature))
                    throw new InvalidOperationException(
                        $"bir2cir: event raise '{localOwner}.{raiseName}' has no synthesized local declaration");
                var recv = obj["recv"]?.DeepClone();
                var args = obj["args"] as JsonArray ?? new JsonArray();
                // Replace the node's contents in place with a plain instance call to raise_<E>.
                foreach (var k in obj.Select(kv => kv.Key).ToList()) obj.Remove(k);
                obj["k"] = "callInstance";
                obj["ownerType"] = owner;
                obj["method"] = raiseName;
                obj["sig"] = signature.DeepClone();
                obj["virtual"] = true;
                obj["recv"] = recv;
                obj["args"] = args;
            }
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) BindRaises(it, refs, signatures);
    }
}
