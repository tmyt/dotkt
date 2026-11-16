using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

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
static partial class ClrEventImplBinding
{
    sealed record RaiseBinding(string Name, TypeNode[] Signature, string DeclarationId, bool IsStatic);

    // Event accessors adopt their native delegate slot later. Keep Kotlin's complete handler type first,
    // including nominal nullability and covariant Nothing overrides, not only ordinary function shapes.
    public static BirFileDeclaration[] PreserveSourceFacts(IReadOnlyList<BirFileDeclaration> roots)
    {
        static OptionalField<string> Present(OptionalField<string> field) => field.HasValue ? field : OptionalField<string>.Null;
        OptionalField<BirFunctionDeclaration[]> Methods(OptionalField<BirFunctionDeclaration[]> methods,
            IEnumerable<(OptionalField<string> Name, OptionalField<string> Association)> events)
        {
            if (!methods.HasValue) return methods;
            return methods.Value.Select(method => {
                var carrier = method.KotlinPropertyAccessorCarrier;
                if (method.PropertyAccessor.OrDefault() == "get")
                {
                    if (method.FakeOverride.OrDefault() && method.EventProperty.OrDefault())
                        carrier = new EventPropertySourceFacts {
                            Name = Present(method.PropertyName), Kind = "get",
                            Association = Present(method.PropertyAssociation), Inherited = true,
                        };
                    if (method.PropertyAssociation.HasValue)
                        foreach (var declaration in events)
                            if (method.PropertyAssociation.Value == declaration.Association.OrDefault())
                                carrier = new EventPropertySourceFacts {
                                    Name = Present(method.PropertyName), Kind = "get",
                                    Association = Present(declaration.Association), Event = Present(declaration.Name),
                                };
                }
                var parameters = method.Params;
                if (method.Body.OrEmpty().FirstOrDefault() is BirClrEventAccessor accessor
                    && accessor.Kind.OrDefault() is "add" or "remove"
                    && parameters.OrDefault() is { Length: 1 } slots && !slots[0].KotlinType.HasValue)
                    parameters = new[] { slots[0] with { KotlinType = slots[0].Type } };
                return method with { EventProperty = default, KotlinPropertyAccessorCarrier = carrier, Params = parameters };
            }).ToArray();
        }
        var none = Array.Empty<(OptionalField<string>, OptionalField<string>)>();
        IBirTypeDeclaration Owner(IBirTypeDeclaration declaration) => declaration switch {
            BirClassDefinition owner => owner with { Methods = Methods(owner.Methods,
                owner.ClrEvents.OrEmpty().Select(value => (value.Name, value.PropertyAssociation))
                    .Concat(owner.ClrEventForwarders.OrEmpty().Select(value => (value.Name, value.PropertyAssociation)))) },
            BirInterfaceDefinition owner => owner with { Methods = Methods(owner.Methods, none) },
            BirEnumDefinition owner => owner with { Methods = Methods(owner.Methods, none) },
            _ => throw new InvalidOperationException("Unaccounted event-source owner: " + declaration.GetType().Name),
        };
        return roots.Select(root => root with { Methods = Methods(root.Methods, none),
            Types = root.Types.HasValue ? root.Types.Value.Select(Owner).ToArray() : root.Types,
        }).ToArray();
    }

