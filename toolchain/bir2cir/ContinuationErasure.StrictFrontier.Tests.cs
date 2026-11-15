using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ContinuationErasure
{
    internal static void StrictFrontierSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Continuation strict frontier: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Continuation strict custody accepted an unreported occurrence transition");
        }
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var any = new TypeNode.Fqn("kotlin.Any");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var ownerType = new TypeNode.Fqn("ContinuationStorage", new TypeNode[] { ownerVariable });
        var ownerContinuation = new TypeNode.Fqn(Cont, new TypeNode[] { ownerVariable });
        var methodContinuation = new TypeNode.Fqn(Cont, new TypeNode[] { methodVariable });
        var physicalContinuation = new TypeNode.Fqn(Cont, new TypeNode[] { any });
        var native = new BirFieldDeclaration { Name = "addressed", Type = ownerContinuation };
        var ordinary = new BirFieldDeclaration { Name = "ordinary", Type = ownerContinuation };
        var fieldRead = new BirFieldGet { OwnerType = ownerType, Name = native.Name, Ret = ownerContinuation,
            Recv = E(new BirThis { Sty = ownerType }) };
        var byrefParameter = new BirParameter { Name = "slot", Type = new TypeNode.ByRef(ownerContinuation) };
        // A supported local ClrRef parameter and direct byref field operand
        // select native storage without an invented foreign MemberRef.
        var address = new BirFunctionDeclaration { Name = "address", DeclarationId = "continuation-address", Static = false,
            Params = new[] { byrefParameter }, Ret = unit, Body = Array.Empty<IBirStatement>() };
        var addressCall = new BirCallInstance { OwnerType = ownerType, Method = address.Name,
            DeclarationId = address.DeclarationId, Sig = new TypeNode[] { byrefParameter.Type.Value }, Ret = unit,
            Recv = E(new BirThis { Sty = ownerType }),
            Args = new IBirExpression[] { new BirByRefOf { Inner = E(fieldRead) } } };
        var use = new BirFunctionDeclaration { Name = "touch", DeclarationId = "continuation-touch", Static = false,
            Params = Array.Empty<BirParameter>(), Ret = unit,
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = E(addressCall) } } };
        var parameter = new BirParameter { Name = "value", Type = methodContinuation };
        var signature = new BirFunctionDeclaration { Name = "identity", DeclarationId = "continuation-identity", Static = false,
            TypeParams = new[] { new BirTypeParameter { Name = "M" } }, Params = new[] { parameter },
            Ret = methodContinuation, Body = new IBirStatement[] { new BirReturn { Value = E(new BirLocal {
                Name = "value", Sty = methodContinuation }) } } };
        var uncheckedMethod = new BirFunctionDeclaration { Name = "read", DeclarationId = "continuation-unchecked", Static = false,
            TypeParams = new[] { new BirTypeParameter { Name = "M" } }, Ret = methodVariable,
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Nullable(any) } },
            Body = new IBirStatement[] { new BirReturn { Value = E(new BirCast { Type = methodVariable,
                ReifiedTypeOperand = false, E = E(new BirLocal { Name = "value", Sty = new TypeNode.Nullable(any) }) }) } } };
        var owner = new BirClassDefinition { Name = "ContinuationStorage", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = new[] { native, ordinary },
            Methods = new[] { address, use, signature, uncheckedMethod } };
        var outer = new BirParameter { Name = "$outer", Type = ownerType, Outer = true };
        var constructor = new BirConstructor { Params = new[] { outer }, Body = Array.Empty<IBirStatement>() };
        var inner = new BirClassDefinition { Name = "ContinuationStorage.Inner", Kind = "class", NestedIn = owner.Name,
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "T" } },
            TypeParams = Array.Empty<BirTypeParameter>(), Ctors = new[] { constructor } };
        var file = new BirFileDeclaration { FileClass = "ContinuationFrontier", Types = new IBirTypeDeclaration[] { owner, inner } };
        var roots = new[] { file };
        var generic = GenericRepresentationPolicy.CreateSource(roots, references.Aliases, references);
        var valueSource = ValueRepresentationLowering.CaptureSource(roots);
        var values = ValueRepresentationLowering.CreateSource(roots, references, false,
            new ValueRepresentationSourcePolicy(roots, references, false), valueSource, generic);
        var pending = UncheckedGenericCastReturnErasure.CaptureSource(roots);
        var uncheckedIndex = pending.SourceDeclarations;
        Need(uncheckedIndex.ByIdentity.Count == 1 && ReferenceEquals(uncheckedIndex.ByIdentity[uncheckedMethod.DeclarationId.Value].SourceMethod, uncheckedMethod),
            "fixture did not select its exact original unchecked declaration");
        UncheckedGenericCastReturnErasure.Apply(file, pending);
        generic.PrepareSourceLocations(roots);
        generic.RefreshPhysicalNativeLocations(roots);
        var bindings = new CurrentDeclarationBindings(roots);
        var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, references, generic);
        var signatures = FBoundStarProjectionErasure.CaptureSourceSignatures(bindings);
        var definitions = bindings.SourceDefinitions;
        var methods = bindings.SourceLocalDeclarations;
        var ownerBinding = bindings.GetCapturedSource(owner);
        var signatureBinding = bindings.GetCapturedSource(signature);
        var constructorBinding = bindings.GetCapturedSource(constructor);
        var transitions = new SuspendColdLowering.SourcePipelineTransitions(roots, bindings, proofs, generic, null,
            values.RetireSourceDeclaration, Array.Empty<BirFunctionDeclaration>());
        var originalSignature = signatures.GetSourceSignature(signatureBinding);
        var ownerMethods = owner.Methods.Value;
        var fields = owner.Fields.Value;
        var parameters = signature.Params.Value;
        var eligibility = generic.ProjectArgumentHead(ownerType, true, null);
        Need(generic.IsNativeField(native) && !generic.IsNativeField(ordinary)
            && proofs.ExactTypeArgument(owner.Name.Value, 0)
            && eligibility is TypeNode.Fqn { Args: [TypeNode.Star] },
            "actual byref selection did not establish native storage and original source eligibility");

        Apply(file, references);
        ConsumeExactOuterSource(file);
        Need(ReferenceEquals(owner.Fields.Value, fields) && ReferenceEquals(owner.Methods.Value, ownerMethods)
            && ReferenceEquals(signature.Params.Value, parameters) && ReferenceEquals(parameters[0], parameter)
            && ReferenceEquals(inner.Ctors.Value[0], constructor) && ReferenceEquals(constructor.Params.Value[0], outer)
            && !outer.Outer.IsPresent && outer.Type.Value == ownerType,
            "erasure replaced owning declarations/parameters or lost the exact enclosing frame before consumption");
        Need(native.Type.Value == physicalContinuation && ordinary.Type.Value == physicalContinuation
            && fieldRead.Ret.Value == physicalContinuation && signature.Ret.Value == physicalContinuation
            && parameter.Type.Value == physicalContinuation && byrefParameter.Type.Value == new TypeNode.ByRef(physicalContinuation)
            && addressCall.Sig.Value[0] == new TypeNode.ByRef(physicalContinuation),
            "current native field or current method/call signature did not take the physical continuation ABI");
        Need(native.KotlinType.Value == ownerContinuation && parameter.KotlinType.Value == methodContinuation
            && signature.RetKotlinType.Value == methodContinuation && originalSignature[0] == methodContinuation
            && ReferenceEquals(signatures.GetSourceSignature(signatureBinding), originalSignature)
            && ReferenceEquals(bindings.SourceDefinitions, definitions) && ReferenceEquals(bindings.SourceLocalDeclarations, methods)
            && ReferenceEquals(methods[(owner.Name.Value, signature.DeclarationId.Value)], signatureBinding)
            && ReferenceEquals(pending.SourceDeclarations, uncheckedIndex)
            && uncheckedIndex.ByIdentity[uncheckedMethod.DeclarationId.Value].Return == methodVariable
            && generic.ProjectArgumentHead(ownerType, true, null) == eligibility
            && generic.IsNativeField(native) && !generic.IsNativeField(ordinary) && proofs.ExactTypeArgument(owner.Name.Value, 0),
            "physical erasure rebuilt source/proof catalogs, changed frozen signatures or widened native eligibility");

        var scalarAliases = new Dictionary<string, string> { ["kotlin.Any"] = "System.Object" };
        var runtimeContext = new BirTypeLowering.PhysicalTypeContext(false, scalarAliases);
        var shadow = (BirFileDeclaration)file.DeepClone();
        BirTypeLowering.Apply(shadow, runtimeContext);
        // A private collision projection must not register cloned occurrences
        // or mutate the actual declarations before their own physical rewrite.
        Need(native.Type.Value == physicalContinuation
            && ReferenceEquals(bindings.SourceDefinitions, definitions)
            && ReferenceEquals(bindings.SourceLocalDeclarations, methods)
            && !bindings.TryGetCapturedSource((BirGraphValue)shadow.Types.Value[0], out _),
            "collision shadow changed or entered the actual declaration authority");
        BirTypeLowering.Apply(file, runtimeContext);
        var expectedNativeType = new TypeNode.Fqn(Cont, new TypeNode[] { new TypeNode.Fqn("System.Object") });
        Need(TypeNodeDataEquality.Equal(native.Type.Value, expectedNativeType)
            && TypeNodeDataEquality.Equal(parameter.Type.Value, expectedNativeType)
            && ReferenceEquals(signature.Params.Value, parameters)
            && ReferenceEquals(owner.Fields.Value, fields)
            && native.KotlinType.Value == ownerContinuation
            && signature.RetKotlinType.Value == methodContinuation
            && ReferenceEquals(signatures.GetSourceSignature(signatureBinding), originalSignature)
            && generic.ProjectArgumentHead(ownerType, true, null) == eligibility
            && generic.IsNativeField(native) && !generic.IsNativeField(ordinary)
            && proofs.ExactTypeArgument(owner.Name.Value, 0)
            && uncheckedIndex.ByIdentity[uncheckedMethod.DeclarationId.Value].Return == methodVariable,
            "physical type rewriting lost source signatures, ownership, unchecked guards or exact native authority");

        var projection = transitions.Projection;
        // The SAME backend's method-vector callback participates in sealing;
        // equal IDs/names or an unchanged count cannot authorize replacements.
        owner.Methods.Value[0] = (BirFunctionDeclaration)address.DeepClone();
        Reject(projection.SealSourceContinuations);
        owner.Methods.Value[0] = address;
        (owner.Methods.Value[0], owner.Methods.Value[1]) = (owner.Methods.Value[1], owner.Methods.Value[0]);
        Reject(projection.SealSourceContinuations);
        (owner.Methods.Value[0], owner.Methods.Value[1]) = (owner.Methods.Value[1], owner.Methods.Value[0]);
        var currentTypes = file.Types.Value;
        var emitted = new BirClassDefinition { Name = "ContinuationPhysicalHelper", Kind = "class", Generated = true };
        file.Types = currentTypes.Append(emitted).ToArray();
        Reject(projection.SealSourceContinuations);
        transitions.PublishTypes(file, new IBirTypeDeclaration[] { emitted });
        var appendedBinding = bindings.GetCapturedSource(emitted);
        projection.SealSourceContinuations();
        Reject(() => projection.RequireOccurrence(native.DeepClone()));
        Reject(() => projection.RequireOccurrence(signature.DeepClone()));
        Need(!outer.Outer.IsPresent && ReferenceEquals(ownerBinding.Source, owner)
            && ReferenceEquals(signatureBinding.Source, signature)
            && ReferenceEquals(constructorBinding.Source, constructor)
            && ReferenceEquals(appendedBinding.Source, emitted)
            && ReferenceEquals(owner.Fields.Value[0], native)
            && TypeNodeDataEquality.Equal(native.Type.Value, expectedNativeType)
            && TypeNodeDataEquality.Equal(parameter.Type.Value, expectedNativeType)
            && generic.IsNativeField(native) && !generic.IsNativeField(ordinary)
            && ReferenceEquals(signatures.GetSourceSignature(signatureBinding), originalSignature)
            && proofs.ExactTypeArgument(owner.Name.Value, 0)
            && ReferenceEquals(proofs.LocalSourceMethod(signature.DeclarationId.Value), signature),
            "typed seal lost physical types or exact original signature/native/proof authorities");
        generic.CompleteSourceContinuation(roots, null, projection);
        values.CompleteSourceContinuation(roots, null, projection);
        UncheckedGenericCastReturnErasure.CompleteSourceDeclarations(pending, roots, null, projection);
        Need(uncheckedIndex.ByIdentity[uncheckedMethod.DeclarationId.Value].SourceMethod == null
            && uncheckedIndex.ByIdentity[uncheckedMethod.DeclarationId.Value].Return == methodVariable
            && ReferenceEquals(signature.Params.Value, parameters) && ReferenceEquals(owner.Fields.Value, fields),
            "terminal continuation changed original unchecked facts or current owned fields/signatures");
        signatures.CompleteSourceLifetime();
        Reject(() => signatures.GetSourceSignature(signatureBinding));
        bindings.CompleteSourceCustody(projection);
        Reject(() => { _ = ownerBinding.Source; });
        Reject(() => { _ = signatureBinding.Source; });
        Reject(() => { _ = constructorBinding.Source; });
        Reject(() => { _ = appendedBinding.Source; });
        Reject(() => bindings.CompleteSourceCustody(projection));
        Need(ReferenceEquals(file.Types.Value[0], owner) && ReferenceEquals(owner.Methods.Value[2], signature)
            && ReferenceEquals(file.Types.Value.Last(), emitted), "custody completion replaced the actual physical graph");
        transitions.Release();
        Reject(() => projection.RequireOccurrence(signature));
        Console.WriteLine("[continuation strict frontier] self-test OK (actual byref field, physical signatures, outer consumption, original catalogs, strict backend vectors and terminal custody)");
    }
}
