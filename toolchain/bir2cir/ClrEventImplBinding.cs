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
// Kotlin function or nominal handler type) in the type's `clrEvents` array, plus three synthesized accessor methods add_/remove_/raise_<E>
// whose bodies are tagged `clrEventAccessor{kind,event}` and whose add_/remove_ carry an `overrides` closure naming the
// interface event slot by Kotlin identity (owner FQN + event name). This pass — the ONLY layer that reads the ref.dll —
// resolves the concrete delegate `D` (the interface event's EventHandlerType, or an Action/Func synthesized from the
// handler fn for a new event) and:
//   * inserts a real backing field `<E>$delegate : D`, or retains the explicit Kotlin policy field;
//   * rewrites each accessor's param signature to `D` (add/remove) / D.Invoke's params (raise) and its tagged body to a
//     `clrEventAccessorImpl{kind, name:<field>, delegateType:D}` CIR directive (ilemit emits the CAS loop / raise);
//   * routes custom-policy accessors through ordinary calls to that policy instead of the field-like directives;
//   * replaces `clrEventBacking` with a type-level `clrEventDecl{name, delegateType:D}` (ilemit emits the `.event` row).
// The interface MethodImpl wiring is FREE: ilemit's referenced-interface binding pass matches the public-virtual
// add_/remove_<E> to the interface's add_/remove_ event-accessor slots by name+signature (DefineMethodOverride).
// A RAISE (`clrEventRaise`) is bound below to the selected static/instance accessor on its physical declaring type,
// with the §6 guard: raise is legal ONLY for a Kotlin-DECLARED event (one that has a synthesized `raise_<E>`).
static class ClrEventImplBinding
{
    const string RaiseDeclarationKey = "eventRaiseDeclarationId";
    sealed record RaiseBinding(string Name, JsonArray Signature, string DeclarationId, bool IsStatic);

