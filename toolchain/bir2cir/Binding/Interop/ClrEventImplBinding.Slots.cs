using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrEventImplBinding
{
    sealed record LocalEventRow(string Kind, string Name, TypeNode Delegate);
    sealed record LocalAccessorRow(string Name, bool Special, bool HasParameters,
        Func<TypeNode[]> Signature, Func<string> Identity, Action<string> BindIdentity, string Visibility);

    // Owning declarations supply live readers to this selection rule.
    // The graph's constructed-edge authority remains SupertypeGraph; no event
    // catalog or declaration provenance is reconstructed from an emitted body.
    sealed class LocalEventResolver(
        IReadOnlyDictionary<string, SupertypeGraph.Def> definitions,
        Func<SupertypeGraph.Def, IEnumerable<LocalEventRow>> events,
        Func<SupertypeGraph.Def, IEnumerable<LocalAccessorRow>> accessors)
    {
        (TypeNode[] Signature, string Identity, string Visibility) Accessor(SupertypeGraph.Def definition, string name)
        {
            var candidates = accessors(definition).Where(method => method.Name == name && method.Special && method.HasParameters).ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException($"bir2cir: synthesized local event accessor '{definition.Name}.{name}' resolves to {candidates.Length} declarations");
            var selected = candidates[0];
            var identity = selected.Identity();
            if (identity == null)
            {
                identity = DeclarationIdentityBinding.PhysicalOnlyId(
                    "clr-event-accessor:" + EventAccessorIdentity(definition.Name, name), "accessor");
                selected.BindIdentity(identity);
            }
            return (selected.Signature(), identity, selected.Visibility ?? "public");
        }

        LocalEventBinding FromOwner(TypeNode.Fqn owner, string name)
        {
            if (!definitions.ContainsKey(owner.Name)) return null;
            var pending = new Queue<TypeNode.Fqn>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(owner);
            while (pending.Count != 0)
            {
                var count = pending.Count;
                var matches = new List<LocalEventBinding>();
                for (var index = 0; index < count; index++)
                {
                    var spec = pending.Dequeue();
                    if (!seen.Add(SupertypeGraph.TypeKey(spec)) || !definitions.TryGetValue(spec.Name, out var definition)) continue;
                    var arguments = SupertypeGraph.EffectiveArgs(spec, definition.Arity);
                    if (arguments == null)
                        throw new InvalidOperationException($"bir2cir: local clrEvent owner '{spec.Name}' has {definition.Arity} type parameter(s), "
                            + "but the subscription owner does not carry that constructed argument vector");
                    foreach (var declaration in events(definition))
                    {
                        if (declaration.Kind != "clrEventDecl" || declaration.Name != name) continue;
                        var delegateType = declaration.Delegate
                            ?? throw new InvalidOperationException($"bir2cir: local clrEvent '{spec.Name}.{name}' has no concrete delegate type");
                        var add = Accessor(definition, "add_" + name);
                        var remove = Accessor(definition, "remove_" + name);
                        matches.Add(new LocalEventBinding(SupertypeGraph.SubstOwnerTvs(delegateType, arguments), spec,
                            add.Signature, remove.Signature, add.Identity, remove.Identity, add.Visibility, remove.Visibility));
                    }
                    foreach (var parent in definition.Interfaces)
                        if (SupertypeGraph.SubstOwnerTvs(parent, arguments) is TypeNode.Fqn constructed) pending.Enqueue(constructed);
                    if (definition.Base != null && SupertypeGraph.SubstOwnerTvs(definition.Base, arguments) is TypeNode.Fqn baseType)
                        pending.Enqueue(baseType);
                }
                if (matches.Count == 0) continue;
                var distinct = Distinct(matches);
                if (distinct.Length != 1)
                    throw new InvalidOperationException($"bir2cir: local clrEvent '{owner.Name}.{name}' resolves to {distinct.Length} "
                        + "different accessor declarations at the same inheritance depth");
                return distinct[0];
            }
            return null;
        }

        static LocalEventBinding[] Distinct(IEnumerable<LocalEventBinding> matches)
            => matches.GroupBy(match => SupertypeGraph.TypeKey(match.AccessorOwner), StringComparer.Ordinal).Select(group => group.First()).ToArray();

        internal LocalEventBinding Resolve(TypeNode owner, string name, Func<TypeNode.Tv, IEnumerable<TypeNode>> constraints)
        {
            if (name == null) return null;
            if (owner is TypeNode.Fqn named) return FromOwner(named, name);
            if (owner is not TypeNode.Tv variable || constraints(variable) is not { } bounds) return null;
            var matches = Distinct(bounds.OfType<TypeNode.Fqn>().Select(bound => FromOwner(bound, name)).Where(binding => binding != null));
            if (matches.Length > 1)
                throw new InvalidOperationException($"bir2cir: local clrEvent '{name}' on '{variable.Scope}' type parameter {variable.I} resolves to "
                    + $"{matches.Length} different accessor declarations");
            return matches.SingleOrDefault();
        }
    }

    // Bind cross-file local subscriptions after every event backing has its concrete
    // declaration. Referenced events remain for ClrMemberResolution's EventInfo path.
    internal static void BindLocalSubscriptionsAll(IReadOnlyList<BirFileDeclaration> roots)
    {
        var definitions = SupertypeGraph.CollectSource(roots);
        // These are the actual writers associated with CollectSource's last
        // declaration occurrence, not a second declaration-selection policy.
        var owners = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var owner in roots.SelectMany(file => file.Types.OrEmpty()))
            if (owner.Name.HasValue) owners[owner.Name.Value] = owner;
        var resolver = new LocalEventResolver(definitions,
            definition => owners[definition.Name].ClrEvents.OrEmpty().Select(declaration => new LocalEventRow(
                declaration.K.OrDefault(), declaration.Name.OrDefault(), declaration.DelegateType.OrDefault())),
            definition => owners[definition.Name].Methods.OrEmpty().Select(method => new LocalAccessorRow(
                method.Name.OrDefault(), method.SpecialName.OrDefault(), method.Params.HasValue,
                () => method.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray(),
                () => method.DeclarationId.OrDefault(), identity => method.DeclarationId = identity, method.Vis.OrDefault())));
        var empty = Array.Empty<BirTypeParameter>();
        var scopes = new Dictionary<BirGraphValue, (BirTypeParameter[] Owner, BirTypeParameter[] Method)>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
        {
            scopes[root] = (empty, empty);
            foreach (var method in root.Methods.OrEmpty()) scopes[method] = (empty, method.TypeParams.OrDefault() ?? empty);
            foreach (var owner in root.Types.OrEmpty())
            {
                var frame = TypeParameterFrame.CloneDeclarations(owner);
                scopes[(BirGraphValue)owner] = (frame, empty);
                foreach (var constructor in owner.Ctors.OrEmpty()) scopes[constructor] = (frame, empty);
                foreach (var method in owner.Methods.OrEmpty()) scopes[method] = (frame, method.TypeParams.OrDefault() ?? empty);
            }
        }
        foreach (var root in roots)
            root.VisitChildSlots(slot => Stamp(slot, empty, empty));

        void Stamp(BirGraphSlot slot, BirTypeParameter[] ownerParameters, BirTypeParameter[] methodParameters)
        {
            var node = slot.Value;
            if (node == null || node is IBirExecutablePayload) return;
            if (scopes.TryGetValue(node, out var scope)) (ownerParameters, methodParameters) = scope;
            var (owner, name) = node switch {
                BirEventAdd value => (value.Type.OrDefault(), value.Event.OrDefault()),
                BirEventRemove value => (value.Type.OrDefault(), value.Event.OrDefault()),
                _ => ((TypeNode)null, (string)null),
            };
            var binding = resolver.Resolve(owner, name, variable => {
                var frame = variable.Scope == "type" ? ownerParameters : variable.Scope == "method" ? methodParameters : null;
                return frame != null && variable.I >= 0 && variable.I < frame.Length ? frame[variable.I].Constraints.OrDefault() : null;
            });
            if (binding != null)
            {
                node = BindSourceSubscription(node, binding);
                if (!ReferenceEquals(node, slot.Value)) slot.Replace(node);
            }
            node.VisitChildSlots(child => Stamp(child, ownerParameters, methodParameters));
        }
    }

    static BirGraphValue BindSourceSubscription(BirGraphValue node, LocalEventBinding binding)
    {
        var add = node is BirEventAdd;
        var (type, name, isStatic, receiver, handler) = node switch {
            BirEventAdd value => (value.Type.OrDefault(), value.Event.OrDefault(), value.Static.OrDefault(), value.Recv, value.Handler),
            BirEventRemove value => (value.Type.OrDefault(), value.Event.OrDefault(), value.Static.OrDefault(), value.Recv, value.Handler),
            _ => throw new InvalidOperationException("Not an event subscription"),
        };
        var accessor = (add ? "add_" : "remove_") + name;
        var identity = add ? binding.AddIdentity : binding.RemoveIdentity;
        var signature = (add ? binding.AddSignature : binding.RemoveSignature).Select(Copy).ToArray();
        var cast = new BirCast { Type = Copy(binding.DelegateType), E = handler.IsPresent ? handler : OptionalField<IBirExpression>.Null };
        var dispatch = isStatic ? "call" : type is TypeNode.Tv ? "constrained" : "callvirt";
        var visibility = add ? binding.AddVisibility : binding.RemoveVisibility;
        if (visibility is "private" or "protected")
        {
            var arguments = new IBirExpression[] { cast };
            var result = new TypeNode.Fqn("kotlin.Unit");
            if (isStatic) return new BirCallStatic { Owner = Copy(binding.AccessorOwner), Method = accessor,
                DeclarationId = identity, Sig = signature, Args = arguments, Virtual = false, Ret = result, Sty = Copy(result) };
            if (type is TypeNode.Tv) return new BirConstrainedCall { Iface = Copy(binding.AccessorOwner), RecvType = Copy(type),
                Method = accessor, DeclarationId = identity, Sig = signature, Recv = receiver.IsPresent ? receiver : OptionalField<IBirExpression>.Null,
                Args = arguments, Virtual = true, Ret = result, Sty = Copy(result) };
            return new BirCallInstance { OwnerType = Copy(binding.AccessorOwner), Method = accessor, DeclarationId = identity,
                Sig = signature, Recv = receiver.IsPresent ? receiver : OptionalField<IBirExpression>.Null,
                Args = arguments, Virtual = true, Ret = result, Sty = Copy(result) };
        }
        if (node is BirEventAdd adding)
        {
            adding.Accessor = accessor; adding.AccessorOwner = Copy(binding.AccessorOwner); adding.DeclarationId = identity;
            adding.DelegateType = Copy(binding.DelegateType); adding.Handler = cast; adding.Sig = signature;
            adding.LocalAccessor = true; adding.Dispatch = dispatch;
        }
        else
        {
            var removing = (BirEventRemove)node;
            removing.Accessor = accessor; removing.AccessorOwner = Copy(binding.AccessorOwner); removing.DeclarationId = identity;
            removing.DelegateType = Copy(binding.DelegateType); removing.Handler = cast; removing.Sig = signature;
            removing.LocalAccessor = true; removing.Dispatch = dispatch;
        }
        return node;
    }

    // Resolve cross-file raises after all synthesized raise methods exist. Copy the
    // exact selected signature; ilemit links it without selecting by name/arity.
    internal static void BindRaisesAll(IEnumerable<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var files = roots.ToArray();
        var signatures = new Dictionary<(string Owner, string Event), RaiseBinding>();
        foreach (var owner in files.SelectMany(file => file.Types.OrEmpty()))
        {
            if (!owner.Name.HasValue || !owner.Methods.HasValue) continue;
            foreach (var declaration in owner.ClrEvents.OrEmpty())
            {
                if (!declaration.EventRaiseDeclarationId.HasValue) continue;
                var identity = declaration.EventRaiseDeclarationId.Value;
                var method = owner.Methods.Value.Single(candidate => candidate.DeclarationId.OrDefault() == identity);
                signatures[(owner.Name.Value, declaration.Name.OrDefault())] = new RaiseBinding(method.Name.OrDefault(),
                    method.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray(), identity, method.Static.OrDefault());
                declaration.EventRaiseDeclarationId = default;
            }
        }
        foreach (var file in files) file.VisitChildSlots(slot => BindSourceRaise(slot, refs, signatures));
    }

    static MethodInfo ReferencedRaise(TypeNode.Fqn owner, string name, ReferenceMetadataIndex refs)
    {
        var referenced = ClrMemberResolution.ResolveOwnerType(owner, refs);
        if (referenced == null || !refs.HasDotKtOwner(owner.Name))
            throw new InvalidOperationException($"bir2cir: cannot raise the .NET event '{name}' on '{owner.Name}' — you can only raise an "
                + "event you DECLARE in Kotlin (`override val E by clrEvent()`), not a consumed foreign .NET event");
        var declaration = referenced.GetEvent(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        return declaration?.GetRaiseMethod(true)
            ?? throw new InvalidOperationException($"bir2cir: referenced Kotlin event '{owner.Name}.{name}' has no raise declaration");
    }

    static void BindSourceRaise(BirGraphSlot slot, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<(string Owner, string Event), RaiseBinding> signatures)
    {
        var node = slot.Value;
        if (node == null || node is IBirExecutablePayload) return;
        node.VisitChildSlots(child => BindSourceRaise(child, refs, signatures));
        if (node is not BirEventRaise raise) return;
        var owner = raise.Type.OrDefault() as TypeNode.Fqn
            ?? throw new InvalidOperationException("bir2cir: event raise has no declaring owner type");
        var name = raise.Event.OrDefault();
        var arguments = raise.Args.OrDefault() ?? Array.Empty<IBirExpression>();
        BirGraphValue call;
        if (signatures.TryGetValue((owner.Name, name), out var local))
        {
            call = local.IsStatic
                ? new BirCallStatic { Owner = Copy(owner), Method = local.Name, Sig = local.Signature.Select(Copy).ToArray(),
                    DeclarationId = local.DeclarationId, Virtual = false, Args = arguments }
                : new BirCallInstance { OwnerType = Copy(owner), Method = local.Name, Sig = local.Signature.Select(Copy).ToArray(),
                    DeclarationId = local.DeclarationId, Virtual = true, Recv = raise.Recv.IsPresent ? raise.Recv : OptionalField<IBirExpression>.Null,
                    Args = arguments };
        }
        else
        {
            var method = ReferencedRaise(owner, name, refs);
            call = method.IsStatic
                ? ClrMemberResolution.ExactStaticCall(refs, method, owner, arguments)
                : ClrMemberResolution.ExactInstanceCall(refs, method, owner, raise.Recv.OrDefault(), arguments);
        }
        // Kotlin super access is an instance declaration access. Restore the
        // original derived this instead of the exact binder's base cast.
        if (raise.Super.OrDefault() && call is BirCallInstance instance)
        { instance.Super = true; instance.Virtual = false; instance.Recv = raise.Recv.IsPresent ? raise.Recv : OptionalField<IBirExpression>.Null; }
        slot.Replace(call);
    }
}
