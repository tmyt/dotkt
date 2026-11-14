using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UncheckedGenericCastReturnErasure
{
    internal static void Apply(BirFileDeclaration root, PendingSourceIndex pending)
    {
        var index = pending.SourceDeclarations;
        RetypeProjectedSourceBodies(root);
        var owned = root.Methods.OrEmpty().Concat(root.Types.OrEmpty().SelectMany(type => type.Methods.OrEmpty()))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var declaration in index.ByIdentity.Values)
        {
            var method = declaration.SourceMethod;
            if (method == null || !owned.Contains(method) || method.Ret.OrDefault() is not TypeNode.Tv) continue;
            if (!method.RetKotlinType.HasValue)
                method.RetKotlinType = BirGraphValue.CopyType(method.NullableGenericRet.OrDefault() ?? declaration.Return);
            pending.EraseOwnedReturn(declaration);
            foreach (var statement in method.Body.OrEmpty()) RetypeSourceReturnedCasts((BirGraphValue)statement, declaration.Return);
        }
        foreach (var method in root.Methods.OrEmpty()) RewriteSourceMethod(method.Body.OrEmpty().Cast<BirGraphValue>(), index);
        foreach (var type in root.Types.OrEmpty())
        {
            foreach (var method in type.Methods.OrEmpty()) RewriteSourceMethod(method.Body.OrEmpty().Cast<BirGraphValue>(), index);
            foreach (var constructor in type.Ctors.OrEmpty())
            {
                var regions = constructor.PreStmts.OrEmpty().Cast<BirGraphValue>()
                    .Concat((constructor.ThisArgs.HasValue ? constructor.ThisArgs.Value : constructor.BaseArgs.OrEmpty()).Cast<BirGraphValue>())
                    .Concat(constructor.Body.OrEmpty().Cast<BirGraphValue>());
                RewriteSourceMethod(regions, index);
            }
            foreach (var field in type.Fields.OrEmpty())
                if (field.Init.HasValue) RewriteSourceMethod(new[] { (BirGraphValue)field.Init.Value }, index);
        }
    }

    static void RetypeProjectedSourceBodies(BirFileDeclaration root)
    {
        void Methods(IEnumerable<BirFunctionDeclaration> methods, NullableRepresentationFrame ownerFrame)
        {
            foreach (var method in methods)
            {
                if (!IsObject(method.Ret.OrDefault()) || method.RetKotlinType.OrDefault() is not TypeNode.Tv source) continue;
                var frame = source.Scope == "method" ? method.NullableRepresentationFrame.OrDefault() : ownerFrame;
                var physical = frame == null ? source : frame.Variable(source,
                    frame.StorageIndices.Contains(source.I) ? NullableRepresentationFrame.Role.Storage : NullableRepresentationFrame.Role.Ordinary);
                foreach (var statement in method.Body.OrEmpty()) RetypeSourceReturnedCasts((BirGraphValue)statement, physical);
            }
        }
        Methods(root.Methods.OrEmpty(), null);
        foreach (var owner in root.Types.OrEmpty())
            Methods(owner.Methods.OrEmpty(), NullableRepresentationViews.TypeFacts((BirGraphValue)owner)?.NullableFrame);
    }

    static void VisitSourceUseChildren(BirGraphValue node, Action<BirGraphValue> visit)
    {
        if (node is IBirExecutablePayload) return;
        node.VisitGenericFrame((_, _) => { }, (role, child) => {
            if (!NullableRepresentationViews.Frozen(role)
                && role is not (BirGenericFrameRole.ReferencedDeclaration or BirGenericFrameRole.ResolvedDeclaration
                    or BirGenericFrameRole.OverrideDeclaration)) visit(child);
        });
    }

    static void RewriteSourceMethod(IEnumerable<BirGraphValue> regions, DeclIndex index)
    {
        var body = regions.ToArray();
        var deferred = new Dictionary<string, TypeNode.Tv>(StringComparer.Ordinal);
        foreach (var node in body) RewriteSourceDeferredLocals(node, index, deferred);
        if (deferred.Count != 0) foreach (var node in body) RenarrowSourceTypedUses(node, deferred);
    }

    static void RewriteSourceDeferredLocals(BirGraphValue node, DeclIndex index, Dictionary<string, TypeNode.Tv> deferred)
    {
        if (node is BirVariable { Name.HasValue: true, Type.HasValue: true, Type.Value: TypeNode.Tv variable,
                Init.HasValue: true } local && local.Init.Value is BirGraphValue call
            && TryResolveSource(call, index, out var logical) && logical is TypeNode.Tv result && result == variable)
        {
            local.Type = ObjectReturn;
            WriteSourceReturn(call, ObjectReturn);
            StampSourceResult(call, ObjectReturn, dynamic: true);
            deferred[local.Name.Value] = variable;
        }
        if (SourceOwnType(node, "ret") == null && SourceOwnType(node, "sty") is { } expected
            && !IsObject(expected) && !IsUnit(expected)
            && TryResolveSource(node, index, out var projected) && projected.Equals(expected))
            WriteSourceReturn(node, BirGraphValue.CopyType(expected));
        VisitSourceUseChildren(node, child => RewriteSourceDeferredLocals(child, index, deferred));
    }

    static TypeNode SourceOwnType(BirGraphValue node, string key)
    {
        TypeNode result = null;
        node.VisitOwnTypeFields((field, type) => { if (field == key) result = type; });
        return result;
    }

    static void WriteSourceReturn(BirGraphValue node, TypeNode result)
    {
        switch (node)
        {
            case BirCallStatic call: call.Ret = result; break;
            case BirCallInstance call: call.Ret = result; break;
            default: throw new InvalidOperationException("Unchecked return requires its selected Kotlin call");
        }
    }

    static void StampSourceResult(BirGraphValue node, TypeNode result, bool dynamic)
        => node.RewriteOwnTypeFieldsInPlace((field, _, current) =>
            field == "sty" || dynamic && field == "dynRet" ? BirGraphValue.CopyType(result) : current);

    static bool TryResolveSource(BirGraphValue node, DeclIndex index, out TypeNode result)
    {
        var (kind, identity, ownerType, owners, arguments) = node switch {
            BirCallStatic call => (call.Tag, call.DeclarationId.OrDefault(), call.OwnerType.OrDefault(),
                new[] { call.Owner.OrDefault(), call.CalleeOwner.OrDefault(), call.OwnerType.OrDefault() }.Where(type => type != null).ToArray(),
                call.TypeArgs.OrEmpty()),
            BirCallInstance call => (call.Tag, call.DeclarationId.OrDefault(), call.OwnerType.OrDefault(), Array.Empty<TypeNode>(), call.TypeArgs.OrEmpty()),
            _ => ((string)null, null, (TypeNode)null, Array.Empty<TypeNode>(), Array.Empty<TypeNode>()),
        };
        return TryResolve(kind, identity, ownerType, owners, arguments, index, out result);
    }

    static void RenarrowSourceTypedUses(BirGraphValue node, IReadOnlyDictionary<string, TypeNode.Tv> deferred)
    {
        var ownerArguments = (SourceOwnType(node, "ownerType") as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>();
        var methodArguments = node.ApplicationTypeArguments.OrEmpty();
        var signature = new List<TypeNode>();
        var argumentTypes = new List<TypeNode>();
        node.VisitOwnTypeFields((field, type) => { if (field == "sig") signature.Add(type); else if (field == "argTypes") argumentTypes.Add(type); });
        if (signature.Count == 0) signature = argumentTypes;
        var arguments = new List<BirGraphSlot>();
        BirGraphSlot? receiver = null;
        node.VisitOwnChildSlots((field, _, slot) => { if (field == "args") arguments.Add(slot); else if (field == "recv") receiver = slot; });
        if (signature.Count == arguments.Count)
            for (var index = 0; index < arguments.Count; index++)
                if (arguments[index].Value is BirLocal { Name.HasValue: true } local
                    && deferred.TryGetValue(local.Name.Value, out var logical)
                    && Substitute(signature[index], ownerArguments, methodArguments).Equals(logical))
                    arguments[index].Replace(new BirCast { Type = logical, E = local });
        if (receiver is { } receiverSlot && receiverSlot.Value is BirLocal { Name.HasValue: true } selected
            && deferred.TryGetValue(selected.Name.Value, out var receiverLogical)
            && SourceOwnType(node, "ownerType") is TypeNode.Tv required && required == receiverLogical)
            receiverSlot.Replace(new BirCast { Type = receiverLogical, E = selected });
        VisitSourceUseChildren(node, child => RenarrowSourceTypedUses(child, deferred));
    }

    static BirGraphValue ReturnedSourceValue(BirReturn statement)
        => ReturnedValue("return", statement.Value.OrDefault() as BirGraphValue,
            value => (value as BirGraphNode)?.Tag, value => value is BirCast cast ? cast.Type.OrDefault() : null,
            value => value is BirCast cast ? cast.E.OrDefault() as BirGraphValue : null,
            value => value is BirValueBlock block ? block.Result.OrDefault() as BirGraphValue : null);

    static void RetypeSourceReturnedCasts(BirGraphValue node, TypeNode.Tv result)
    {
        if (node is BirReturn statement && ReturnedSourceValue(statement) is BirCast cast
            && cast.E.OrDefault() is BirGraphValue source
            && IsUncheckedReturnCast(cast.Tag, cast.ReifiedTypeOperand.OrDefault(), cast.Type.OrDefault(), result, source.ExplicitSourceType))
            cast.Type = ObjectReturn;
        VisitSourceUseChildren(node, child => RetypeSourceReturnedCasts(child, result));
    }

    static readonly TypeNode ObjectReturn = new TypeNode.Fqn("object");
}