    // Event accessors adopt their native delegate slot later. Keep Kotlin's complete handler type first,
    // including nominal nullability and covariant Nothing overrides, not only ordinary function shapes.
    public static void PreserveSourceFacts(IEnumerable<JsonNode> roots)
    {
        void Preserve(JsonObject owner)
        {
            if (owner["methods"] is JsonArray inheritedAccessors)
                foreach (var getter in inheritedAccessors.OfType<JsonObject>().Where(method =>
                    method["fakeOverride"]?.GetValue<bool>() == true
                    && method["eventProperty"]?.GetValue<bool>() == true
                    && Str(method[KotlinPropertyAccessors.KindKey]) == "get"))
                    getter[KotlinPropertyAccessors.MetadataCarrierKey] = new JsonObject
                    {
                        ["name"] = Str(getter[KotlinPropertyAccessors.SourceNameKey]), ["kind"] = "get",
                        ["association"] = Str(getter[KotlinPropertyAccessors.AssociationKey]), ["inherited"] = true,
                    };
            var events = (owner["clrEvents"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
            var forwarders = (owner["clrEventForwarders"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
            if (owner["methods"] is JsonArray accessors)
                foreach (var declaration in events.Concat(forwarders))
                    foreach (var getter in accessors.OfType<JsonObject>().Where(method =>
                        Str(method[KotlinPropertyAccessors.KindKey]) == "get"
                        && Str(method[KotlinPropertyAccessors.AssociationKey]) is string association
                        && association == Str(declaration[KotlinPropertyAccessors.AssociationKey])))
                        getter[KotlinPropertyAccessors.MetadataCarrierKey] = new JsonObject
                        {
                            ["name"] = Str(getter[KotlinPropertyAccessors.SourceNameKey]),
                            ["kind"] = "get",
                            ["association"] = Str(declaration[KotlinPropertyAccessors.AssociationKey]),
                            ["event"] = Str(declaration["name"]),
                        };
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                {
                    method.Remove("eventProperty");
                    if (method["body"] is JsonArray { Count: > 0 } body
                        && Str(body[0]?["k"]) == "clrEventAccessor"
                        && Str(body[0]?["kind"]) is "add" or "remove"
                        && method["params"] is JsonArray { Count: 1 } parameters)
                        parameters[0]["kotlinType"] ??= parameters[0]["type"].ToJsonString();
                }
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) Preserve(type);
        }
        foreach (var root in roots.OfType<JsonObject>()) Preserve(root);
    }

    // Snapshot declaration recipes before per-file lowering mutates their bodies. A local override uses the base
    // delegate ABI, even when its covariant ClrEvent handler is narrower or inferred as Nothing. Input file order
    // must not decide which delegate is selected. Hoisting may add newly materialized owners to the same index.
    public sealed class LocalEvents
    {
        readonly Dictionary<(string Owner, string Event), (JsonObject Backing, JsonObject Add)> declarations = new();
        readonly ValueTypeOracle isByRefLike;

        public LocalEvents(IEnumerable<JsonNode> roots, ValueTypeOracle isByRefLike)
        {
            this.isByRefLike = isByRefLike;
            foreach (var root in roots) Index(root);
        }

        public void Index(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["name"]) is string owner && obj["clrEvents"] is JsonArray events
                    && obj["methods"] is JsonArray methods)
                    foreach (var backing in events.OfType<JsonObject>())
                        if (Str(backing["k"]) == "clrEventBacking" && Str(backing["name"]) is string name
                            && FindAccessor(methods, "add_" + name, "add") is JsonObject add)
                        {
                            if (declarations.ContainsKey((owner, name))) continue;
                            var recipe = (JsonObject)backing.DeepClone();
                            // Event binding follows ordinary function-value representation. Snapshot the same ABI
                            // here, not a new Action/Func allocation from the un-erased source function signature.
                            FunctionValueRepresentation.Apply(recipe, isByRefLike);
                            declarations.Add((owner, name), (recipe, (JsonObject)add.DeepClone()));
                        }
                if (obj["types"] is JsonArray nested) foreach (var value in nested) if (value != null) Index(value);
            }
            else if (node is JsonArray array) foreach (var value in array) if (value != null) Index(value);
        }

        public bool TryGet(TypeNode.Fqn owner, string name, out (JsonObject Backing, JsonObject Add) declaration) =>
            declarations.TryGetValue((owner.Name, name), out declaration);
    }

    sealed record LocalEventBinding(
        TypeNode DelegateType,
        TypeNode.Fqn AccessorOwner,
        JsonArray AddSignature,
        JsonArray RemoveSignature,
        string AddIdentity,
        string RemoveIdentity,
        string AddVisibility,
        string RemoveVisibility);

    public static JsonNode BindImplementations(JsonNode root, ReferenceMetadataIndex refs, LocalEvents locals)
    {
        locals.Index(root);
        if (root is JsonObject obj && obj["types"] is JsonArray types)
            foreach (var t in types.OfType<JsonObject>())
                BindType(t, refs, locals);
        return root;
    }

