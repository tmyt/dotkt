using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationIdentityBinding
{
    internal static void ApplyLocal(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlyDictionary<string, string> physicalById, IReadOnlySet<string> semanticCarrierIds,
        SourceDeclarationSignatures semanticSignatures, IReadOnlyDictionary<string, int[]> physicalParameterIndices,
        ReferenceMetadataIndex refs,
        Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration, string> onConsumedIdentity)
    {
        ArgumentNullException.ThrowIfNull(onConsumedIdentity);
        var declarations = new List<(BirFileDeclaration File, IBirTypeDeclaration Owner, BirFunctionDeclaration Method)>();
        foreach (var file in roots)
        {
            foreach (var method in file.Methods.OrEmpty())
                if (method.DeclarationId.HasValue) declarations.Add((file, null, method));
            foreach (var owner in file.Types.OrEmpty())
                foreach (var method in owner.Methods.OrEmpty())
                    if (method.DeclarationId.HasValue) declarations.Add((file, owner, method));
        }
        // Preserve the old strict unique current-ID requirement before any ID
        // is consumed. Generated physical artifacts stay selected by this exact
        // original key during the remainder of this operation.
        var declarationsById = declarations.ToDictionary(row => row.Method.DeclarationId.Value,
            row => row.Method, StringComparer.Ordinal);
        foreach (var (file, owner, method) in declarations)
        {
            var id = method.DeclarationId.Value;
            if (!physicalById.TryGetValue(id, out var physical))
                throw new InvalidOperationException($"bir2cir: declaration identity '{id}' is absent from the CLR collision projection");
            method.Name = physical;
            if (method.ExtensionCoreDeclarationId.OrDefault() is string coreId)
            {
                if (!physicalById.TryGetValue(coreId, out var corePhysical))
                    throw new InvalidOperationException($"bir2cir: companion-extension wrapper core identity '{coreId}' has no physical name");
                RoundtripMetadata.StampExtensionCore(method, corePhysical);
                method.ExtensionCoreDeclarationId = default;
            }
            if (id.Contains(PhysicalOnlySuffix, StringComparison.Ordinal))
            {
                method.NullableRepresentationFrame = default;
                method.MethodTypeParameterBoundsPre = default;
                method.DeclarationSemanticSignature = default;
                if (!id.Contains(ExistentialSlotMarker, StringComparison.Ordinal)
                    && !id.EndsWith(PhysicalOnlySuffix + "|" + ProjectedConstructorRole, StringComparison.Ordinal)
                    && !id.EndsWith(PhysicalOnlySuffix + "|" + ProjectedInnerConstructorRole, StringComparison.Ordinal))
                {
                    // Report the actual attached occurrence, with its original
                    // ID still present. This is consumption, not retirement.
                    onConsumedIdentity(file, owner, method, id);
                    method.DeclarationId = default;
                    method.DeclarationSourceName = default;
                }
                continue;
            }
            if (id.EndsWith("|cold", StringComparison.Ordinal)
                || id.EndsWith(AliasHelperHoist.IdentitySuffix, StringComparison.Ordinal))
            {
                method.DeclarationSemanticSignature = default;
                continue;
            }
            if (semanticCarrierIds.Contains(id) || method.SemanticReifiedIndices.HasValue
                || method.NullableWitnessIndices.HasValue || method.NullableRepresentationFrame.HasValue)
                method.DeclarationSemanticSignature = semanticSignatures.TryGetValue(id, out var signature)
                    ? signature.Rewrite(type => type)
                    : throw new InvalidOperationException($"declaration identity '{id}' has no preserved semantic signature");
            else method.DeclarationSemanticSignature = default;
        }

        (string Name, TypeNode Return, TypeNode[] Parameters) SelectCall(string id, OptionalField<TypeNode[]> signature)
        {
            var selected = declarationsById[id];
            var result = selected.Ret.OrDefault()
                ?? throw new InvalidOperationException("Local declaration has no physical return signature");
            TypeNode[] parameters = null;
            if (selected.Params.HasValue && selected.Params.Value.Any(parameter =>
                    parameter.FunctionSignatureType.HasValue || parameter.Type.OrDefault() is TypeNode.Mod))
                parameters = selected.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray();
            if (physicalParameterIndices.TryGetValue(id, out var indices))
            {
                if (!signature.HasValue || !selected.Params.HasValue)
                    throw new InvalidOperationException($"bir2cir: declaration identity '{id}' has no complete local call signature");
                RewritePhysicalParameterSlots(id, signature.Value, selected.Params.Value, indices);
            }
            return (physicalById[id], BirGraphValue.CopyType(result), parameters);
        }
        TypeNode[] EventSignature(string id) => declarationsById[id].Params.HasValue
            ? declarationsById[id].Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray()
            : throw new InvalidOperationException("Local event accessor has no physical parameter signature");

        void Rewrite(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitChildSlots(slot => Rewrite(slot.Value));
            if (node is BirFunctionDeclaration method && method.UnsafeTargetDeclarationId.OrDefault() is string targetId)
            {
                if (!physicalById.TryGetValue(targetId, out var targetPhysical)
                    && !refs.TryDeclarationIdentity(targetId, out targetPhysical, out _, out _, out _))
                    throw new InvalidOperationException($"bir2cir: unresolved UnsafeAccessor target declaration identity '{targetId}'");
                RewriteUnsafeAccessorTarget(method, targetPhysical);
                method.UnsafeTargetDeclarationId = default;
            }
            if (node.DeclarationIdentityFact.OrDefault() is not string id || !physicalById.ContainsKey(id)) return;
            switch (node)
            {
                case BirCallStatic call:
                {
                    var selected = SelectCall(id, call.Sig);
                    call.Method = selected.Name; call.CalleeRet = selected.Return;
                    if (selected.Parameters != null) call.CalleeParams = selected.Parameters;
                    call.Prop = default; call.DeclarationId = default; break;
                }
                case BirCallInstance call:
                {
                    var selected = SelectCall(id, call.Sig);
                    call.Method = selected.Name; call.CalleeRet = selected.Return;
                    if (selected.Parameters != null) call.CalleeParams = selected.Parameters;
                    call.Prop = default; call.DeclarationId = default; break;
                }
                case BirConstrainedCall call:
                {
                    var selected = SelectCall(id, call.Sig);
                    call.Method = selected.Name; call.CalleeRet = selected.Return;
                    if (selected.Parameters != null) call.CalleeParams = selected.Parameters;
                    call.Prop = default; call.DeclarationId = default; break;
                }
                case BirNewDelegate call:
                {
                    var selected = SelectCall(id, call.Sig);
                    call.Method = selected.Name; call.CalleeRet = selected.Return;
                    if (selected.Parameters != null) call.CalleeParams = selected.Parameters;
                    call.DeclarationId = default; break;
                }
                case BirNewBoundDelegate call:
                {
                    var selected = SelectCall(id, call.Sig);
                    call.Method = selected.Name; call.CalleeRet = selected.Return;
                    if (selected.Parameters != null) call.CalleeParams = selected.Parameters;
                    call.DeclarationId = default; break;
                }
                case BirEventAdd call:
                    call.Accessor = physicalById[id]; call.Sig = EventSignature(id); call.DeclarationId = default; break;
                case BirEventRemove call:
                    call.Accessor = physicalById[id]; call.Sig = EventSignature(id); call.DeclarationId = default; break;
            }
        }
        foreach (var root in roots) Rewrite(root);
        void RejectUnboundUse(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirCallStatic or BirCallInstance or BirConstrainedCall or BirNewDelegate
                    or BirNewBoundDelegate or BirEventAdd or BirEventRemove
                && node.DeclarationIdentityFact.OrDefault() is string id)
                throw new InvalidOperationException($"bir2cir: unresolved frontend declaration identity '{id}'");
            node.VisitChildSlots(slot => RejectUnboundUse(slot.Value));
        }
        foreach (var root in roots) RejectUnboundUse(root);
    }

    static void RewritePhysicalParameterSlots(string declarationId, TypeNode[] callSignature,
        BirParameter[] declarationParameters, IEnumerable<int> rewrittenIndices)
    {
        foreach (var index in rewrittenIndices)
        {
            if (index < 0 || index >= callSignature.Length || index >= declarationParameters.Length
                || !declarationParameters[index].Type.HasValue)
                throw new InvalidOperationException($"bir2cir: declaration identity '{declarationId}' has no physical parameter {index}");
            callSignature[index] = BirGraphValue.CopyType(declarationParameters[index].Type.Value);
        }
    }

    static void RewriteUnsafeAccessorTarget(BirFunctionDeclaration method, string physicalName)
    {
        if (!method.Attrs.HasValue)
            throw new InvalidOperationException("bir2cir: declaration-bound UnsafeAccessor has no attribute list");
        foreach (var attribute in method.Attrs.Value)
        {
            if (attribute.Attr.OrDefault() is not TypeNode.Fqn {
                    Name: "System.Runtime.CompilerServices.UnsafeAccessorAttribute" }) continue;
            foreach (var argument in attribute.NamedArgs.OrEmpty())
                if (argument.Name.OrDefault() == "Name")
                    switch (argument.Value.OrDefault())
                    {
                        case BirAttributeLiteral value: value.Value = new BirLiteral.TextLiteral(physicalName); return;
                        case BirConstant value: value.Value = new BirLiteral.TextLiteral(physicalName); return;
                    }
        }
        throw new InvalidOperationException("bir2cir: declaration-bound UnsafeAccessor has no Name argument");
    }
}
