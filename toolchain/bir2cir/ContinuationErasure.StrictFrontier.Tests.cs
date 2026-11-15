using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ContinuationErasure
{
    internal static void StrictFrontierSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Continuation strict frontier: " + message); }
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
        generic.PrepareDomLocations(roots);
        generic.RefreshPhysicalNativeLocations(roots);
        var bindings = new CurrentDeclarationBindings(roots);
        var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, references, generic);
        var signatures = FBoundStarProjectionErasure.CaptureSourceSignatures(bindings);
        var definitions = bindings.SourceDefinitions;
        var methods = bindings.SourceLocalDeclarations;
        var ownerBinding = bindings.GetCapturedSource(owner);
        var signatureBinding = bindings.GetCapturedSource(signature);
        var constructorBinding = bindings.GetCapturedSource(constructor);
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

        var projection = new SourceDeclarationProjection(roots, bindings);
        var dom = file.ToDom(projection.ObserveProjection);
        static bool HasOuter(JsonNode node) => node switch {
            JsonObject obj => obj.ContainsKey("outer") || obj.Any(pair => HasOuter(pair.Value)),
            JsonArray array => array.Any(HasOuter),
            _ => false,
        };
        Need(!HasOuter(dom), "an exact outer role escaped the single source frontier before any DOM cleanup");
        projection.ProjectRetiredForests();
        bindings.BindProjected();
        generic.BindDomDeclarations(new JsonNode[] { dom }, declarations: projection);
        values.BindDomDeclarations(new JsonNode[] { dom }, projection.RetiredMethods, projection);
        var continuedUnchecked = UncheckedGenericCastReturnErasure.BindDomDeclarations(pending, new[] { dom },
            projection.RetiredMethods, declarations: projection);
        var continuedSignatures = signatures.BindPhysical();
        var physicalField = projection.Get(native);
        var physicalMethod = projection.Get(signature);
        Need(ReferenceEquals(ownerBinding.Physical, projection.Get(owner))
            && ReferenceEquals(signatureBinding.Physical, physicalMethod)
            && ReferenceEquals(constructorBinding.Physical, projection.Get(constructor))
            && ReferenceEquals(dom["types"][0]["fields"][0], physicalField)
            && TypeJson.Read(physicalField["type"]) == native.Type.Value
            && TypeJson.Read(physicalMethod["params"][0]["type"]) == parameter.Type.Value
            && TypeJson.Read(physicalMethod["ret"]) == signature.Ret.Value
            && generic.IsNativeField(physicalField) && !generic.IsNativeField(projection.Get(ordinary))
            && ReferenceEquals(continuedSignatures[physicalMethod], originalSignature)
            && proofs.ExactTypeArgument(owner.Name.Value, 0)
            && ReferenceEquals(proofs.LocalMethod(signature.DeclarationId.Value), physicalMethod)
            && ReferenceEquals(continuedUnchecked.ByIdentity[uncheckedMethod.DeclarationId.Value].Method, projection.Get(uncheckedMethod))
            && continuedUnchecked.ByIdentity[uncheckedMethod.DeclarationId.Value].Return == methodVariable,
            "the single observed frontier lost current physical types or the original exact signature/native/unchecked authorities");
        projection.Release();
        Console.WriteLine("[continuation strict frontier] self-test OK (actual byref field, current signatures, exact outer consumption, original catalogs and one observed handoff)");
    }
}