    // Raise sites and their declaring event may live in different source files. Resolve them only after every local
    // event implementation has its final synthesized raise_<E> declaration, then copy that declaration's exact
    // parameter vector onto the call. ilemit links this descriptor; it never selects a local member by name/arity.
    public static void BindRaisesAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        var signatures = new Dictionary<(string Owner, string Event), RaiseBinding>();
        foreach (var root in rootList.OfType<JsonObject>())
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                {
                    var owner = Str(type["name"]);
                    if (owner == null || type["methods"] is not JsonArray methods) continue;
                    if (type["clrEvents"] is not JsonArray events) continue;
                    foreach (var declaration in events.OfType<JsonObject>())
                    {
                        var identity = Str(declaration[RaiseDeclarationKey]);
                        if (identity == null) continue; // Interface delegation supplies add/remove, not a raiser.
                        var method = methods.OfType<JsonObject>().Single(m => Str(m[DeclarationIdentityBinding.Key]) == identity);
                        signatures[(owner, Str(declaration["name"]))] = new RaiseBinding(
                            Str(method["name"]), FunctionSignatureIdentity.Signature((JsonArray)method["params"]), identity,
                            method["static"]?.GetValue<bool>() == true);
                        declaration.Remove(RaiseDeclarationKey);
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

        (JsonArray Signature, string Identity, string Visibility) AccessorSignature(SupertypeGraph.Def definition, string name)
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
            var selected = candidates[0];
            // These are compiler-authored physical accessors, not additional
            // Kotlin callable declarations. State their identity at selection
            // so subsequent carrier/collision projection preserves this edge.
            selected[DeclarationIdentityBinding.Key] ??= DeclarationIdentityBinding.PhysicalOnlyId(
                "clr-event-accessor:" + new JsonArray(definition.Name, name).ToJsonString(), "accessor");
            return (FunctionSignatureIdentity.Signature((JsonArray)selected["params"]),
                Str(selected[DeclarationIdentityBinding.Key]), Str(selected["vis"]) ?? "public");
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
                        var add = AccessorSignature(definition, "add_" + eventName);
                        var remove = AccessorSignature(definition, "remove_" + eventName);
                        matches.Add(new LocalEventBinding(
                            SupertypeGraph.SubstOwnerTvs(declaredDelegate, ownerArgs),
                            spec,
                            add.Signature, remove.Signature, add.Identity, remove.Identity,
                            add.Visibility, remove.Visibility));
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

        void Stamp(JsonNode node, JsonArray typeParams, JsonArray methodParams)
        {
            if (node is JsonObject obj)
            {
                if (scopes.TryGetValue(obj, out var scope))
                    (typeParams, methodParams) = scope;
                var kind = Str(obj["k"]);
                if (kind is "clrEventAdd" or "clrEventRemove")
                {
                    var binding = Resolve(obj, typeParams, methodParams);
                    if (binding != null)
                    {
                        var eventName = Str(obj["event"])
                            ?? throw new InvalidOperationException($"bir2cir: local {kind} is missing its event name");
                        obj["accessor"] = (kind == "clrEventAdd" ? "add_" : "remove_") + eventName;
                        obj["accessorOwner"] = TypeJson.Write(binding.AccessorOwner);
                        obj[DeclarationIdentityBinding.Key] = kind == "clrEventAdd"
                            ? binding.AddIdentity : binding.RemoveIdentity;
                        obj["delegateType"] = TypeJson.Write(binding.DelegateType);
                        obj["handler"] = new JsonObject
                        {
                            ["k"] = "cast", ["type"] = TypeJson.Write(binding.DelegateType),
                            ["e"] = obj["handler"]?.DeepClone(),
                        };
                        obj["sig"] = (kind == "clrEventAdd"
                            ? binding.AddSignature : binding.RemoveSignature).DeepClone();
                        obj["localAccessor"] = true;
                        // A type-parameter receiver requires an address plus constrained. dispatch.  A concrete local
                        // owner uses the synthesized public-virtual accessor normally.
                        var isStatic = obj["static"]?.GetValue<bool>() == true;
                        obj["dispatch"] = isStatic ? "call"
                            : TypeJson.Read(obj["type"]) is TypeNode.Tv ? "constrained" : "callvirt";
                        var visibility = kind == "clrEventAdd" ? binding.AddVisibility : binding.RemoveVisibility;
                        if (visibility is "private" or "protected")
                        {
                            // Retained handles put this call on a generated callback TypeDef. Use the ordinary exact
                            // member-call path so lexical-access lowering can bridge it without widening the event.
                            var constrained = TypeJson.Read(obj["type"]) is TypeNode.Tv;
                            var call = new JsonObject
                            {
                                ["k"] = isStatic ? "callStatic" : constrained ? "constrainedCall" : "callInstance",
                                [isStatic ? "owner" : constrained ? "iface" : "ownerType"] = obj["accessorOwner"]!.DeepClone(),
                                ["method"] = obj["accessor"]!.DeepClone(),
                                [DeclarationIdentityBinding.Key] = obj[DeclarationIdentityBinding.Key]!.DeepClone(),
                                ["sig"] = obj["sig"]!.DeepClone(), ["recv"] = obj["recv"]?.DeepClone(),
                                ["args"] = new JsonArray(obj["handler"]?.DeepClone()),
                                ["virtual"] = !isStatic, ["ret"] = TypeJson.Fqn("kotlin.Unit"),
                                ["sty"] = TypeJson.Fqn("kotlin.Unit"),
                            };
                            if (constrained) call["recvType"] = obj["type"]!.DeepClone();
                            if (isStatic) call.Remove("recv");
                            obj.Clear();
                            foreach (var pair in call) obj[pair.Key] = pair.Value?.DeepClone();
                        }
                    }
                }
                foreach (var child in obj.ToList())
                    if (child.Value != null) Stamp(child.Value, typeParams, methodParams);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToList())
                    if (child != null) Stamp(child, typeParams, methodParams);
        }

        foreach (var root in rootList) Stamp(root, empty, empty);
    }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // Resolve field-like/custom-policy `clrEvents` and class-delegation `clrEventForwarders` on one type.
    static void BindType(JsonObject type, ReferenceMetadataIndex refs, LocalEvents locals)
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
            var isStatic = backing["static"]?.GetValue<bool>() == true;
            var addM = FindAccessor(methods, "add_" + name, "add");
            var remM = FindAccessor(methods, "remove_" + name, "remove");
            var raiseM = FindAccessor(methods, "raise_" + name, "raise");
            if (addM == null || remM == null || raiseM == null)
                throw new InvalidOperationException($"bir2cir: clrEvent '{name}' is missing a synthesized add/remove/raise accessor — kotc synthesis defect");

            var classOwner = TypeJson.Read(backing["classOverrideOwner"]) as TypeNode.Fqn;
            var openEvent = Str(backing["modality"]) switch
            {
                "open" => true,
                "final" => false,
                _ => throw new InvalidOperationException($"bir2cir: clrEvent '{name}' has no concrete source modality"),
            };
            if (Str(backing[KotlinPropertyAccessors.AssociationKey]) is string association)
                foreach (var getter in methods.OfType<JsonObject>().Where(method =>
                    Str(method[KotlinPropertyAccessors.AssociationKey]) == association
                    && Str(method[KotlinPropertyAccessors.KindKey]) == "get"))
                    getter["clrFinal"] = !openEvent && getter["virtual"]?.GetValue<bool>() == true;
            var baseType = classOwner == null ? null : ClrMemberResolution.ResolveOwnerType(classOwner, refs);
            EventInfo baseEvent = null;
            if (baseType != null
                && !TryResolveDeclaredEvent(baseType, classOwner.Args ?? Array.Empty<TypeNode>(), name,
                    out baseEvent, out _))
                throw new InvalidOperationException($"bir2cir: overridden class event '{classOwner.Name}.{name}' was not found");

            // The concrete delegate `D` + its Invoke param nodes (raise's signature).
            var (delegateNode, invokeParamNodes, parameterSignature) = ResolveImplementationDelegate(name, addM, backing, refs, locals);

            if (Str(backing["policyField"]) is string policyField)
                RewritePolicy(type, fields, name, policyField, addM, remM, raiseM,
                    delegateNode, invokeParamNodes, refs);
            else
            {
                var fieldName = name + "$delegate";
                fields.Add(new JsonObject { ["name"] = fieldName, ["type"] = delegateNode.DeepClone(),
                    ["vis"] = "private", ["static"] = isStatic });
                RewriteFieldLike(addM, "add", fieldName, delegateNode);
                RewriteFieldLike(remM, "remove", fieldName, delegateNode);
                RewriteRaise(raiseM, fieldName, delegateNode, invokeParamNodes);
            }

            void Slot(JsonObject method, MethodInfo externalSlot, bool reusesSlot, bool needsInterfaceSlot)
            {
                var isVirtual = !isStatic && (reusesSlot || openEvent || needsInterfaceSlot);
                method["override"] = !isStatic && reusesSlot;
                method["virtual"] = isVirtual;
                method["clrFinal"] = isVirtual && !openEvent;
                if (externalSlot == null) return;
                // A MethodImpl owner is a declaration, not an ordinary covariant value-view type. Keep it in the
                // existing descriptor path so carrier projection preserves the exact native declaring class.
                method["clrBaseImpls"] = new JsonArray(new JsonObject
                {
                    ["owner"] = TypeJson.Write(classOwner), ["member"] = externalSlot.Name, ["arity"] = 0,
                    ["params"] = new JsonArray(((JsonArray)method["params"]).OfType<JsonObject>()
                        .Select(parameter => parameter["type"].DeepClone()).ToArray()),
                    ["ret"] = TypeJson.Write(NetTypeToNode(externalSlot.ReturnType)),
                });
            }
            MethodInfo Overridable(MethodInfo method) => baseEvent?.DeclaringType?.IsInterface == false
                && method is { IsVirtual: true, IsFinal: false }
                && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly) ? method : null;
            var localBase = classOwner != null && baseType == null;
            if (localBase)
                foreach (var method in new[] { addM, remM })
                {
                    // Optional source-function signature discriminators are part of the declared native slot too.
                    // A covariant handler or Nothing inference must not allocate a different override signature.
                    var parameter = (JsonObject)method["params"][0];
                    parameter.Remove(FunctionSignatureIdentity.Key);
                    if (parameterSignature != null)
                        parameter[FunctionSignatureIdentity.Key] = TypeJson.Write(parameterSignature);
                }
            var baseAdd = Overridable(baseEvent?.GetAddMethod(true));
            var baseRemove = Overridable(baseEvent?.GetRemoveMethod(true));
            var baseRaise = Overridable(baseEvent?.GetRaiseMethod(true));
            // Even a final new declaration can satisfy an interface introduced by a future subclass. CLR requires
            // virtual accessors for that inherited implementation; final seals source overrides without losing it.
            Slot(addM, baseAdd, localBase || baseAdd != null, true);
            Slot(remM, baseRemove, localBase || baseRemove != null, true);
            Slot(raiseM, baseRaise, localBase || baseRaise != null, false);

