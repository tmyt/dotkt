using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UnsafeAccessorLowering
{
    static BirParameter SourceParameter(string name, TypeNode type) => new() { Name = name, Type = CopySourceType(type) ?? new TypeNode.Fqn("kotlin.Any") };
    static BirLocal SourceLocal(string name) => new() { Name = name };
    static BirTypeParameter[] CopySourceParameters(BirTypeParameter[] parameters, string prefix = null,
        TypeNode[] ownerArgs = null)
    {
        if (parameters == null) return null;
        return parameters.Select((parameter, index) => {
            var copy = (BirTypeParameter)parameter.DeepClone();
            if (prefix != null) copy.Name = prefix + index;
            if (ownerArgs != null) copy.RewriteTypeFieldsInPlace(type => SubstituteOwnerSlots(type, ownerArgs));
            return copy;
        }).ToArray();
    }
    static IBirStatement[] SourceReturningBody(IBirExpression expression, TypeNode type)
        => type is TypeNode.Fqn { Name: "kotlin.Unit" or "void" }
            ? new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(expression) }, new BirReturn() }
            : new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(expression) } };

    static BirFunctionDeclaration SourceAccessorDeclaration(string name, TypeNode result,
        BirParameter[] parameters, int kind, string target)
    {
        var attribute = new BirAttribute {
            Attr = new TypeNode.Fqn(AttributeFqn), AttrExternal = true, AttrAssembly = "System.Runtime",
            ArgTypes = new TypeNode[] { new TypeNode.Fqn(KindFqn) },
            Args = new IBirAttributeValue[] { new BirAttributeLiteral { Type = new TypeNode.Fqn(KindFqn),
                Value = new BirLiteral.IntegerLiteral(kind) } },
        };
        if (target != null) attribute.NamedArgs = new[] { new BirAttributeNamedArgument {
            Kind = "property", Name = "Name", Type = new TypeNode.Fqn("System.String"),
            Value = OptionalField<IBirAttributeValue>.Of(new BirAttributeLiteral {
                Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(target) }),
        } };
        return new() { Name = name, Generated = true, Static = true, Extern = true,
            Override = false, Virtual = false, Abstract = false, ObjectOverride = false, Vis = "private",
            Params = parameters, Ret = CopySourceType(result) ?? new TypeNode.Fqn("kotlin.Unit"), Attrs = new[] { attribute } };
    }
    static void StampSourceNullableOwnership(BirFunctionDeclaration method, TypeNode nullableReturn, bool known)
    {
        if (nullableReturn != null) method.NullableGenericRet = CopySourceType(nullableReturn);
        if (known) method.NullableErasureOwnershipKnown = true;
    }
    static SourceAccessor EnsureSourceAccessor(SourceHost caller, SourceAccessContext context, string key,
        string targetName, int kind, TypeNode.Fqn owner, BirTypeParameter[] ownerParameters,
        BirTypeParameter[] methodParameters, TypeNode result, TypeNode[] signature, bool includeTarget,
        string targetDeclarationId = null, TypeNode nullableReturn = null, bool ownershipKnown = false)
    {
        if (context.Accessors.TryGetValue(key, out var existing)) return existing;
        var ownerCount = ownerParameters?.Length ?? 0;
        if (ownerCount != (owner.Args?.Length ?? 0))
            throw new InvalidOperationException($"UnsafeAccessor owner generic frame does not match its construction: {owner.Name} has " +
                $"{ownerCount} declaration parameters and {owner.Args?.Length ?? 0} constructed arguments ({key})");
        var parameters = new List<BirParameter>();
        if (includeTarget) parameters.Add(SourceParameter("target", OpenSourceOwner(owner, ownerCount)));
        parameters.AddRange(signature.Select((type, index) => SourceParameter("arg" + index, type)));
        var methodFrame = CopySourceParameters(methodParameters, "__method");
        var accessorName = AccessorName(targetName);
        var identity = DeclarationIdentityBinding.PhysicalOnlyId(caller.Name, "unsafe-accessor:" + accessorName);
        var accessor = SourceAccessorDeclaration(accessorName, result, parameters.ToArray(), kind, targetName);
        accessor.DeclarationId = identity;
        if (kind is 1 or 2 && methodFrame is { Length: > 0 } && targetDeclarationId != null)
            accessor.ProjectedAccessBinding = (identity, targetDeclarationId);
        if (targetDeclarationId != null)
        {
            if (kind == 0) accessor.UnsafeSignatureTargetDeclarationId = targetDeclarationId;
            else accessor.UnsafeTargetDeclarationId = targetDeclarationId;
        }
        if (methodFrame != null) accessor.TypeParams = CopySourceParameters(methodFrame);
        var declarationSignature = parameters.Select(p => CopySourceType(p.Type.Value)).ToArray();
        SourceAccessor definition;
        if (ownerCount == 0)
        {
            StampSourceNullableOwnership(accessor, nullableReturn, ownershipKnown);
            // A generic method accessor matches its target's declaring frame,
            // not the generic class where the Kotlin access happened.
            if (accessor.ProjectedAccessBinding != null && caller.Type != null && TypeParameterFrame.Count(caller.Type) != 0)
            {
                var holderName = "dotkt$unsafe$holder$" + System.Threading.Interlocked.Increment(ref _counter);
                accessor.Vis = "internal";
                var holder = new BirClassDefinition { Name = holderName, Kind = "class", Generated = true,
                    Vis = "internal", Methods = new[] { accessor } };
                caller.File.Types = caller.File.Types.OrEmpty().Append(holder).ToArray();
                context.Transitions.PublishTypes(caller.File, new IBirTypeDeclaration[] { holder });
                definition = new(accessorName, holderName, declarationSignature, 0, methodFrame.Length, identity);
            }
            else
            {
                caller.Append(accessor, context.Transitions);
                definition = new(accessorName, null, declarationSignature, 0, methodFrame?.Length ?? 0, identity);
            }
        }
        else
        {
            // TYPE variables stay on a physical holder, never on the accessor
            // method: UnsafeAccessor matches the exact target declaration frame.
            var holderName = "dotkt$unsafe$holder$" + System.Threading.Interlocked.Increment(ref _counter);
            var holderFrame = CopySourceParameters(ownerParameters, "__owner");
            var entryName = accessorName + "$invoke";
            var wrapperIdentity = DeclarationIdentityBinding.PhysicalOnlyId(caller.Name, "unsafe-wrapper:" + accessorName);
            var holderSelf = (TypeNode.Fqn)OpenSourceOwner(new TypeNode.Fqn(holderName), ownerCount);
            var call = new BirCallStatic {
                Owner = CopySourceType(holderSelf), OwnerType = CopySourceType(holderSelf), Method = accessorName,
                DeclarationId = identity, Sig = CopySourceTypes(declarationSignature),
                Args = parameters.Select(p => (IBirExpression)SourceLocal(p.Name.Value)).ToArray(), Ret = CopySourceType(result),
            };
            if (methodFrame is { Length: > 0 }) call.TypeArgs = Enumerable.Range(0, methodFrame.Length)
                .Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
            var wrapper = new BirFunctionDeclaration {
                Name = entryName, Generated = true, Static = true, Override = false, Virtual = false,
                Abstract = false, ObjectOverride = false, Vis = "internal", DeclarationId = wrapperIdentity,
                Params = parameters.Select(p => (BirParameter)p.DeepClone()).ToArray(), Ret = CopySourceType(result),
                Body = SourceReturningBody(call, result), Attrs = Array.Empty<BirAttribute>(),
            };
            if (targetDeclarationId != null) wrapper.UnsafeSignatureTargetDeclarationId = targetDeclarationId;
            if (accessor.ProjectedAccessBinding != null) wrapper.ProjectedAccessBinding = (wrapperIdentity, targetDeclarationId);
            if (methodFrame != null) wrapper.TypeParams = CopySourceParameters(methodFrame);
            StampSourceNullableOwnership(wrapper, nullableReturn, ownershipKnown);
            var holder = new BirClassDefinition {
                Name = holderName, Kind = "class", Generated = true, Vis = "internal", TypeParams = holderFrame,
                Base = OptionalField<TypeNode>.Null, Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<BirFieldDeclaration>(),
                Ctors = Array.Empty<BirConstructor>(), Methods = new[] { accessor, wrapper },
                Properties = Array.Empty<BirPropertyDeclaration>(), Attrs = Array.Empty<BirAttribute>(),
            };
            caller.File.Types = caller.File.Types.OrEmpty().Append(holder).ToArray();
            context.Transitions.PublishTypes(caller.File, new IBirTypeDeclaration[] { holder });
            definition = new(entryName, holderName, declarationSignature, ownerCount, methodFrame?.Length ?? 0, wrapperIdentity);
        }
        context.Accessors.Add(key, definition);
        return definition;
    }

    static BirCallStatic SourceAccessorCall(SourceHost caller, SourceAccessor definition, TypeNode.Fqn target,
        IBirExpression[] arguments, TypeNode result)
    {
        var owner = SourceCallOwner(caller, definition, target);
        return new() { Owner = CopySourceType(owner), OwnerType = CopySourceType(owner), Method = definition.EntryName,
            DeclarationId = definition.Identity, Sig = CopySourceTypes(definition.Signature), Args = arguments,
            Ret = CopySourceType(result) };
    }
}
