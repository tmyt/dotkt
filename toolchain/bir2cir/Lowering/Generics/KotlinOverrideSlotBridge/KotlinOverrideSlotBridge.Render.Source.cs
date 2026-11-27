using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    static TypeNode.Fqn NativeCursorResult(TypeNode original, TypeNode actual, TypeNode required,
        ValueTypeOracle isValue, ReferenceMetadataIndex refs)
    {
        if (refs == null || required is not TypeNode.Fqn { Args.Length: 1 } cursor
            || ReferenceMetadataIndex.BareOwnerFqn(cursor.Name) != "System.Collections.Generic.IEnumerator"
            || original is not TypeNode.Fqn source || !LowersToObject(actual, refs)
            || !AliasVarianceRepresentation.RequiresErasure(source, refs)) return null;
        var bound = BirTypeLowering.LowerPhysicalType(source, refs.Aliases, isValue, refs.PhysicalTypeNames,
            typeArg: false, nullableFrames: refs.NullableTypeFrames);
        return ClrMemberResolution.SameInterfaceSlotType(bound, required) ? cursor : null;
    }

    internal static BirFunctionDeclaration BuildBridgeSource(IBirTypeDeclaration owner,
        BirFunctionDeclaration implementation, TypeNode[] parameters, TypeNode result, string name,
        ValueTypeOracle isValue, ReferenceMetadataIndex refs, TypeNode.Fqn callOwner = null,
        bool virtualCall = true, string callMember = null, bool unitValueReturn = false,
        bool physicalImplementation = false, Action nativeCursor = null) => BuildBridgeSource(owner, SelectedMethodHeader.FromSource(implementation),
            parameters, result, name, isValue, refs, callOwner, virtualCall, callMember, unitValueReturn, physicalImplementation, nativeCursor: nativeCursor);

    internal static BirFunctionDeclaration BuildBridgeSource(IBirTypeDeclaration owner,
        SelectedInheritedSourceMethod selected, TypeNode[] parameters, TypeNode result, string name,
        ValueTypeOracle isValue, ReferenceMetadataIndex refs, TypeNode.Fqn callOwner = null,
        bool virtualCall = true, string callMember = null, bool unitValueReturn = false,
        bool physicalImplementation = false, Action nativeCursor = null) => BuildBridgeSource(owner, selected.Method,
            parameters, result, name, isValue, refs, callOwner ?? selected.Owner, virtualCall,
            callMember ?? selected.Method.ExplicitClrName.OrDefault(),
            unitValueReturn, physicalImplementation, selected.DeclarationSignature, selected.DeclarationId, nativeCursor);

    static BirFunctionDeclaration BuildBridgeSource(IBirTypeDeclaration owner, SelectedMethodHeader implementation,
        TypeNode[] parameters, TypeNode result, string name, ValueTypeOracle isValue, ReferenceMetadataIndex refs,
        TypeNode.Fqn callOwner = null, bool virtualCall = true, string callMember = null,
        bool unitValueReturn = false, bool physicalImplementation = false,
        TypeNode[] declarationSignature = null, string declarationId = null, Action nativeCursor = null)
    {
        var declared = implementation.Params.OrEmpty();
        var bridgeParameters = new BirParameter[parameters.Length];
        var arguments = new IBirExpression[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = declared[index];
            var parameterName = parameter.Name.OrDefault() ?? "p" + index;
            var physical = parameter.Type.OrDefault();
            var bridge = new BirParameter { Name = parameterName, Type = BirGraphValue.CopyType(parameters[index]) };
            IBirExpression argument = new BirLocal { Name = parameterName };
            if (physical != null && !physical.Equals(parameters[index]))
            {
                bridge.NullableGeneric = BirGraphValue.CopyType(physical);
                var shape = physical is TypeNode.Nullable ? new TypeNode.Nullable(parameters[index]) : parameters[index];
                if (NullableFlags.ComputeValues(shape, isValue) is { } flags) bridge.NullableFlags = flags;
                argument = IsNullableValueSlot(parameters[index], physical, refs, isValue, returnPosition: false)
                    && physical is TypeNode.Nullable nullable
                    ? new BirNullableWrap { Elem = BirGraphValue.CopyType(nullable.Of), E = OptionalField<IBirExpression>.Of(argument),
                        _physicalDeclarationSignature = physicalImplementation }
                    : new BirCast { Type = BirGraphValue.CopyType(physical), E = OptionalField<IBirExpression>.Of(argument),
                        _physicalDeclarationSignature = physicalImplementation };
            }
            bridgeParameters[index] = bridge;
            arguments[index] = argument;
        }
        var ownArguments = Enumerable.Range(0, TypeParameterFrame.Count(owner)).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        var implementationReturn = implementation.Ret.OrDefault();
        var call = new BirCallInstance {
            OwnerType = BirGraphValue.CopyType(callOwner ?? new TypeNode.Fqn(owner.Name.Value, ownArguments.Length == 0 ? null : ownArguments)),
            Virtual = virtualCall, ClrOwnerResolved = true, _physicalDeclarationSignature = physicalImplementation,
            Recv = new BirThis(), Method = callMember ?? implementation.Name.OrDefault(),
            Sig = declared.Select(parameter => BirGraphValue.CopyType(parameter.Type.OrDefault())).ToArray(),
            CalleeParams = (declarationSignature ?? declared.Select(parameter => FunctionSignatureIdentity.SignatureType(
                parameter.Type.OrDefault(), parameter.FunctionSignatureType.OrDefault())).ToArray()).Select(BirGraphValue.CopyType).ToArray(),
            Sty = BirGraphValue.CopyType(implementationReturn), DynRet = BirGraphValue.CopyType(implementationReturn),
            Ret = BirGraphValue.CopyType(implementationReturn), Args = arguments,
        };
        if ((declarationId ?? implementation.DeclarationId.OrDefault()) is { } identity) call.DeclarationId = identity;
        var suspend = implementation.Mods.OrDefault()?.Suspend.OrDefault() == true;
        if (suspend) call.SuspendCall = true;
        var arity = implementation.TypeParams.OrEmpty().Length;
        if (arity > 0)
            call.TypeArgs = Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
        var carried = false;
        IBirStatement statement;
        if (IsVoid(result) && !unitValueReturn) statement = new BirExpressionStatement { Expr = call };
        else
        {
            IBirExpression returned = call;
            if (implementationReturn != null && !implementationReturn.Equals(result))
            {
                returned = nativeCursor != null && NativeCursorResult(implementation.RetKotlinType.OrDefault(),
                    implementationReturn, result, isValue, refs) is { } cursor
                    ? ReverseEnumeratorBridgeSynthesis.AdaptNativeCursor(returned, cursor, nativeCursor)
                    : StringCharSequenceBridge.CanAdaptStringReturn(implementationReturn, result)
                    ? StringCharSequenceBridge.AdaptStringReturn(returned, implementationReturn)
                    : IsNullableValueSlot(result, implementationReturn, refs, isValue, returnPosition: true)
                    && implementationReturn is TypeNode.Nullable nullable
                    ? new BirNullableValue { Elem = BirGraphValue.CopyType(nullable.Of), E = OptionalField<IBirExpression>.Of(returned),
                        _physicalDeclarationSignature = physicalImplementation }
                    : new BirCast { Type = BirGraphValue.CopyType(result), E = OptionalField<IBirExpression>.Of(returned),
                        _physicalDeclarationSignature = physicalImplementation };
                carried = true;
            }
            statement = new BirReturn { Value = OptionalField<IBirExpression>.Of(returned) };
        }
        var method = new BirFunctionDeclaration {
            Name = name, Static = false, Override = false, Virtual = true, Abstract = false, ObjectOverride = false,
            Vis = "private", Params = bridgeParameters, Ret = BirGraphValue.CopyType(result), Body = new[] { statement },
            Attrs = Array.Empty<BirAttribute>(), Generated = true, PhysicalSlotBridge = true, OverrideAdapterGroup = new object(),
            ClrInterfaceImpls = Array.Empty<BirMethodImpl>(), ClrBaseImpls = Array.Empty<BirMethodImpl>(),
        };
        // A final physical bridge implements the selected signature. Keep both the
        // entry point and its forwarding conversions in that frame, including DIM repairs.
        if (physicalImplementation) method._physicalDeclarationSignature = true;
        if (owner.Kind.OrDefault() == "interface") method.ClrInterfaceSlotBridge = true;
        if (unitValueReturn) method.DotktValueReturn = true;
        if (suspend)
        {
            method.Mods = new BirModifiers { Suspend = true };
            method.SuspendRet = BirGraphValue.CopyType(result);
            method.SuspendResult = BirGraphValue.CopyType(implementation.SuspendResult.OrDefault()
                ?? throw new InvalidOperationException("suspend slot bridge source has no logical result"));
        }
        if (carried && implementationReturn != null)
        {
            method.NullableGenericRet = BirGraphValue.CopyType(implementationReturn);
            var shape = implementationReturn is TypeNode.Nullable ? new TypeNode.Nullable(result) : result;
            if (NullableFlags.ComputeValues(shape, isValue) is { } flags) method.RetNullableFlags = flags;
        }
        if (implementation.TypeParams.HasValue)
            method.TypeParams = implementation.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        return MaterializedExecutable.NormalizeSource(method);
    }
}