            var raiseIdentity = DeclarationIdentityBinding.PhysicalOnlyId(
                "clr-event-accessor:" + new JsonArray(Str(type["name"]), Str(raiseM["name"])).ToJsonString(), "accessor");
            raiseM[DeclarationIdentityBinding.Key] = raiseIdentity;
            decls.Add(new JsonObject {
                ["k"] = "clrEventDecl", ["name"] = name, ["delegateType"] = delegateNode.DeepClone(),
                [RaiseDeclarationKey] = raiseIdentity,
            });
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

    static (JsonNode Delegate, List<JsonNode> Parameters, TypeNode Signature) ResolveImplementationDelegate(
        string name, JsonObject add, JsonObject backing, ReferenceMetadataIndex refs, LocalEvents locals)
    {
        if (TypeJson.Read(backing["classOverrideOwner"]) is TypeNode.Fqn owner
            && locals.TryGet(owner, name, out var declaration))
        {
            var (type, parameters, signature) = ResolveImplementationDelegate(name, declaration.Add, declaration.Backing, refs, locals);
            var arguments = owner.Args ?? Array.Empty<TypeNode>();
            JsonNode Substitute(JsonNode node) => TypeJson.Write(SubstituteOwnerTypeVariables(TypeJson.Read(node), arguments));
            return (Substitute(type), parameters.Select(Substitute).ToList(),
                signature == null ? null : SubstituteOwnerTypeVariables(signature, arguments));
        }
        var (delegateType, invokeParameters) = ResolveDelegate(name, add, backing["handlerType"], refs,
            policyArguments: backing["policyField"] != null);
        var parameter = add["params"]?[0] as JsonObject;
        var sourceType = parameter?[FunctionSignatureIdentity.Key] ?? parameter?["type"];
        var discriminator = FunctionValueRepresentation.ContainsOrdinaryFunction(sourceType)
            ? FunctionSignatureIdentity.DeclarationType(TypeJson.Read(sourceType)) : null;
        return (delegateType, invokeParameters, discriminator);
    }

