using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UnsafeAccessorLowering
{
    // A read of the actual call record, not a declaration/selection catalog.
    sealed record SourceMethodEdge(TypeNode Owner, string Method, string Visibility, string Identity,
        BirTypeParameter[] OwnerParameters, BirTypeParameter[] MethodParameters, TypeNode[] MemberSignature,
        TypeNode MemberReturn, TypeNode[] Signature, OptionalField<TypeNode[]> TypeArgs,
        IBirExpression Receiver, IBirExpression[] Arguments, TypeNode DynamicReturn, TypeNode Return, TypeNode Stamp,
        bool Static, bool Super, bool ExactBridge, string PropertyName, string PropertyKind, string Prop);
    static SourceMethodEdge SourceMethodEdgeOf(BirGraphValue node) => node switch {
        BirCallInstance c => new(c.OwnerType.OrDefault(), c.Method.OrDefault(), c.MemberVisibility.OrDefault(), c.DeclarationId.OrDefault(),
            c.MemberOwnerTypeParams.OrDefault(), c.MemberMethodTypeParams.OrDefault(), c.MemberSignature.OrDefault(), c.MemberReturnType.OrDefault(),
            c.Sig.OrDefault() ?? c.ShapeTypes.OrDefault() ?? c.ArgTypes.OrEmpty(), c.TypeArgs, c.Recv.OrDefault(), c.Args.OrEmpty(),
            c.DynRet.OrDefault(), c.Ret.OrDefault(), c.Sty.OrDefault(), false, c.Super.OrDefault(), c._exactExistentialBridgeOwnerCall.OrDefault(),
            c.PropertyName.OrDefault(), c.PropertyAccessor.OrDefault(), c.Prop.OrDefault()),
        BirCallStatic c => new(c.OwnerType.OrDefault() ?? c.Owner.OrDefault() ?? c.CalleeOwner.OrDefault(), c.Method.OrDefault(), c.MemberVisibility.OrDefault(), c.DeclarationId.OrDefault(),
            c.MemberOwnerTypeParams.OrDefault(), c.MemberMethodTypeParams.OrDefault(), c.MemberSignature.OrDefault(), c.MemberReturnType.OrDefault(),
            c.Sig.OrDefault() ?? c.ShapeTypes.OrDefault() ?? c.ArgTypes.OrEmpty(), c.TypeArgs, null, c.Args.OrEmpty(),
            c.DynRet.OrDefault(), c.Ret.OrDefault(), c.Sty.OrDefault(), true, false, false,
            c.PropertyName.OrDefault(), c.PropertyAccessor.OrDefault(), c.Prop.OrDefault()),
        BirConstrainedCall c => new(c.Iface.OrDefault(), c.Method.OrDefault(), c.MemberVisibility.OrDefault(), c.DeclarationId.OrDefault(),
            c.MemberOwnerTypeParams.OrDefault(), c.MemberMethodTypeParams.OrDefault(), c.MemberSignature.OrDefault(), c.MemberReturnType.OrDefault(),
            c.Sig.OrDefault() ?? c.ShapeTypes.OrDefault() ?? c.ArgTypes.OrEmpty(), c.TypeArgs, c.Recv.OrDefault(), c.Args.OrEmpty(),
            null, c.Ret.OrDefault(), c.Sty.OrDefault(), false, c.Super.OrDefault(), c._exactExistentialBridgeOwnerCall.OrDefault(),
            c.PropertyName.OrDefault(), c.PropertyAccessor.OrDefault(), c.Prop.OrDefault()),
        _ => throw new InvalidOperationException("not an UnsafeAccessor method edge"),
    };

    static BirGraphValue RewriteSourceMethod(BirGraphValue node, SourceHost caller, SourceAccessContext context,
        IReadOnlyDictionary<string, TypeNode> scope)
    {
        var edge = SourceMethodEdgeOf(node);
        // Exact carrier bridges already own their dispatch; visibility
        // rewriting must not turn them into ordinary accessor candidates.
        if (edge.ExactBridge) return node;
        DropSourceMemberFacts(node);
        // Selected native owner is authoritative even after a value-view cast.
        var selectedOwner = node switch {
            BirCallInstance c => c.MemberRef.OrDefault()?.DeclaringType.OrDefault(),
            BirCallStatic c => c.MemberRef.OrDefault()?.DeclaringType.OrDefault(),
            BirConstrainedCall c => c.MemberRef.OrDefault()?.DeclaringType.OrDefault(),
            _ => null,
        };
        if (selectedOwner != null)
        {
            if (node is BirCallInstance instance) instance.OwnerType = CopySourceType(selectedOwner);
            else if (node is BirCallStatic statik) statik.OwnerType = CopySourceType(selectedOwner);
            if (node is not BirConstrainedCall) edge = edge with { Owner = selectedOwner };
        }
        if (edge.Super)
        {
            // A lexical this keeps its normal base dispatch. A relocated
            // receiver needs the original host's nonvirtual forwarder instead.
            if (edge.Receiver is BirThis) return node;
            return RewriteSourceSuper(node, edge, caller, context);
        }
        if (edge.Owner is not TypeNode.Fqn owner || edge.Method == null) return node;
        KotlinPropertyAccessors.TryCallIdentity(edge.PropertyName, edge.PropertyKind, edge.Method, edge.Prop,
            out var propertyName, out var propertyKind);
        context.Hosts.TryGetValue(owner.Name, out var targetHost);
        if (targetHost != null && SourcePrivateAccess(caller.Name, targetHost.Name, context.Hosts)) return node;
        var signature = edge.Signature;
        var arity = edge.TypeArgs.OrEmpty().Length;
        var id = edge.Identity;
        BirFunctionDeclaration target = null;
        if (targetHost != null)
        {
            var candidates = targetHost.LookupMethods.Where(method => !KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
                && (id != null ? method.DeclarationId.OrDefault() == id
                    : (propertyKind != null ? KotlinPropertyAccessors.TryIdentity(method, out var candidateName, out var candidateKind)
                            && candidateName == propertyName && candidateKind == propertyKind
                        : method.Name.OrDefault() == edge.Method && !KotlinPropertyAccessors.TryIdentity(method, out _, out _))
                        && method.Static.OrDefault() == edge.Static && method.Params.OrEmpty().Length == signature.Length
                        && method.TypeParams.OrEmpty().Length == arity)).ToArray();
            if (id != null && candidates.Length == 0 && edge.Visibility is not ("private" or "protected" or "internal")) return node;
            if (id != null && candidates.Length != 1) throw new InvalidOperationException(
                $"UnsafeAccessor target '{owner.Name}.{edge.Method}' has no unique local MethodDef for declaration identity '{id}'");
            target = id != null ? candidates[0] : SelectSourceMethod(candidates, signature, owner.Args, m => m.Params.OrEmpty());
            if (id != null && (target.Static.OrDefault() != edge.Static || target.Params.OrEmpty().Length != signature.Length
                || target.TypeParams.OrEmpty().Length != arity))
                throw new InvalidOperationException($"UnsafeAccessor target declaration identity '{id}' disagrees with its call shape");
            if (target == null || target.Vis.OrDefault() is not ("private" or "protected")) return node;
        }
        else if (edge.Visibility is not ("private" or "protected" or "internal")) return node;
        var targetName = target == null ? edge.Method : target.Name.OrDefault()
            ?? throw new InvalidOperationException("private Kotlin accessor has no physical method name");
        id = target?.DeclarationId.OrDefault() ?? id;
        var referenced = SourceReferencedMethod(target, id, owner, targetName, arity, edge.Static,
            signature, edge.MemberReturn, edge.MethodParameters, propertyKind != null, context.References);
        if (referenced?.IsPublic == true) return node;
        if (referenced != null) targetName = referenced.PhysicalMember;
        var declaredReturn = referenced?.Return ?? target?.Ret.OrDefault() ?? edge.MemberReturn
            ?? edge.DynamicReturn ?? edge.Stamp ?? edge.Return ?? new TypeNode.Fqn("kotlin.Unit");
        var result = edge.DynamicReturn ?? edge.Stamp ?? edge.Return
            ?? (owner.Args is { Length: > 0 } ? SubstituteOwnerSlots(declaredReturn, owner.Args) : declaredReturn);
        if (referenced != null) signature = referenced.Parameters;
        else if (target?.Params.HasValue == true) signature = target.Params.Value.Select(p => p.Type.OrDefault()
            ?? throw new InvalidOperationException($"UnsafeAccessor target '{owner.Name}.{targetName}' has an untyped parameter")).ToArray();
        if (context.Allows(caller, edge.Receiver, owner, target?.Vis.OrDefault() ?? edge.Visibility,
            edge.Static, scope, signature.Append(declaredReturn))) return node;
        var key = $"{caller.Name}|method|{owner.Name}|{targetName}|{edge.Static}|{arity}|"
            + string.Join(";", signature.Select(SourceTypeKey)) + "|" + SourceTypeKey(declaredReturn) + "|declaration:" + id;
        var definition = EnsureSourceAccessor(caller, context, key, targetName, edge.Static ? 2 : 1, owner,
            SourceOwnerParameters(targetHost, edge.OwnerParameters, owner, context.References),
            referenced?.TypeParams ?? SourceMethodParameters(target, edge.MethodParameters), declaredReturn, signature, true,
            id, referenced?.NullableGenericReturn ?? target?.NullableGenericRet.OrDefault(), target != null || referenced != null);
        var arguments = new IBirExpression[edge.Arguments.Length + 1];
        arguments[0] = edge.Static ? new BirDefaultValue { Type = CopySourceType(owner) } : edge.Receiver;
        Array.Copy(edge.Arguments, 0, arguments, 1, edge.Arguments.Length);
        var replacement = SourceAccessorCall(caller, definition, owner, arguments, result);
        if (edge.TypeArgs.HasValue) replacement.TypeArgs = CopySourceTypes(edge.TypeArgs.Value);
        replacement.Sty = CopySourceType(result);
        return replacement;
    }

    static BirGraphValue RewriteSourceBound(BirNewBoundDelegate access, SourceHost caller,
        SourceAccessContext context, IReadOnlyDictionary<string, TypeNode> scope)
    {
        var visibility = access.MemberVisibility.OrDefault();
        var ownerParameters = access.MemberOwnerTypeParams.OrDefault();
        var methodParameters = access.MemberMethodTypeParams.OrDefault();
        var memberReturn = access.MemberReturnType.OrDefault();
        DropSourceMemberFacts(access);
        if (access.OwnerType.OrDefault() is not TypeNode.Fqn owner || !access.Method.HasValue) return access;
        context.Hosts.TryGetValue(owner.Name, out var host);
        if (host != null && SourcePrivateAccess(caller.Name, host.Name, context.Hosts)) return access;
        var name = access.Method.Value;
        var signature = access.Sig.OrDefault() ?? access.ArgTypes.OrEmpty();
        var arity = access.TypeArgs.OrEmpty().Length;
        BirFunctionDeclaration target = null;
        if (host != null)
        {
            var candidates = host.LookupMethods.Where(m => m.Name.OrDefault() == name && !m.Static.OrDefault()
                && m.Params.OrEmpty().Length == signature.Length && m.TypeParams.OrEmpty().Length == arity).ToArray();
            target = SelectSourceMethod(candidates, signature, owner.Args, m => m.Params.OrEmpty());
            if (target == null || target.Vis.OrDefault() is not ("private" or "protected")) return access;
        }
        else if (visibility is not ("private" or "protected" or "internal")) return access;
        var id = target?.DeclarationId.OrDefault() ?? access.DeclarationId.OrDefault();
        var referenced = SourceReferencedMethod(target, id, owner, name, arity, false, signature, memberReturn,
            methodParameters, false, context.References);
        if (referenced?.IsPublic == true) return access;
        if (referenced != null) { name = referenced.PhysicalMember; signature = referenced.Parameters; }
        var result = referenced?.Return ?? target?.Ret.OrDefault() ?? memberReturn
            ?? (access.FuncType.OrDefault() is TypeNode.Fn fn ? fn.Ret : new TypeNode.Fqn("kotlin.Unit"));
        if (context.Allows(caller, access.Recv.OrDefault(), owner, target?.Vis.OrDefault() ?? visibility,
            false, scope, signature.Append(result))) return access;
        var key = $"{caller.Name}|bound|{owner.Name}|{name}|{arity}|" + string.Join(";", signature.Select(SourceTypeKey)) + "|" + SourceTypeKey(result);
        var definition = EnsureSourceAccessor(caller, context, key, name, 1, owner,
            SourceOwnerParameters(host, ownerParameters, owner, context.References),
            referenced?.TypeParams ?? SourceMethodParameters(target, methodParameters), result, signature, true, id);
        var callOwner = SourceCallOwner(caller, definition, owner);
        var replacement = new BirNewBoundDelegate { OwnerType = CopySourceType(callOwner), CalleeOwner = CopySourceType(callOwner),
            Method = definition.EntryName, DeclarationId = definition.Identity, Sig = CopySourceTypes(definition.Signature),
            Virtual = false, Recv = access.Recv, FuncType = access.FuncType.HasValue ? CopySourceType(access.FuncType.Value) : OptionalField<TypeNode>.Null };
        if (access.TypeArgs.HasValue) replacement.TypeArgs = CopySourceTypes(access.TypeArgs.Value);
        return replacement;
    }

    static BirGraphValue RewriteSourceConstructor(BirNew access, SourceHost caller, SourceAccessContext context)
    {
        var visibility = access.MemberVisibility.OrDefault();
        var ownerParameters = access.MemberOwnerTypeParams.OrDefault();
        var signature = access.MemberSignature.OrDefault() ?? access.ArgTypes.OrEmpty();
        // Constructor selected signatures remain live for the later constructor binder.
        access.MemberVisibility = default; access.MemberOwnerTypeParams = default; access.MemberMethodTypeParams = default;
        if (access.Type.OrDefault() is not TypeNode.Fqn owner) return access;
        context.Hosts.TryGetValue(owner.Name, out var host);
        if (host != null && SourcePrivateAccess(caller.Name, host.Name, context.Hosts)) return access;
        var id = access.ConstructorDeclarationId.OrDefault();
        BirConstructor target = null;
        if (host != null)
        {
            var candidates = host.LookupConstructors.Where(c => c.Vis.OrDefault() is "private" or "protected"
                && c.Params.OrEmpty().Length == signature.Length).ToArray();
            target = id != null ? candidates.SingleOrDefault(c => c.DeclarationId.OrDefault() == id)
                : SelectSourceMethod(candidates, signature, owner.Args, c => c.Params.OrEmpty());
            if (target == null) return access;
        }
        else if (visibility is not ("private" or "protected" or "internal")) return access;
        id = target?.DeclarationId.OrDefault() ?? id;
        if (target == null && id != null)
        {
            var referenced = context.References.UnsafeAccessorConstructor(owner, id);
            if (referenced.IsPublic) return access;
            signature = referenced.GetParameters().Select(context.References.DeclarationSignatureSlot).ToArray();
        }
        var frame = SourceOwnerParameters(host, ownerParameters, owner, context.References);
        var key = $"{caller.Name}|ctor|{owner.Name}|" + string.Join(";", signature.Select(SourceTypeKey)) + "|declaration:" + id;
        var definition = EnsureSourceAccessor(caller, context, key, null, 0, owner, frame, null,
            OpenSourceOwner(owner, frame?.Length ?? 0), signature, false, id);
        return SourceAccessorCall(caller, definition, owner, access.Args.OrEmpty(), owner);
    }
}
