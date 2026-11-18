using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UnsafeAccessorLowering
{
    static SourceHost SourceEnclosingHost(SourceHost caller, string name, IReadOnlyDictionary<string, SourceHost> hosts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (caller != null && seen.Add(caller.Name))
        {
            if (caller.Name == name) return caller;
            caller = caller.NestedIn != null && hosts.TryGetValue(caller.NestedIn, out var parent) ? parent : null;
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
        if (node is not BirCallInstance original || edge.Receiver is not BirGraphNode receiver
            || BirNodeType.Of(receiver) is not TypeNode.Fqn receiverType || edge.Owner is not TypeNode.Fqn targetOwner || edge.Method == null)
            throw new InvalidOperationException("Nested Kotlin super call is missing its receiver/target facts");
        var host = SourceEnclosingHost(caller, receiverType.Name, context.Hosts)
            ?? throw new InvalidOperationException($"Nested Kotlin super receiver '{receiverType.Name}' is not an enclosing physical owner of '{caller.Name}'");
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
        var key = $"{host.Name}|super|{targetOwner.Name}|{edge.Method}|{arity}|" + string.Join(";", signature.Select(SourceTypeKey)) + "|" + SourceTypeKey(result);
        if (!context.Accessors.TryGetValue(key, out var definition))
        {
            var name = SuperForwarderName(edge.Method);
            var parameters = signature.Select((type, index) => SourceParameter("arg" + index, type)).ToArray();
            // Copy only the call header. Original executable operands move to
            // the continuing outer call; the forwarder gets fresh this/locals.
            var targetCall = original with {
                Recv = OptionalField<IBirExpression>.Of(new BirThis()), OwnerType = CopySourceType(resolvedOwner),
                Sig = CopySourceTypes(declarationSignature), Args = parameters.Select(p => (IBirExpression)SourceLocal(p.Name.Value)).ToArray(),
                Ret = CopySourceType(result), DynRet = CopySourceType(result), Sty = CopySourceType(result),
                TypeArgs = arity == 0 ? default : OptionalField<TypeNode[]>.Of(Enumerable.Range(0, arity)
                    .Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray()),
            };
            if (original.MemberRef.HasValue)
            {
                var member = (BirMemberReference)original.MemberRef.Value.DeepClone();
                if (member.DeclaringType.OrDefault() is TypeNode.Fqn selected)
                    member.DeclaringType = selected with { Args = resolvedOwner.Args is { Length: > 0 }
                        ? ((TypeNode.Fqn)ClrMemberResolution.PhysicalInstanceView(null, context.References, resolvedOwner)).Args : null };
                targetCall.MemberRef = member;
            }
            DropSourceMemberFacts(targetCall);
            var forwarder = new BirFunctionDeclaration {
                Name = name, Generated = true, Static = false, Override = false, Virtual = false,
                Abstract = false, ObjectOverride = false, Vis = "private", Params = parameters,
                Ret = CopySourceType(result), Body = SourceReturningBody(targetCall, result), Attrs = Array.Empty<BirAttribute>(),
            };
            if (frame != null) forwarder.TypeParams = frame;
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
        return replacement;
    }
}
