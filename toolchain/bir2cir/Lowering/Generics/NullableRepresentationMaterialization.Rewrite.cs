using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableRepresentationMaterialization
{
    static void Rewrite(BirGraphValue node, NullableRepresentationTypes mapping,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methods, Func<BirGraphValue, NullableRepresentationTypes> declarationMapping,
        NullableGenericErasure.Pos position = NullableGenericErasure.Pos.Slot, Func<BirGraphValue, bool> skipChild = null)
    {
        if (node is IBirExecutablePayload) return;
        try { RewriteCore(node, mapping, methods, declarationMapping, position, skipChild); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            var description = node is BirFunctionDeclaration method ? "Nullable frame rewrite of " + method.Name.OrDefault()
                : node is BirGraphNode operation ? operation.Tag + " " + ForeignDeclarationFacts.Read(node)?.Member : null;
            if (description == null) throw;
            throw new InvalidOperationException(description + ": " + error.Message, error);
        }
    }

    static void RewriteCore(BirGraphValue node, NullableRepresentationTypes mapping,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methods, Func<BirGraphValue, NullableRepresentationTypes> declarationMapping,
        NullableGenericErasure.Pos position, Func<BirGraphValue, bool> skipChild)
    {
        var selected = node is BirGraphNode || node is BirConstructor { DelegationSig.HasValue: true }
            || node is BirInlineCapture { SharedCellTypeParams.HasValue: true } ? declarationMapping(node) : null;
        TypeNode[] closedArguments = null, closedDispatch = null, closedDispatchOwner = null;
        NullableRepresentationFrame capturedFrame = null;
        Dictionary<int, TypeNode[]> storageBounds = null;
        var arguments = node.ApplicationTypeArguments.OrDefault();
        var synthetic = node switch { BirNewClosure value => (BirGraphValue)value.SynthClass.OrDefault(),
            BirNewSam value => value.SynthClass.OrDefault(), BirNewSuspendLambda value => value, _ => null };
        if (node is BirNewClosure or BirNewSam && synthetic == null && arguments != null)
        {
            var owner = node switch { BirNewClosure value => value.ClosureType.OrDefault(), BirNewSam value => value.SamType.OrDefault(), _ => null };
            if (owner is TypeNode.Fqn named && mapping.Slot(new TypeNode.Fqn(named.Name, arguments)) is TypeNode.Fqn { Args: { } physical })
                closedArguments = physical;
        }
        var receivers = (node as BirCallInline)?.Recvs.OrDefault();
        if (receivers?.DispatchTypeArgs.OrDefault() is { } dispatch && selected?.DispatchOwnerFrame is { } dispatchFrame)
        {
            closedDispatch = mapping.CloseMethod(dispatchFrame, dispatch);
            closedDispatchOwner = mapping.CloseMethod(selected.ApplicationOwnerFrame, dispatch);
        }
        if (node is BirLocalFunction local)
        {
            var frame = selected.MethodFrame;
            if (local.Decl.Value._syntheticTypeArgs.OrDefault() is { } origins)
            {
                if (origins.Length != frame.SourceArity) throw new InvalidOperationException("Local function capture origins do not match its source frame");
                local.Decl.Value._syntheticTypeArgs = Enumerable.Range(0, frame.PhysicalArity).Select(index => {
                    var slot = frame.PhysicalSlot(index);
                    var origin = origins[slot.SourceIndex];
                    return origin is TypeNode.Tv { Scope: "type" } ? mapping.ArgumentForRole(origin, slot.Representation)
                        : slot.Representation == NullableRepresentationFrame.Role.Ordinary ? origin
                        : frame.Variable(new TypeNode.Tv("method", slot.SourceIndex), slot.Representation);
                }).ToArray();
            }
            return;
        }
        if (node is BirCallLocal or BirLocalFunctionReference && selected.MethodFrame.RequiresMetadata)
            closedArguments = mapping.CloseMethod(selected.MethodFrame, arguments);
        var closureReturn = node is BirNewClosure closure && closure.SynthClass.OrDefault() is { } body
            && !ClosureSynthesis.HasPreboundFrame(body) && body.Ret.OrDefault() is { } result ? mapping.Argument(result) : null;
        if (synthetic != null && arguments != null && NullableRepresentationViews.TypeParameters(synthetic).HasValue
            && (node is BirNewSuspendLambda suspend ? suspend.TypeFrame.OrDefault() != "dense" : !ClosureSynthesis.HasPreboundFrame(synthetic)))
        {
            var parameters = NullableRepresentationViews.TypeParameters(synthetic).Value;
            if (arguments.Length != parameters.Length) throw new InvalidOperationException("Synthetic capture arguments do not match their declarations");
            var frame = CaptureFrame(arguments, mapping);
            if (frame.RequiresMetadata)
            {
                capturedFrame = frame;
                storageBounds = CaptureStorageBounds(parameters, arguments, frame, mapping);
                if (node is BirNewSam or BirNewClosure) PreserveCompanionSourceBounds(synthetic, frame, owner: true);
                closedArguments = mapping.CloseMethod(frame, arguments);
                var companions = frame.Companions.Select(slot => {
                    var variable = (TypeNode.Tv)arguments[slot.SourceIndex];
                    return new BirTypeParameter { Name = "$" + slot.Representation.ToString().ToLowerInvariant() + "Capture" + variable.Scope + variable.I };
                });
                NullableRepresentationViews.SetTypeParameters(synthetic, parameters.Concat(companions).ToArray());
                if (node is BirNewSam or BirNewClosure) NullableRepresentationViews.SetTypeFacts(synthetic,
                    (NullableRepresentationViews.TypeFacts(synthetic) ?? SourceTypeFacts.Empty).MergeNullableFrame(frame));
            }
        }
        if (node is BirGraphNode && node.DeclarationIdentityFact.OrDefault() is string id
            && methods.TryGetValue(id, out var calledFrame) && calledFrame.RequiresMetadata)
        {
            if (arguments == null) throw new InvalidOperationException("Nullable-frame call has no source type arguments");
            closedArguments = mapping.CloseMethod(calledFrame, arguments);
            if (node is BirCallInline inline) inline.Ga = calledFrame.PhysicalArity;
        }
        var snapshot = NullableRepresentationViews.ReturnFacts(node);
        node.RewriteRepresentationParametersInPlace((role, parameters) => {
            var rewritten = RewriteDescriptorParameters(parameters, selected, mapping);
            var frame = role == BirRepresentationParameterRole.Method ? selected?.MethodFrame : selected?.OwnerFrame;
            return frame == null ? rewritten : ExpandParameters(rewritten, frame,
                role == BirRepresentationParameterRole.Method ? "method" : "type");
        });
        if (closedDispatch != null) receivers.DispatchTypeArgs = default;
        node.RewriteRepresentationInPlace((ownership, role, type) => {
            if (NullableRepresentationViews.Frozen(ownership) || ownership == BirGenericFrameRole.SyntheticApplication) return type;
            if (role == BirRepresentationRole.TypeArguments && closedArguments != null) return type;
            if (node is BirCallLocal or BirLocalFunctionReference && role == BirRepresentationRole.Declaration) return type;
            if (role == BirRepresentationRole.DeclaredMemberType && mapping.IsNativeField(node)) return (selected ?? mapping).Argument(type);
            if (role is BirRepresentationRole.Declaration or BirRepresentationRole.DeclaredMemberType
                || role == BirRepresentationRole.Return && snapshot.DeclarationReturn)
                return (selected ?? mapping).Rewrite(type, selected == null ? NullableGenericErasure.Pos.Bound : NullableGenericErasure.Pos.Slot);
            if (role == BirRepresentationRole.Constraint) return mapping.Rewrite(type,
                type is TypeNode.Tv ? NullableGenericErasure.Pos.Argument : NullableGenericErasure.Pos.Slot);
            var location = role switch {
                // A ref-cell declaration owns a value slot. Native address uses do not
                // change its declaration projection to the argument frame.
                BirRepresentationRole.Element when node is BirRefTypeDeclaration => NullableGenericErasure.Pos.Slot,
                BirRepresentationRole.ValueType or BirRepresentationRole.Element when node is not BirGraphNode && mapping.IsNativeField(node)
                    => NullableGenericErasure.Pos.Argument,
                BirRepresentationRole.Return or BirRepresentationRole.ResultStamp when node is BirFieldGet or BirStaticFieldGet or BirLateinitGet
                    && mapping.IsNativeField(node) => NullableGenericErasure.Pos.Argument,
                BirRepresentationRole.Return when node is BirFunctionDeclaration prepared && prepared._preparedDelegateReturnArgument.OrDefault()
                    => NullableGenericErasure.Pos.Argument,
                BirRepresentationRole.TypeArguments => NullableGenericErasure.Pos.Argument,
                BirRepresentationRole.Element when NullableGenericErasure.IsArgumentElementKind((node as BirGraphNode)?.Tag)
                    => NullableGenericErasure.Pos.Argument,
                BirRepresentationRole.Foreign => NullableGenericErasure.Pos.Bound,
                _ => position,
            };
            return mapping.Rewrite(type, location);
        }, (ownership, role, child) => {
            if (NullableRepresentationViews.Frozen(ownership) || ownership is BirGenericFrameRole.OverrideDeclaration or BirGenericFrameRole.SyntheticApplication
                || role is BirRepresentationRole.Attributes or BirRepresentationRole.TypeParameterDeclarations || skipChild?.Invoke(child) == true) return;
            if (role == BirRepresentationRole.InheritedImplementation) RewriteImplementation((BirInheritedImplementation)child, declarationMapping(child), mapping);
            else Rewrite(child, mapping, methods, declarationMapping, position);
        });
        if (closedDispatch != null) { receivers.DispatchTypeArgs = closedDispatch; receivers.DispatchOwnerTypeArgs = closedDispatchOwner; }
        if (closedArguments != null) node.ApplicationTypeArguments = closedArguments;
        if (capturedFrame != null)
        {
            var parameters = NullableRepresentationViews.TypeParameters(synthetic).Value;
            foreach (var (index, bounds) in storageBounds)
            {
                parameters[index].Constraints = bounds;
                var ordinary = capturedFrame.SourcePosition(capturedFrame.PhysicalSlot(index).SourceIndex);
                parameters[ordinary].Constraints = parameters[ordinary].Constraints.OrEmpty().Concat(bounds).Distinct().ToArray();
            }
            foreach (var sourceIndex in capturedFrame.NullableIndices)
            {
                var ordinary = capturedFrame.Variable(new TypeNode.Tv("type", sourceIndex), NullableRepresentationFrame.Role.Ordinary).I;
                var companion = capturedFrame.NullableVariable(new TypeNode.Tv("type", sourceIndex)).I;
                parameters[ordinary].Constraints = parameters[ordinary].Constraints.OrEmpty().Append(closedArguments[companion]).ToArray();
            }
        }
        if (closureReturn != null) ((BirNewClosure)node).SynthClass.Value.Ret = closureReturn;
    }

    static BirTypeParameter[] RewriteDescriptorParameters(IReadOnlyList<BirTypeParameter> parameters,
        NullableRepresentationTypes declaration, NullableRepresentationTypes lexical)
    {
        var mapper = declaration ?? lexical;
        var position = declaration == null ? NullableGenericErasure.Pos.Bound : NullableGenericErasure.Pos.Slot;
        return parameters.Select(parameter => {
            var copy = (BirTypeParameter)parameter.DeepClone();
            copy.RewriteRepresentationInPlace((_, role, type) => mapper.Rewrite(type,
                role == BirRepresentationRole.Constraint && declaration != null && type is TypeNode.Tv ? NullableGenericErasure.Pos.Argument : position),
                (_, _, _) => throw new InvalidOperationException("A type parameter unexpectedly owns a graph child"));
            return copy;
        }).ToArray();
    }

    static void RewriteImplementation(BirInheritedImplementation implementation, NullableRepresentationTypes declaration, NullableRepresentationTypes lexical)
    {
        var mapper = declaration ?? lexical;
        var position = declaration == null ? NullableGenericErasure.Pos.Bound : NullableGenericErasure.Pos.Slot;
        if (implementation.Owner.HasValue) implementation.Owner = lexical.Slot(implementation.Owner.Value);
        if (implementation.Sig.HasValue) implementation.Sig = implementation.Sig.Value.Select(type => mapper.Rewrite(type, position)).ToArray();
        if (implementation.TypeParams.HasValue) implementation.TypeParams = RewriteDescriptorParameters(implementation.TypeParams.Value, declaration, lexical);
    }
}
