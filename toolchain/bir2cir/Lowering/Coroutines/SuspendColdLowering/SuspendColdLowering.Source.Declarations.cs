using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed partial class SourceFunGen
    {
        BirParameter[] CloneNamedParameters() => _params.Select(parameter =>
            (BirParameter)_module.Transitions.CloneIndependent(parameter.Declaration)).ToArray();

        TypeNode ColdEntrySmTypeInst()
        {
            if (_smAllTps.Count == 0) return new TypeNode.Fqn(_smType);
            return new TypeNode.Fqn(_smType, Enumerable.Range(0, _ownerTypeParams.Count)
                .Select(index => (TypeNode)new TypeNode.Tv("type", index))
                .Concat(Enumerable.Range(0, _typeParams.Count).Select(index => (TypeNode)new TypeNode.Tv("method", index))).ToArray());
        }

        BirFunctionDeclaration ColdEntrySm()
        {
            var sm = ColdEntrySmTypeInst();
            var args = new List<IBirExpression>();
            var parameters = new List<TypeNode>();
            if (_isMember) { args.Add(new BirThis { Outer = true }); parameters.Add(_selfType); }
            foreach (var parameter in _params) { args.Add(Local(parameter.Name)); parameters.Add(parameter.Type); }
            args.Add(Local(CompletionParameter)); parameters.Add(ContAny());
            var entry = ColdMethod(new IBirStatement[] {
                new BirVariable { Name = StateMachineLocal, Type = sm,
                    Init = new BirNew { Type = sm, ArgTypes = parameters.Select(BirGraphValue.CopyType).ToArray(), Args = args.ToArray() } },
                Ret(new BirCallInstance { OwnerType = sm, Virtual = true, Recv = Local(StateMachineLocal),
                    Method = "invokeSuspend", Sig = new TypeNode[] { AnyTn },
                    Args = new IBirExpression[] { NullConst(AnyTn) }, Ret = AnyTn }),
            });
            entry.ColdEntryMachine = (_smType, _ownerTypeParams.Count);
            return entry;
        }

        BirFunctionDeclaration ColdEntryDirect(IBirStatement[] body)
        {
            var statements = body.Select(statement => (IBirStatement)ReplaceCoroutineContextDirect((BirGraphValue)statement)).ToList();
            if (IsUnitTn(_resultType) && (statements.Count == 0 || statements[^1] is not BirReturn))
                statements.Add(Ret(UnitValue()));
            return ColdMethod(statements.ToArray());
        }

        BirFunctionDeclaration ColdMethod(IBirStatement[] body)
        {
            var method = new BirFunctionDeclaration {
                Name = _coldName, Static = !_isMember, Override = _memberOverride, Virtual = _memberVirtual,
                Abstract = false, ObjectOverride = false,
                Vis = _physicalSlotBridge ? _physicalSlotVisibility ?? "private" : "public",
                Params = CloneNamedParameters().Append(new BirParameter { Name = CompletionParameter, Type = ContAny() }).ToArray(),
                Ret = AnyTn, Body = body, Attrs = Array.Empty<BirAttribute>(), Generated = true,
            };
            FinishColdDeclaration(method);
            return method;
        }

        BirFunctionDeclaration ColdEntryAbstract()
        {
            var method = ColdMethod(Array.Empty<IBirStatement>());
            method.Static = false; method.Virtual = true; method.Abstract = true;
            return method;
        }

        BirFunctionDeclaration ColdEntryStub(string reason) => ColdMethod(UnsupportedThrowBody(
            $"{(_ownerClass ?? _fileClass)}.{_name}: {reason} — this suspend fun is not supported by bir2cir's cold-lowering."));

        IBirStatement[] UnsupportedThrowBody(string message) => new IBirStatement[] {
            new BirThrow { Value = new BirNewClr { Type = new TypeNode.Fqn("System.NotSupportedException"),
                ArgTypes = new TypeNode[] { new TypeNode.Fqn("kotlin.String") },
                Args = new IBirExpression[] { new BirConstant { Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral(message) } } } },
        };

        void FinishColdDeclaration(BirFunctionDeclaration method)
        {
            if (_typeParams.Count > 0) method.TypeParams = _methodTypeParamDecls.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            CarrySourceDeclaration(method);
            CarryOverrideMarkers(method, true);
            CarryPhysicalSlotFacts(method, true);
            if (_declarationId != null)
            {
                method.DeclarationId = _declarationId + "|cold";
                if (_m.NullableRepresentationFrame.OrDefault() is { } frame)
                    method.ColdRepresentation = (_declarationId, frame);
                method.DeclarationSourceName = _declarationSourceName
                    ?? throw new InvalidOperationException("suspend declaration identity has no frontend source name");
            }
            _methodProjections.Add(new(_m, method));
        }

        void CarryOverrideMarkers(BirFunctionDeclaration method, bool coldEntry = false)
        {
            method.SourceCallableVirtual = _m.SourceCallableVirtual;
            method.OwnerDependentSourceSlots = _m.OwnerDependentSourceSlots;
            method.HasNativeOverrideSlot = _m.HasNativeOverrideSlot;
            // Implementing a Kotlin interface requires a CLR slot even for a
            // final override. Neither suspend entry is a second override point.
            if (_m.Mods.OrDefault() is { } mods && mods.Override.OrDefault() && !mods.Open.OrDefault() && !_memberAbstract)
                method.ClrFinal = true;
            if (_overrideMarkers.Length == 0) return;
            var markers = _overrideMarkers.Select(marker => (BirOverride)marker.DeepClone()).ToArray();
            if (coldEntry)
                foreach (var marker in markers)
                    if (marker.DeclarationId.HasValue) marker.PhysicalDeclarationId = marker.DeclarationId.Value + "|cold";
            method.Overrides = markers;
            // Each entry implements the corresponding entry of the selected source
            // declaration. A generated carrier forwarder is not another source override.
            if (_m.SourceCallableVirtual && _typeParams.Count != 0)
            {
                method.SignatureOverrideDeclarations = markers.Where(edge => edge.DeclarationId.HasValue)
                    .Select(edge => edge.PhysicalDeclarationId.OrDefault() ?? edge.DeclarationId.Value)
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (coldEntry)
                    method.OverrideRepresentationDeclarations = markers.Where(edge => edge.DeclarationId.HasValue)
                        .Select(edge => (Slot: edge.PhysicalDeclarationId.Value, Source: edge.DeclarationId.Value))
                        .Distinct().ToDictionary(edge => edge.Slot, edge => edge.Source);
            }
        }

        void CarrySourceDeclaration(BirFunctionDeclaration method)
        {
            if (method.TypeParams.HasValue)
                OwnerConstrainedMethodLowering.PreserveDispatchBounds(method, method.TypeParams.Value);
            method.KotlinSourceMember = _m.KotlinSourceMember.HasValue ? _m.KotlinSourceMember : _name;
            method.SuspendSourceParams = _params.Select(parameter => BirGraphValue.CopyType(parameter.Type)).ToArray();
            method.SuspendSourceRet = _m.Ret.HasValue ? OptionalField<TypeNode>.Of(BirGraphValue.CopyType(_m.Ret.Value)) : OptionalField<TypeNode>.Null;
        }

        void CarryKotlinDeclarationMetadata(BirFunctionDeclaration method)
        {
            if (_m.NullableRepresentationFrame.IsPresent) method.NullableRepresentationFrame = _m.NullableRepresentationFrame;
            CallableVisibilityLowering.Copy(_m, method);
            RoundtripMetadata.CopySourceOverloadPriority(_m, method);
            var source = _m.Mods.OrDefault();
            if (source?.Operator.OrDefault() == true || source?.Infix.OrDefault() == true || source?.Inline.OrDefault() == true)
            {
                var modifiers = method.Mods.OrDefault() ?? new BirModifiers();
                if (source.Operator.OrDefault()) modifiers.Operator = true;
                if (source.Infix.OrDefault()) modifiers.Infix = true;
                if (source.Inline.OrDefault()) modifiers.Inline = true;
                method.Mods = modifiers;
            }
            if (_m.InlineBir.IsPresent) method.InlineBir = _m.InlineBir.HasValue
                ? OptionalField<byte[]>.Of(_m.InlineBir.Value.ToArray()) : _m.InlineBir;
            if (_m.MethodTypeParameterBoundsPre.IsPresent) method.MethodTypeParameterBoundsPre = _m.MethodTypeParameterBoundsPre;
            if (_m.SemanticReifiedIndices.IsPresent) method.SemanticReifiedIndices = _m.SemanticReifiedIndices.HasValue
                ? OptionalField<int[]>.Of(_m.SemanticReifiedIndices.Value.ToArray()) : _m.SemanticReifiedIndices;
            if (_m.NullableWitnessIndices.IsPresent) method.NullableWitnessIndices = _m.NullableWitnessIndices.HasValue
                ? OptionalField<int[]>.Of(_m.NullableWitnessIndices.Value.ToArray()) : _m.NullableWitnessIndices;
            if (_m.ReflectedWitnessIndices.HasValue) method.ReflectedWitnessIndices = _m.ReflectedWitnessIndices.Value.ToArray();
            RoundtripMetadata.CopySourceMethodIdentity(_m, method);
        }

        string ForwarderEntry(bool cold) =>
            _m.ExistentialCallable == ExistentialCallableRole.Forwarder && _typeParams.Count != 0 &&
            _selfType is TypeNode.Fqn owner && _m.SignatureOverrideDeclarations.OrEmpty() is { Length: > 0 } slots
                ? DeclarationIdentityBinding.InheritedProjectionId(owner.Name, slots) + (cold ? "|cold" : "") : null;

        void CarryPhysicalSlotFacts(BirFunctionDeclaration method, bool coldEntry)
        {
            method.ExistentialCallable = _m.ExistentialCallable;
            if (_m.MaterialFrameOrigin is { } origin) method.MaterialFrameOrigin = coldEntry ? origin + "|cold" : origin;
            // Generated forwarding methods carry physical slot IDs rather than FIR
            // override markers. Both entries must implement their corresponding slots.
            if (_m.ExistentialCallable == ExistentialCallableRole.Forwarder && _m.SignatureOverrideDeclarations.HasValue)
                method.SignatureOverrideDeclarations = _m.SignatureOverrideDeclarations.Value
                    .Select(id => coldEntry ? id + "|cold" : id).ToArray();
            if (ForwarderEntry(coldEntry) is { } entry) method.LocalBodyEntry = (entry, null);
            if (_m.ImplicitExistentialImplementationId is string implementation)
                method.ImplicitExistentialImplementationId = coldEntry ? implementation + "|cold" : implementation;
            if (!_physicalSlotBridge) return;
            method.PhysicalSlotBridge = true;
            method.OverrideAdapterGroup = _m.OverrideAdapterGroup;
            if (_clrInterfaceSlotBridge) method.ClrInterfaceSlotBridge = true;
            if (coldEntry && _taskOnlySlotBridge) return;
            if (_taskOnlySlotBridge) method.SuspendTaskOnlyBridge = true;
            if (Project(_clrInterfaceImpls) is { } interfaces) method.ClrInterfaceImpls = interfaces;
            if (Project(_clrBaseImpls) is { } bases) method.ClrBaseImpls = bases;

            BirMethodImpl[] Project(OptionalField<BirMethodImpl[]> source)
            {
                if (!source.HasValue || source.Value.Length == 0) return null;
                var descriptors = source.Value.Select(item => (BirMethodImpl)_module.Transitions.CloneIndependent(item)).ToArray();
                foreach (var descriptor in descriptors)
                {
                    if (coldEntry)
                    {
                        if (descriptor.LocalOverrideTargets is { } targets)
                            descriptor.LocalOverrideTargets = targets.Select(id => id + "|cold").ToArray();
                        descriptor.Member = (descriptor.Member.OrDefault() ?? _name) + "$dotkt_suspend";
                        descriptor.Params = descriptor.Params.OrEmpty().Append(ContAny()).ToArray();
                        descriptor.Ret = AnyTn;
                    }
                    else if (_m.ExistentialCallable == ExistentialCallableRole.Forwarder)
                    {
                        // The generated forwarder implements its paired carrier slot
                        // with the very same completion ABI, including Task for Unit.
                        descriptor.Ret = BirGraphValue.CopyType(method.Ret.Value);
                    }
                    else if (descriptor.Ret.HasValue)
                    {
                        var result = descriptor.Ret.Value;
                        descriptor.Ret = IsUnitTn(result) && !descriptor.DotktValueReturn.OrDefault()
                            ? new TypeNode.Fqn(_taskBcl)
                            : new TypeNode.Fqn(_taskBcl, new[] { BirTypeLowering.AsReadonlyResultSlot(result) });
                    }
                    descriptor.DotktValueReturn = default;
                }
                return descriptors;
            }
        }
    }
}
