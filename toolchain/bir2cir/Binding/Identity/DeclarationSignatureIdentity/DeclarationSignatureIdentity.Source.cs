using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationSignatureIdentity
{
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, SignatureAllocationPlan plan,
        Action<BirFileDeclaration, IReadOnlyList<IBirTypeDeclaration>> retireTypes)
    {
        ArgumentNullException.ThrowIfNull(retireTypes);
        void Parameters(BirParameter[] parameters, TypeNode[] selected)
        {
            if (parameters.Length != selected.Length)
                throw new InvalidOperationException("Generic signature allocation changed parameter arity");
            for (var index = 0; index < parameters.Length; index++)
                parameters[index].Type = BirGraphValue.CopyType(WithIdentityModifiers(selected[index], parameters[index].Type.OrDefault()));
        }
        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (node is BirFunctionDeclaration accessor
                && (accessor.UnsafeTargetDeclarationId.OrDefault() ?? accessor.UnsafeSignatureTargetDeclarationId.OrDefault()) is string targetId
                && plan.Parameters.TryGetValue(targetId, out var targetTypes) && accessor.Params.HasValue)
            {
                var offset = accessor.Params.Value.Length - targetTypes.Length;
                if (offset is < 0 or > 1)
                    throw new InvalidOperationException("UnsafeAccessor signature differs from its selected declaration");
                for (var index = 0; index < targetTypes.Length; index++)
                {
                    var parameter = accessor.Params.Value[index + offset];
                    parameter.Type = BirGraphValue.CopyType(WithIdentityModifiers(targetTypes[index], parameter.Type.OrDefault()));
                }
            }
            if (node is BirFunctionDeclaration method) method.UnsafeSignatureTargetDeclarationId = default;
            if (node.DeclarationIdentityFact.OrDefault() is string id && plan.Parameters.TryGetValue(id, out var types))
                switch (node)
                {
                    case BirFunctionDeclaration declaration when declaration.Params.HasValue:
                        declaration.DeclarationSignatureAllocation = true; Parameters(declaration.Params.Value, types); break;
                    case BirConstructor declaration when declaration.Params.HasValue:
                        declaration.DeclarationSignatureAllocation = true; Parameters(declaration.Params.Value, types); break;
                    case BirCallStatic call: call.CalleeParams = CopyTypes(types); break;
                    case BirCallInstance call: call.CalleeParams = CopyTypes(types); break;
                    case BirConstrainedCall call: call.CalleeParams = CopyTypes(types); break;
                    case BirNewDelegate call: call.CalleeParams = CopyTypes(types); break;
                    case BirNewBoundDelegate call: call.CalleeParams = CopyTypes(types); break;
                }
            if (node is BirNew creation && creation.ConstructorDeclarationId.OrDefault() is string constructorId
                && plan.Parameters.TryGetValue(constructorId, out var constructorTypes)) creation.MemberSignature = CopyTypes(constructorTypes);
            if (node is BirConstructor constructor && constructor.DelegationDeclarationId.OrDefault() is string delegationId
                && plan.Parameters.TryGetValue(delegationId, out var delegationTypes)) constructor.DelegationSig = CopyTypes(delegationTypes);
            switch (node)
            {
                case BirParameter parameter:
                    parameter.GenericParameterSignatureBounds = default; parameter.NominalParameterSignatureIdentity = default; break;
                case BirLambdaParameter parameter:
                    parameter.GenericParameterSignatureBounds = default; parameter.NominalParameterSignatureIdentity = default; break;
            }
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
        }
        foreach (var root in roots) Walk(root);
        var usedMarkers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in plan.Parameters.Values.SelectMany(value => value))
            BirGraphNameFields.VisitTypeStrings(type, name => { if (Markers.ContainsKey(name)) usedMarkers.Add(name); });
        foreach (var root in roots)
        {
            var removed = root.Types.OrEmpty().Where(type => type.Name.OrDefault() is string name
                && Markers.ContainsKey(name) && !usedMarkers.Contains(name)).ToArray();
            if (removed.Length == 0) continue;
            // The producer's marker registry selects these exact owned rows.
            // Report before detaching; no source membership is recaptured here.
            retireTypes(root, removed);
            root.Types = root.Types.Value.Where(type => !removed.Any(value => ReferenceEquals(value, type))).ToArray();
        }
    }

    static TypeNode[] CopyTypes(IEnumerable<TypeNode> types) => types.Select(BirGraphValue.CopyType).ToArray();
}