    // Select the kotc-synthesized shell by its semantic marker, never a user overload that merely shares `add_E`.
    static JsonObject FindAccessor(JsonArray methods, string name, string kind) =>
        methods.OfType<JsonObject>().FirstOrDefault(m =>
            Str(m["name"]) == name
            && m["body"] is JsonArray { Count: 1 } body
            && body[0] is JsonObject marker
            && Str(marker["k"]) == "clrEventAccessor"
            && Str(marker["kind"]) == kind);

    // The frontend names the actual Kotlin delegate field. Its initializer and ordering remain ordinary Kotlin;
    // this pass only supplies the native event accessors. Calls carry the exact stdlib declaration identity, so
    // the existing member/representation pipeline closes their frames and converts the native handler normally.
    static void RewritePolicy(JsonObject type, JsonArray fields, string eventName, string fieldName,
        JsonObject add, JsonObject remove, JsonObject raise, JsonNode delegateType,
        List<JsonNode> invokeParameters, ReferenceMetadataIndex refs)
    {
        bool ObjectArgument(TypeNode argument) => argument switch
        {
            TypeNode.Nullable nullable => ObjectArgument(nullable.Of),
            TypeNode.Oblivious oblivious => ObjectArgument(oblivious.Of),
            TypeNode.Mod modified => ObjectArgument(modified.Of),
            TypeNode.ByRef or TypeNode.Ptr or TypeNode.FnPtr => false,
            TypeNode.Fqn named => !refs.IsByRefLikeFqn(named),
            _ => true,
        };
        for (var i = 0; i < invokeParameters.Count; i++)
            if (!ObjectArgument(TypeJson.Read(invokeParameters[i])))
                throw new InvalidOperationException($"bir2cir: custom CLR event '{eventName}' parameter {i} "
                    + "cannot be represented in the ClrEventPolicy.invoke Any? argument array: "
                    + invokeParameters[i].ToJsonString());
        var field = fields.OfType<JsonObject>().Single(f => Str(f["name"]) == fieldName);
        var policyType = field["type"] ?? throw new InvalidOperationException("CLR event policy field has no type");
        var ownerArguments = TypeParameterFrame.Declarations(type)
            .Select((_, index) => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        var owner = new TypeNode.Fqn(Str(type["name"]), ownerArguments.Length == 0 ? null : ownerArguments);
        var isStatic = add["static"]?.GetValue<bool>() == true;
        JsonObject Receiver() => new()
        {
            ["k"] = isStatic ? "staticField" : "field", ["ownerType"] = TypeJson.Write(owner), ["name"] = fieldName,
            ["recv"] = isStatic ? null : new JsonObject { ["k"] = "this", ["sty"] = TypeJson.Write(owner) },
            ["sty"] = policyType.DeepClone(),
        };
        JsonObject Call(string name, TypeNode parameter, JsonNode argument)
        {
            var declaration = refs.AuthoredKotlinHelper("kotlin.clr.ClrEventPolicy", name, 0,
                new[] { parameter }, isStatic: false);
            return new JsonObject
            {
                ["k"] = "callInstance", ["ownerType"] = policyType.DeepClone(), ["recv"] = Receiver(),
                ["method"] = name, ["virtual"] = true,
                [DeclarationIdentityBinding.Key] = declaration.DeclarationId,
                ["sig"] = new JsonArray(TypeJson.Write(parameter)), ["args"] = new JsonArray(argument),
                ["ret"] = TypeJson.Fqn("kotlin.Unit"), ["sty"] = TypeJson.Fqn("kotlin.Unit"),
            };
        }
        void Accessor(JsonObject method, string name)
        {
            method["params"][0]["type"] = delegateType.DeepClone();
            method["body"] = new JsonArray(new JsonObject
            {
                ["k"] = "exprStmt", ["expr"] = Call(name, new TypeNode.Tv("type", 0), new JsonObject
                {
                    ["k"] = "local", ["name"] = "value", ["sty"] = delegateType.DeepClone(),
                }),
            });
            method["specialName"] = true;
            method.Remove("overrides");
        }
        Accessor(add, "addHandler");
        Accessor(remove, "removeHandler");
        var parameters = new JsonArray();
        var arguments = new JsonArray();
        for (var i = 0; i < invokeParameters.Count; i++)
        {
            parameters.Add(new JsonObject { ["name"] = "arg" + i, ["type"] = invokeParameters[i].DeepClone() });
            arguments.Add(new JsonObject { ["k"] = "local", ["name"] = "arg" + i, ["sty"] = invokeParameters[i].DeepClone() });
        }
        var element = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var arrayType = new TypeNode.Array(element);
        raise["params"] = parameters;
        raise["body"] = new JsonArray(new JsonObject
        {
            ["k"] = "exprStmt", ["expr"] = Call("invoke", arrayType, new JsonObject
            {
                ["k"] = "newArray", ["elem"] = TypeJson.Write(element), ["elems"] = arguments,
                ["sty"] = TypeJson.Write(arrayType),
            }),
        });
        raise["specialName"] = true;
        raise.Remove("overrides");
    }

    // add_/remove_: param[0].type := D; body := the CAS-loop directive; drop the (consumed) overrides closure.
    static void RewriteFieldLike(JsonObject m, string kind, string fieldName, JsonNode delegateNode)
    {
        if (m["params"] is JsonArray ps && ps.Count > 0 && ps[0] is JsonObject p0)
            p0["type"] = delegateNode.DeepClone();
        m["body"] = new JsonArray { AccessorImpl(kind, fieldName, delegateNode, m["static"]?.GetValue<bool>() == true) };
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
        m["body"] = new JsonArray { AccessorImpl("raise", fieldName, delegateNode, m["static"]?.GetValue<bool>() == true) };
        m["specialName"] = true;   // ECMA-335 event-accessor convention (the .event's Fire)
        m.Remove("overrides");
    }

    static JsonObject AccessorImpl(string kind, string fieldName, JsonNode delegateNode, bool isStatic) => new()
    {
        ["k"] = "clrEventAccessorImpl",
        ["kind"] = kind,
        ["name"] = fieldName,
        ["delegateType"] = delegateNode.DeepClone(),
        ["static"] = isStatic,
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
        JsonNode constructedOwner = null, bool policyArguments = false)
    {
        // Preserve the native declaration's unboxability fact before a reflection Type is projected to a
        // named Kotlin handler signature. Raw pointers are not object-array values, regardless of spelling.
        void CheckPointerArguments(MethodInfo invoke)
        {
            if (!policyArguments || invoke == null) return;
            var parameters = invoke.GetParameters();
            for (var i = 0; i < parameters.Length; i++)
                if (parameters[i].ParameterType.IsPointer || parameters[i].ParameterType.IsFunctionPointer)
                    throw new InvalidOperationException($"bir2cir: custom CLR event '{name}' parameter {i} "
                        + "cannot be represented in the ClrEventPolicy.invoke Any? argument array: native pointer");
        }
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
                    iface = ClrMemberResolution.ResolveOwnerType(
                        overrideSpec ?? new TypeNode.Fqn(owner), refs);
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
                CheckPointerArguments(invoke);
                var invokeParams = invoke?.GetParameters()
                    .Select(p => TypeJson.Write(SubstituteOwnerTypeVariables(NetTypeToNode(p.ParameterType), ownerArgs))).ToList()
                                   ?? new List<JsonNode>();
                return (TypeJson.Write(SubstituteOwnerTypeVariables(NetTypeToNode(d), ownerArgs)), invokeParams);
            }

        // A NEW event: the overrides closure was empty (no interface slot). Map the handler fn -> Action/Func.
        if (handlerType is JsonObject fn && Str(fn["t"]) == "fn")
            return FnToDelegate(fn);

        // A dll2klib-projected nominal delegate is the other natural Kotlin spelling of an event handler. Keep its
        // semantic classifier as the field/accessor type (the ordinary physical type lowering applies ClrExternal),
        // and derive only the raise parameter vector from the exact referenced Invoke declaration here.
        var handler = TypeJson.Read(handlerType);
        while (handler is TypeNode.Nullable nullable) handler = nullable.Of;
        while (handler is TypeNode.Oblivious oblivious) handler = oblivious.Of;
        if (handler is TypeNode.Fqn named && refs.IsClrDelegate(named))
        {
            var declaration = ClrMemberResolution.ResolveOwnerType(named, refs);
            var invoke = declaration?.GetMethod("Invoke");
            if (invoke != null)
            {
                CheckPointerArguments(invoke);
                var ownerArgs = named.Args ?? Array.Empty<TypeNode>();
                var invokeParams = invoke.GetParameters()
                    .Select(p => TypeJson.Write(SubstituteOwnerTypeVariables(
                        NetTypeToNode(p.ParameterType), ownerArgs)))
                    .ToList();
                return (TypeJson.Write(named), invokeParams);
            }
        }

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
                member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
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

    // The frontend selects the declaring event and its constructed owner. Local compiler-authored event records
    // identify their actual raise MethodDef; referenced Kotlin declarations use Event/MethodSemantics. A native
    // foreign .fire association alone does not sanction a Kotlin event raise.
    static void BindRaises(JsonNode node, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<(string Owner, string Event), RaiseBinding> signatures)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
                if (kv.Value != null) BindRaises(kv.Value, refs, signatures);
            if (Str(obj["k"]) == "clrEventRaise")
            {
                var owner = TypeJson.Read(obj["type"]) as TypeNode.Fqn
                    ?? throw new InvalidOperationException("bir2cir: event raise has no declaring owner type");
                var name = Str(obj["event"]);
                var recv = obj["recv"]?.DeepClone();
                var args = (JsonArray)(obj["args"]?.DeepClone() ?? new JsonArray());
                var superCall = obj["super"]?.GetValue<bool>() == true;
                JsonObject call;
                if (signatures.TryGetValue((owner.Name, name), out var local))
                    call = new JsonObject {
                        ["k"] = local.IsStatic ? "callStatic" : "callInstance",
                        [local.IsStatic ? "owner" : "ownerType"] = TypeJson.Write(owner), ["method"] = local.Name,
                        ["sig"] = local.Signature.DeepClone(), [DeclarationIdentityBinding.Key] = local.DeclarationId,
                        ["virtual"] = !local.IsStatic, ["recv"] = recv, ["args"] = args,
                    };
                else
                {
                    var referenced = ClrMemberResolution.ResolveOwnerType(owner, refs);
                    if (referenced == null || !refs.HasDotKtOwner(owner.Name))
                        throw new InvalidOperationException(
                            $"bir2cir: cannot raise the .NET event '{name}' on '{owner.Name}' — you can only raise an "
                            + "event you DECLARE in Kotlin (`override val E by clrEvent()`), not a consumed foreign .NET event");
                    var declaration = referenced.GetEvent(name, BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    var raise = declaration?.GetRaiseMethod(true)
                        ?? throw new InvalidOperationException($"bir2cir: referenced Kotlin event '{owner.Name}.{name}' has no raise declaration");
                    call = raise.IsStatic
                        ? ClrMemberResolution.ExactStaticCall(refs, raise, owner, args)
                        : ClrMemberResolution.ExactInstanceCall(refs, raise, owner, recv, args);
                }
                if (Str(call["k"]) is "callStatic" or "clrStatic") call.Remove("recv");
                if (superCall)
                {
                    call["super"] = true;
                    call["virtual"] = false;
                    // A base cast hides the enclosing derived receiver from the existing lifted-super forwarder.
                    call["recv"] = obj["recv"]?.DeepClone();
                }
                foreach (var k in obj.Select(kv => kv.Key).ToList()) obj.Remove(k);
                foreach (var pair in call) obj[pair.Key] = pair.Value?.DeepClone();
            }
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) BindRaises(it, refs, signatures);
    }
}
