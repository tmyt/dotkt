using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UnsafeAccessorLowering
{
    // Bind a super call while 'this' still belongs to the inline declaration.
    // The same private nonvirtual forwarder is used by its local and exported
    // inline bodies; normal lexical-access lowering handles relocated callers.
    internal static void PrepareInlineSuper(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        SourceAccessContext context = null;
        foreach (var file in roots)
        foreach (var type in file.Types.OrEmpty())
        foreach (var method in type.Methods.OrEmpty())
        {
            if (method.Mods.OrDefault()?.Inline.OrDefault() != true) continue;
            var site = 0;
            BirGraphValue Walk(BirGraphValue node, bool lexicalThis = true)
            {
                if (node is IBirExecutablePayload) return node;
                lexicalThis &= node is not IBirTypeDeclaration;
                node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value, lexicalThis)));
                var sourceReceiver = node switch {
                    BirCallInstance c when c.Super.OrDefault() => c.Recv.OrDefault(),
                    BirNativeInstance n when n.Super.OrDefault() => n.Recv.OrDefault(),
                    BirEventRaise e when e.Super.OrDefault() => e.Recv.OrDefault(),
                    _ => null,
                };
                if (sourceReceiver == null) return node;
                if (context == null)
                {
                    CollectSourceHosts(roots, out var hosts);
                    context = new(roots, hosts, refs, null);
                }
                var host = context.Hosts[type.Name.Value];
                var receiver = sourceReceiver is BirThis or BirDefaultReceiver { Kind: { HasValue: true, Value: "dispatch" } }
                    ? (lexicalThis ? SourceSelfType(host) : null)
                    : sourceReceiver is BirGraphNode expression ? BirNodeType.Of(expression) as TypeNode.Fqn : null;
                if (receiver == null || SourceEnclosingHost(host, receiver.Name, context.Hosts, source: true) is not { } lexicalHost)
                    return node;
                var identity = method.DeclarationId.Value + "|inline-super:" + site++;
                return node is BirEventRaise raise ? PrepareSuperRaise(raise, lexicalHost, receiver, identity)
                    : BuildSourceSuper(node, SourceMethodEdgeOf(node), lexicalHost, receiver, context, identity);
            }
            foreach (var statement in method.Body.OrEmpty()) Walk((BirGraphValue)statement);
            foreach (var parameter in method.Params.OrEmpty())
            foreach (var attribute in parameter.Attrs.OrEmpty())
                if (attribute.Attr.OrDefault() is TypeNode.Fqn { Name: DefaultBirCarrier.AttributeName })
                    DefaultBirCarrier.Rewrite(attribute, expression => (IBirExpression)Walk((BirGraphValue)expression));
        }
    }

    // Keep the raise in its lexical host. The event binder still selects its
    // accessor and performs the vararg length/element checks at its usual phase.
    static BirCallInstance PrepareSuperRaise(BirEventRaise raise, SourceHost host, TypeNode.Fqn receiver, string identity)
    {
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var element = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var args = raise.PackedArgs.HasValue ? new[] { raise.PackedArgs.Value } : raise.Args.Value;
        var parameters = args.Select((_, i) => SourceParameter("arg" + i,
            raise.PackedArgs.HasValue ? new TypeNode.Array(element) : element)).ToArray();
        var reads = parameters.Select(p => (IBirExpression)new BirLocal { Name = p.Name, Sty = p.Type }).ToArray();
        var body = raise with { Recv = OptionalField<IBirExpression>.Of(new BirThis()) };
        if (raise.PackedArgs.HasValue) body.PackedArgs = OptionalField<IBirExpression>.Of(reads[0]);
        else body.Args = reads;
        var name = "dotkt$inlineSuper$" + identity;
        host.Append(new BirFunctionDeclaration {
            Name = name, DeclarationId = identity, Generated = true, Static = false, Virtual = false, Vis = "private",
            Params = parameters, Ret = unit, Body = SourceReturningBody(body, unit), Attrs = Array.Empty<BirAttribute>(),
        }, null);
        var signature = parameters.Select(p => p.Type.Value).ToArray();
        return new BirCallInstance {
            OwnerType = receiver, Virtual = false, Recv = raise.Recv, Args = args, Method = name, DeclarationId = identity,
            Sig = signature, Ret = unit, Sty = unit, MemberVisibility = "private", MemberReturnType = unit,
            MemberSignature = CopySourceTypes(signature), MemberOwnerTypeParams = TypeParameterFrame.Declarations(host.Type).ToArray(),
        };
    }

    static SourceHost SourceEnclosingHost(SourceHost caller, string name, IReadOnlyDictionary<string, SourceHost> hosts,
        bool source = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (caller != null && seen.Add(caller.Name))
        {
            if (caller.Name == name) return caller;
            var owner = source ? caller.Type?.SemanticOwner.OrDefault() : caller.NestedIn;
            caller = owner != null && hosts.TryGetValue(owner, out var parent) ? parent : null;
        }
        return null;
    }
    static TypeNode.Fqn SourceConstructedAncestor(SourceHost start, string target, IReadOnlyDictionary<string, SourceHost> hosts)
    {
        var queue = new Queue<TypeNode.Fqn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(SourceSelfType(start));
        while (queue.TryDequeue(out var current))
        {
            if (!seen.Add(SourceTypeKey(current))) continue;
            if (current.Name == target) return current;
            if (!hosts.TryGetValue(current.Name, out var host) || host.Type == null) continue;
            var arguments = current.Args ?? Array.Empty<TypeNode>();
            void Enqueue(TypeNode type) { if (type is TypeNode.Fqn ancestor) queue.Enqueue((TypeNode.Fqn)SubstituteOwnerSlots(ancestor, arguments)); }
            Enqueue(host.Type.Base.OrDefault());
            foreach (var edge in host.Type.Interfaces.OrEmpty()) Enqueue(edge);
        }
        return null;
    }
    static BirGraphValue RewriteSourceSuper(BirGraphValue node, SourceMethodEdge edge, SourceHost caller, SourceAccessContext context)
    {
        if (edge.Receiver is not BirGraphNode receiver
            || BirNodeType.Of(receiver) is not TypeNode.Fqn receiverType)
            throw new InvalidOperationException("Nested Kotlin super call is missing its receiver/target facts");
        var host = SourceEnclosingHost(caller, receiverType.Name, context.Hosts)
            ?? throw new InvalidOperationException($"Nested Kotlin super receiver '{receiverType.Name}' is not an enclosing physical owner of '{caller.Name}'");
        return BuildSourceSuper(node, edge, host, receiverType, context, null);
    }
    static BirGraphValue BuildSourceSuper(BirGraphValue node, SourceMethodEdge edge, SourceHost host,
        TypeNode.Fqn receiverType, SourceAccessContext context, string publishedIdentity)
    {
        if (edge.Owner is not TypeNode.Fqn targetOwner || edge.Method == null)
            throw new InvalidOperationException("Kotlin super call is missing its target");
        // A super qualifier may inherit the selected declaration without
        // declaring it. Use the override row selected by the frontend, not
        // another class or interface that happens to declare the same name.
        if (publishedIdentity != null && node is BirCallInstance inherited && edge.Identity != null
            && inherited.Overrides.OrEmpty().FirstOrDefault(row => row.DeclarationId.OrDefault() == edge.Identity)
                ?.Owner.OrDefault() is TypeNode.Fqn selectedOwner)
            targetOwner = selectedOwner;
        var suspends = publishedIdentity != null && (node is BirCallInstance c && c.SuspendCall.OrDefault()
            || node is BirNativeInstance n && n.SuspendCall.OrDefault());
        var resolvedOwner = SourceConstructedAncestor(host, targetOwner.Name, context.Hosts) ?? targetOwner;
        var ownerArgs = resolvedOwner.Args ?? Array.Empty<TypeNode>();
        var declarationSignature = edge.MemberSignature ?? edge.Signature;
        var signature = declarationSignature.Select(type => SubstituteOwnerSlots(type
            ?? throw new InvalidOperationException($"Kotlin super call '{edge.Method}' has an untyped parameter"), ownerArgs)).ToArray();
        var declarationReturn = edge.MemberReturn ?? edge.DynamicReturn ?? edge.Return ?? edge.Stamp ?? new TypeNode.Fqn("kotlin.Unit");
        var result = SubstituteOwnerSlots(declarationReturn, ownerArgs);
        var arity = edge.TypeArgs.OrEmpty().Length;
        BirFunctionDeclaration target = null;
        if (context.Hosts.TryGetValue(targetOwner.Name, out var targetHost))
        {
            var candidates = targetHost.LookupMethods.Where(method => !KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
                && (edge.Identity != null ? method.DeclarationId.OrDefault() == edge.Identity : method.Name.OrDefault() == edge.Method)
                && !method.Static.OrDefault() && method.TypeParams.OrEmpty().Length == arity).ToArray();
            target = edge.Identity != null && candidates.Length == 1 ? candidates[0]
                : SelectSourceMethod(candidates, declarationSignature, ownerArgs, method => method.Params.OrEmpty());
            if (target == null) throw new InvalidOperationException($"Nested Kotlin super target '{targetOwner.Name}.{edge.Method}' is not unique");
        }
        var referenced = SourceReferencedMethod(target, edge.Identity, resolvedOwner, edge.Method, arity, false,
            declarationSignature, declarationReturn, edge.MethodParameters, false, context.References);
        var frame = CopySourceParameters(referenced?.TypeParams ?? SourceMethodParameters(target, edge.MethodParameters), ownerArgs: ownerArgs);
        if ((frame?.Length ?? 0) != arity) throw new InvalidOperationException(
            $"Kotlin super call '{targetOwner.Name}.{edge.Method}' has {arity} method arguments but {frame?.Length ?? 0} declaration parameters");
        var key = publishedIdentity ?? $"{host.Name}|super|{targetOwner.Name}|{edge.Method}|{arity}|" + string.Join(";", signature.Select(SourceTypeKey)) + "|" + SourceTypeKey(result);
        // A nested super call goes through a method on the enclosing receiver.
        // Its private body is projectable; its call to the base must stay nonvirtual.
        var localEntry = publishedIdentity == null && arity != 0 ? DeclarationIdentityBinding.PhysicalOnlyId(key, "super-forwarder") : null;
        if (!context.Accessors.TryGetValue(key, out var definition))
        {
            var name = publishedIdentity == null ? SuperForwarderName(edge.Method) : "dotkt$inlineSuper$" + publishedIdentity;
            var parameters = signature.Select((type, index) => SourceParameter("arg" + index, type)).ToArray();
            // Copy only the call header. Original executable operands move to
            // the continuing outer call; the forwarder gets fresh this/locals.
            var self = OptionalField<IBirExpression>.Of(new BirThis());
            var args = parameters.Select(p => (IBirExpression)SourceLocal(p.Name.Value)).ToArray();
            var typeArgs = arity == 0 ? default : OptionalField<TypeNode[]>.Of(Enumerable.Range(0, arity)
                .Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray());
            IBirExpression targetCall = node switch {
                BirNativeInstance native => native with {
                    Recv = self, Type = CopySourceType(resolvedOwner), Args = args, TypeArgs = typeArgs,
                    Ret = CopySourceType(result), Sty = CopySourceType(result),
                },
                BirCallInstance original => original with {
                    Recv = self, OwnerType = CopySourceType(resolvedOwner), Args = args, TypeArgs = typeArgs,
                    Sig = CopySourceTypes(declarationSignature), Ret = CopySourceType(result),
                    DynRet = CopySourceType(result), Sty = CopySourceType(result),
                },
                _ => throw new InvalidOperationException("Unsupported nested super call"),
            };
            if (targetCall is BirCallInstance instance && instance.MemberRef.HasValue)
            {
                var member = (BirMemberReference)instance.MemberRef.Value.DeepClone();
                if (member.DeclaringType.OrDefault() is TypeNode.Fqn selected)
                    member.DeclaringType = selected with { Args = resolvedOwner.Args is { Length: > 0 }
                        ? ((TypeNode.Fqn)ClrMemberResolution.PhysicalInstanceView(null, context.References, resolvedOwner)).Args : null };
                instance.MemberRef = member;
            }
            if (publishedIdentity == null) DropSourceMemberFacts((BirGraphValue)targetCall);
            if (localEntry != null) ((BirGraphValue)targetCall).MethodWitnessFacts = default;
            var forwarder = new BirFunctionDeclaration {
                Name = name, Generated = true, Static = false, Override = false, Virtual = false,
                Abstract = false, ObjectOverride = false, Vis = "private", Params = parameters,
                Ret = CopySourceType(result), Body = SourceReturningBody(targetCall, result), Attrs = Array.Empty<BirAttribute>(),
            };
            if (frame != null) forwarder.TypeParams = frame;
            if (localEntry != null) forwarder.LocalBodyEntry = (localEntry, null);
            if (publishedIdentity != null) forwarder.DeclarationId = publishedIdentity;
            if (suspends)
            {
                forwarder.Mods = new BirModifiers { Suspend = true };
                forwarder.SuspendRet = CopySourceType(result);
                forwarder.DeclarationSourceName = name;
            }
            host.Append(forwarder, context.Transitions);
            definition = new(name, null, CopySourceTypes(signature), 0, arity);
            context.Accessors.Add(key, definition);
        }
        var callResult = edge.Stamp ?? edge.DynamicReturn ?? edge.Return ?? result;
        var replacement = new BirCallInstance {
            OwnerType = CopySourceType(receiverType), Virtual = false, Recv = OptionalField<IBirExpression>.Of(edge.Receiver),
            Method = definition.EntryName, Sig = CopySourceTypes(definition.Signature), Args = edge.Arguments,
            Ret = CopySourceType(callResult), Sty = CopySourceType(callResult),
        };
        if (edge.TypeArgs.HasValue) replacement.TypeArgs = CopySourceTypes(edge.TypeArgs.Value);
        if (localEntry != null)
        {
            replacement.SelectedGenericCallable = localEntry;
            replacement.MethodWitnessFacts = node.MethodWitnessFacts;
        }
        if (publishedIdentity != null)
        {
            replacement.DeclarationId = publishedIdentity;
            if (suspends) replacement.SuspendCall = true;
            replacement.MemberVisibility = "private";
            replacement.MemberOwnerTypeParams = TypeParameterFrame.Declarations(host.Type).ToArray();
            if (frame != null) replacement.MemberMethodTypeParams = frame;
            replacement.MemberSignature = CopySourceTypes(signature);
            replacement.MemberReturnType = CopySourceType(result);
        }
        return replacement;
    }
}