    internal static void SelfTestSourceFacts()
    {
        var handler = new TypeNode.Fqn("probe.Handler", new TypeNode[] { new TypeNode.Tv("method", 2) });
        var earlier = new TypeNode.Nullable(handler);
        BirFunctionDeclaration Getter(string name, string association) => new() {
            Name = "read", PropertyName = name, PropertyAccessor = "get", PropertyAssociation = association,
            FakeOverride = true, EventProperty = true,
        };
        BirFunctionDeclaration Accessor(string kind, OptionalField<TypeNode> previous) => new() {
            Name = kind, Body = new IBirStatement[] { new BirClrEventAccessor { Kind = kind, Event = "Changed" } },
            Params = new[] { new BirParameter { Name = "handler", Type = handler, KotlinType = previous } },
        };
        var methods = new[] { Getter("Changed.日本", "decl"), Getter("Forwarded", "forward"),
            Getter("Inherited", "base"), Accessor("add", default), Accessor("remove", earlier), Accessor("raise", default) };
        var owner = new BirClassDefinition { Kind = "class", Name = "probe.Owner", Methods = methods,
            ClrEvents = new[] { new BirClrEventBacking { Name = "Changed", PropertyAssociation = "decl" } },
            ClrEventForwarders = new[] { new BirClrEventForwarder { Name = "Forwarded", PropertyAssociation = "forward" } },
        };
        var input = new BirFileDeclaration { Methods = new[] { methods[2] }, Types = new IBirTypeDeclaration[] {
            owner, new BirInterfaceDefinition { Kind = "interface", Methods = new[] { methods[2] } },
            new BirEnumDefinition { Kind = "enum", Methods = new[] { methods[2] } },
        } };
        var result = PreserveSourceFacts(new[] { input }).Single();
        var rewritten = ((BirClassDefinition)result.Types.Value[0]).Methods.Value;
        var declared = rewritten[0].KotlinPropertyAccessorCarrier.Value;
        var forwarded = rewritten[1].KotlinPropertyAccessorCarrier.Value;
        var inherited = rewritten[2].KotlinPropertyAccessorCarrier.Value;
        if (declared.Name.Value != "Changed.日本" || declared.Association.Value != "decl"
            || declared.Event.Value != "Changed" || declared.Inherited.IsPresent
            || forwarded.Event.Value != "Forwarded" || forwarded.Association.Value != "forward"
            || !inherited.Inherited.Value || inherited.Event.IsPresent
            || rewritten.Any(method => method.EventProperty.IsPresent)
            || rewritten[3].Params.Value[0].KotlinType.Value != handler
            || rewritten[4].Params.Value[0].KotlinType.Value != earlier
            || rewritten[5].Params.Value[0].KotlinType.IsPresent
            || !methods[0].EventProperty.Value || methods[0].KotlinPropertyAccessorCarrier.IsPresent
            || methods[3].Params.Value[0].KotlinType.IsPresent)
            throw new InvalidOperationException("Typed event source capture lost association, source type precedence or input ownership");
        foreach (var method in new[] { result.Methods.Value[0],
            ((BirInterfaceDefinition)result.Types.Value[1]).Methods.Value[0],
            ((BirEnumDefinition)result.Types.Value[2]).Methods.Value[0] })
            if (!method.KotlinPropertyAccessorCarrier.Value.Inherited.Value || method.EventProperty.IsPresent)
                throw new InvalidOperationException("Typed event source capture missed a declaration owner");
        // An explicit selected declaration supersedes inherited bookkeeping.
        var overriding = owner with { ClrEventForwarders = new[] {
            new BirClrEventForwarder { Name = "Selected", PropertyAssociation = "decl" },
        } };
        var selected = ((BirClassDefinition)PreserveSourceFacts(new[] { input with {
            Types = new IBirTypeDeclaration[] { overriding },
        } }).Single().Types.Value[0]).Methods.Value[0].KotlinPropertyAccessorCarrier.Value;
        if (selected.Event.Value != "Selected" || selected.Inherited.IsPresent)
            throw new InvalidOperationException("Typed event source capture changed declaration precedence");
        var nullFacts = new EventPropertySourceFacts { Name = OptionalField<string>.Null,
            Kind = "get", Association = OptionalField<string>.Null, Inherited = true };
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream)) nullFacts.WriteJson(writer);
        var reader = new System.Text.Json.Utf8JsonReader(stream.ToArray());
        if (!reader.Read()) throw new InvalidOperationException();
        var roundtrip = BirGraphJson.ReadEventPropertySourceFacts(ref reader);
        if (reader.Read() || !roundtrip.Name.IsNull || !roundtrip.Association.IsNull
            || roundtrip.Event.IsPresent || !roundtrip.Inherited.Value
            || !JsonNode.DeepEquals(nullFacts.ToDom(), roundtrip.ToDom()))
            throw new InvalidOperationException("Typed event facts changed absent/null metadata at the codec boundary");
        Console.WriteLine("[event source facts] self-test OK (declared/forwarded/inherited association, source frame, first writer, typed leaf codec)");
    }

    sealed record LocalEventBinding(
        TypeNode DelegateType,
        TypeNode.Fqn AccessorOwner,
        TypeNode[] AddSignature,
        TypeNode[] RemoveSignature,
        string AddIdentity,
        string RemoveIdentity,
        string AddVisibility,
        string RemoveVisibility);

    // Select the kotc-synthesized shell by its semantic marker, never a user overload that merely shares `add_E`.
    static bool IsEventAccessor(string actualName, string expectedName, int statements,
        string marker, string actualKind, string expectedKind) => actualName == expectedName
        && statements == 1 && marker == "clrEventAccessor" && actualKind == expectedKind;

    // The concrete delegate type node for an event + the type nodes of its Invoke's params (raise's signature). For an
    // OVERRIDE (the add_ accessor's `overrides` closure names the interface event slot) the delegate is the interface
    // event's `EventHandlerType`, read off the ref.dll — bir2cir OWNS this resolution (§9). For a NEW event (no override)
    // the handler Kotlin FUNCTION type maps to a System.Action/Func delegate of the same shape.
    static (TypeNode Delegate, TypeNode[] Parameters) ResolveDelegate(
        string name, IEnumerable<BirOverride> overrides, TypeNode handlerType, ReferenceMetadataIndex refs,
        TypeNode constructedOwner = null, bool policyArguments = false)
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
        foreach (var o in overrides)
        {
            var owner = (o.Owner.OrDefault() as TypeNode.Fqn)?.Name;
            var member = o.Member.OrDefault();
            if (owner == null || member == null) continue;
            var ownerSpec = constructedOwner as TypeNode.Fqn;
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
                var overrideSpec = o.Owner.OrDefault() as TypeNode.Fqn;
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
                .Select(p => SubstituteOwnerTypeVariables(NetTypeToNode(p.ParameterType), ownerArgs)).ToArray()
                               ?? Array.Empty<TypeNode>();
            return (SubstituteOwnerTypeVariables(NetTypeToNode(d), ownerArgs), invokeParams);
        }

        // Nullability belongs to the logical handler contract, already preserved on the accessor metadata.
        // Select the native delegate from its shape, just as for a nullable nominal delegate below.
        var handler = handlerType;
        while (handler is TypeNode.Nullable nullable) handler = nullable.Of;
        while (handler is TypeNode.Oblivious oblivious) handler = oblivious.Of;

        // A NEW event: the overrides closure was empty (no interface slot). Map the handler fn -> Action/Func.
        if (handler is TypeNode.Fn fn)
            return FnToDelegate(fn);

        // A dll2klib-projected nominal delegate is the other natural Kotlin spelling of an event handler. Keep its
        // semantic classifier as the field/accessor type (the ordinary physical type lowering applies ClrExternal),
        // and derive only the raise parameter vector from the exact referenced Invoke declaration here.
        if (handler is TypeNode.Fqn named && refs.IsClrDelegate(named))
        {
            var declaration = ClrMemberResolution.ResolveOwnerType(named, refs);
            var invoke = declaration?.GetMethod("Invoke");
            if (invoke != null)
            {
                CheckPointerArguments(invoke);
                var ownerArgs = named.Args ?? Array.Empty<TypeNode>();
                var invokeParams = invoke.GetParameters()
                    .Select(p => SubstituteOwnerTypeVariables(NetTypeToNode(p.ParameterType), ownerArgs))
                    .ToArray();
                return (BirGraphValue.CopyType(named), invokeParams);
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
    static (TypeNode, TypeNode[]) FnToDelegate(TypeNode.Fn fn)
    {
        var parameters = fn.Params.Select(BirGraphValue.CopyType).ToArray();
        var voidReturn = fn.Ret == null || fn.Ret is TypeNode.Fqn { Name: "kotlin.Unit" or "void" };
        var arguments = voidReturn ? parameters.Select(BirGraphValue.CopyType).ToArray()
            : parameters.Select(BirGraphValue.CopyType).Append(BirGraphValue.CopyType(fn.Ret)).ToArray();
        return (new TypeNode.Fqn(voidReturn ? "System.Action" : "System.Func",
            arguments.Length == 0 ? null : arguments), parameters);
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

}
