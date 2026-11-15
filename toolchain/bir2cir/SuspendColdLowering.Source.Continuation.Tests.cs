using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal static void SourceContinuationSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Suspend declaration continuation: " + message); }
        static void Reject(Action action, string message)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Suspend declaration continuation accepted " + message);
        }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var any = new TypeNode.Fqn("kotlin.Any");
        var variable = new TypeNode.Tv("method", 0);
        var native = new TypeNode.Fqn("System.IDisposable");
        var ownerDependent = new TypeNode.Fqn(typeof(IEnumerable<>).FullName,
            new TypeNode[] { new TypeNode.Tv("type", 0) });

        (BirFileDeclaration File, BirClassDefinition Owner, BirFunctionDeclaration Method,
            BirFunctionDeclaration Keeper, BirNewSuspendLambda Donor, GenericRepresentationPolicy Generic,
            ValueRepresentationLowering Values, UncheckedGenericCastReturnErasure.PendingSourceIndex Unchecked,
            CurrentDeclarationBindings Bindings, FBoundStarProjectionErasure.NativeProofPlan Proofs,
            SourcePipelineTransitions Transitions) Fixture()
        {
            var donor = new BirNewSuspendLambda { Arity = 0, Params = Array.Empty<BirLambdaParameter>(),
                Captures = Array.Empty<BirCaptureDescriptor>(), CapValues = Array.Empty<IBirExpression>(),
                TypeParams = new[] { new BirTypeParameter { Name = "M" } },
                TypeArgs = new TypeNode[] { variable }, SuspendRet = any, Body = Array.Empty<IBirStatement>() };
            var method = new BirFunctionDeclaration { Name = "read", DeclarationId = "read-source", Static = false,
                Params = Array.Empty<BirParameter>(), TypeParams = new[] { new BirTypeParameter { Name = "M" } },
                Ret = variable, Mods = new BirModifiers { Suspend = true }, SuspendRet = variable, SuspendResult = variable,
                Body = new IBirStatement[] {
                    new BirExpressionStatement { Expr = donor },
                    new BirReturn { Value = new BirCast { Type = variable, ReifiedTypeOperand = false,
                        E = new BirConstant { Type = new TypeNode.Nullable(any), Value = new BirLiteral.NullLiteral() } } },
                } };
            var keeper = new BirFunctionDeclaration { Name = "keep", DeclarationId = "keep-source", Static = false,
                Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() };
            var owner = new BirClassDefinition { Name = "ContinuationOwner", Kind = "class",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { method, keeper },
                Fields = new[] { new BirFieldDeclaration { Name = "native", Type = new TypeNode.Tv("type", 0),
                    Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } } } };
            var file = new BirFileDeclaration { FileClass = "ContinuationFile", Methods = Array.Empty<BirFunctionDeclaration>(),
                Types = new IBirTypeDeclaration[] { owner } };
            var roots = new[] { file };
            var generic = GenericRepresentationPolicy.CreateSource(roots, new Dictionary<string, string>(), references);
            var values = ValueRepresentationLowering.CreateSource(roots, references, false,
                new ValueRepresentationSourcePolicy(roots, references, false),
                ValueRepresentationLowering.CaptureSource(roots), generic);
            var pending = UncheckedGenericCastReturnErasure.CaptureSource(roots);
            Require(pending.SourceDeclarations.ByIdentity.ContainsKey("read-source"), "fixture did not use the actual unchecked source selector");
            pending.EraseOwnedReturn(pending.SourceDeclarations.ByIdentity["read-source"]);
            generic.PrepareSourceLocations(roots); generic.RefreshPhysicalNativeLocations(roots);
            var bindings = new CurrentDeclarationBindings(roots);
            var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, references, generic);
            proofs.Add(bindings.GetCapturedSource(method), 0, native);
            proofs.Add(bindings.GetCapturedSource(donor), 0, ownerDependent);
            var transitions = new SourcePipelineTransitions(roots, bindings, proofs, generic, null,
                values.RetireSourceDeclaration, Array.Empty<BirFunctionDeclaration>());
            return (file, owner, method, keeper, donor, generic, values, pending, bindings, proofs, transitions);
        }

        var continuing = Fixture();
        var originalDonor = continuing.Bindings.GetCapturedSource(continuing.Donor);
        var copy = (BirNewSuspendLambda)continuing.Transitions.CloneIndependent(continuing.Donor);
        var copyBinding = continuing.Bindings.GetCapturedSource(copy);
        Require(!ReferenceEquals(originalDonor, copyBinding)
            && continuing.Proofs.Bounds(copyBinding, 0).Contains(ownerDependent)
            && !ReferenceEquals(((TypeNode.Fqn)continuing.Proofs.Bounds(copyBinding, 0).Single()).Args,
                ((TypeNode.Fqn)continuing.Proofs.Bounds(originalDonor, 0).Single()).Args),
            "exact same-frame birth filtered owner proofs or shared their nested vectors");
        continuing.Keeper.Body = new IBirStatement[] { new BirExpressionStatement { Expr = copy } };
        // These overlapping reports deliberately share one containing retired
        // method; the donor must have exactly one retired owning occurrence.
        continuing.Transitions.RetireDiscardedSubtree(continuing.Donor);
        continuing.Transitions.RetireMethod(continuing.File, continuing.Owner, continuing.Method);
        var projection = new BirFunctionDeclaration { Name = "read", DeclarationId = "read-source", Static = false,
            Params = Array.Empty<BirParameter>(), TypeParams = new[] { new BirTypeParameter { Name = "M" } },
            Ret = new TypeNode.Fqn(TaskBclFqn, new TypeNode[] { any }), Body = Array.Empty<IBirStatement>() };
        continuing.Owner.Methods = new[] { continuing.Keeper, projection };
        continuing.Transitions.ProjectMethod(continuing.Method, projection);
        continuing.Transitions.PublishMethods(continuing.File, continuing.Owner, new[] { projection });
        Require(ReferenceEquals(continuing.Proofs.LocalSourceMethodBinding("read-source"),
            continuing.Bindings.GetCapturedSource(continuing.Method))
            && continuing.Proofs.Bounds(continuing.Bindings.GetCapturedSource(projection), 0).Contains(native),
            "physical projection replaced source selection or lost its exact native rows");
        var continuingFacts = continuing.Unchecked.SourceDeclarations.ByIdentity["read-source"];
        continuing.Transitions.Projection.SealSourceContinuations();
        Require(continuing.Transitions.Projection.IsRetired(continuing.Donor)
            && ReferenceEquals(originalDonor.Source, continuing.Donor),
            "nested retired donor lost its actual containing forest");
        continuing.Generic.CompleteSourceContinuation(new[] { continuing.File }, null, continuing.Transitions.Projection);
        continuing.Values.CompleteSourceContinuation(new[] { continuing.File }, null, continuing.Transitions.Projection);
        UncheckedGenericCastReturnErasure.CompleteSourceDeclarations(continuing.Unchecked, new[] { continuing.File },
            null, continuing.Transitions.Projection);
        Require(continuingFacts.SourceMethod == null && continuingFacts.Return == variable
            && ReferenceEquals(continuing.Proofs.LocalSourceMethodBinding("read-source"),
                continuing.Bindings.GetCapturedSource(continuing.Method))
            && ReferenceEquals(continuing.Bindings.GetCapturedSource(continuing.Donor), originalDonor)
            && continuing.Transitions.Projection.IsPublished(projection),
            "late buyers selected the new same-ID Task method or reconstructed a donor");
        continuing.Bindings.CompleteSourceCustody(continuing.Transitions.Projection);
        Reject(() => { _ = originalDonor.Source; }, "source binding after terminal completion");
        continuing.Transitions.Release();

        var retiredType = Fixture();
        retiredType.Transitions.RetireTypes(retiredType.File, new IBirTypeDeclaration[] { retiredType.Owner });
        retiredType.File.Types = Array.Empty<IBirTypeDeclaration>();
        var retiredFacts = retiredType.Unchecked.SourceDeclarations.ByIdentity["read-source"];
        retiredType.Transitions.Projection.SealSourceContinuations();
        Require(retiredType.Transitions.Projection.IsRetired(retiredType.Method)
            && ReferenceEquals(retiredType.Owner.Methods.Value[0], retiredType.Method),
            "retired type did not retain its actual member ownership");
        retiredType.Generic.CompleteSourceContinuation(new[] { retiredType.File }, null, retiredType.Transitions.Projection);
        retiredType.Values.CompleteSourceContinuation(new[] { retiredType.File }, null, retiredType.Transitions.Projection);
        UncheckedGenericCastReturnErasure.CompleteSourceDeclarations(retiredType.Unchecked, new[] { retiredType.File },
            null, retiredType.Transitions.Projection);
        Require(retiredFacts.SourceMethod == null && retiredFacts.Return == variable
            && retiredType.Generic.IsNativeField(retiredType.Owner.Fields.Value[0])
            && retiredType.Transitions.Projection.IsRetired(retiredType.Owner.Fields.Value[0]),
            "type retirement lost the original method/native field authority");
        retiredType.Bindings.CompleteSourceCustody(retiredType.Transitions.Projection);
        retiredType.Transitions.Release();

        var stillLive = Fixture();
        stillLive.Transitions.RetireTypes(stillLive.File, new IBirTypeDeclaration[] { stillLive.Owner });
        Reject(stillLive.Transitions.Projection.SealSourceContinuations, "retired owner still live");
        var missing = Fixture(); missing.File.Types = Array.Empty<IBirTypeDeclaration>();
        Reject(missing.Transitions.Projection.SealSourceContinuations, "unreported owner loss");
        var duplicate = Fixture();
        Reject(() => duplicate.Transitions.RetireTypes(duplicate.File, new IBirTypeDeclaration[] { duplicate.Owner, duplicate.Owner }),
            "duplicate retirement");
        var lostCopy = Fixture();
        lostCopy.Owner.Methods = new[] { (BirFunctionDeclaration)lostCopy.Method.DeepClone(), lostCopy.Keeper };
        Reject(lostCopy.Transitions.Projection.SealSourceContinuations,
            "unreported same-ID copy replacing a captured declaration");
        Require(ReferenceEquals(lostCopy.Bindings.GetCapturedSource(lostCopy.Method).Source, lostCopy.Method),
            "failed seal consumed its captured source authority");
        var wrongParent = Fixture();
        var foreignOwner = new BirClassDefinition { Name = "OtherOwner", Methods = new[] { projection } };
        wrongParent.File.Types = wrongParent.File.Types.Value.Append(foreignOwner).ToArray();
        Reject(() => wrongParent.Transitions.ProjectMethod(wrongParent.Method, projection), "projection attached to a different owner");
        Console.WriteLine("[suspend source continuation] self-test OK (exact births/proofs, original catalogs, retired forests, source/native/value/unchecked buyers and strict loss guards)");
    }
}
