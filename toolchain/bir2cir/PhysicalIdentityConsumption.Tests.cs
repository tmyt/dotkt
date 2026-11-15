using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class PhysicalIdentityConsumptionTests
{
    internal static void SelfTest()
    {
        static void Need(bool value, string message)
        { if (!value) throw new InvalidOperationException("Physical identity consumption: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("An unreported or mismatched identity consumption was accepted");
        }

        foreach (var validator in new[] { "value", "unchecked" })
        foreach (var transport in new[] { "physical", "metadata" })
        foreach (var change in transport == "physical"
            ? new[] { "reported", "missing", "uncleared", "replaced", "continuing-id" }
            : new[] { "reported", "missing", "uncleared", "replaced", "continuing-id",
                "metadata-target-replaced", "metadata-target-replaced-after-seal", "metadata-id-altered" })
        {
            var variable = new TypeNode.Tv("method", 0);
            var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
            var method = new BirFunctionDeclaration { Name = "read", DeclarationId = "shared-selection", Generated = true,
                Static = true, Ret = variable, TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Params = new[] { new BirParameter { Name = "value", Type = any } },
                Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(new BirCast {
                    Type = variable, ReifiedTypeOperand = false,
                    E = OptionalField<IBirExpression>.Of(new BirLocal { Name = "value", Sty = any }) }) } } };
            // Original source membership can contain another occurrence of the
            // same selection ID in a different owner. Consuming one does not
            // consume, reindex or suppress the other's strict identity check.
            var sibling = new BirFunctionDeclaration { Name = "other", DeclarationId = "shared-selection",
                Static = true, Ret = new TypeNode.Fqn("kotlin.Unit"), Params = Array.Empty<BirParameter>(),
                Body = Array.Empty<IBirStatement>() };
            var owner = new BirClassDefinition { Name = "IdentityOwner", Kind = "class", Methods = new[] { method } };
            var other = new BirClassDefinition { Name = "SiblingOwner", Kind = "class", Methods = new[] { sibling } };
            var file = new BirFileDeclaration { FileClass = "IdentityFixture", Types = new IBirTypeDeclaration[] { owner, other } };
            var roots = new[] { file };
            var sourceIndex = ValueRepresentationLowering.CaptureSource(roots);
            Need(sourceIndex.Methods.Count == 2, "the source occurrence census was not captured");
            var values = ValueRepresentationLowering.CreateSource(roots, null, false,
                new ValueRepresentationSourcePolicy(roots, null, false), sourceIndex, null);
            var pending = UncheckedGenericCastReturnErasure.CaptureSource(roots);
            var original = pending.SourceDeclarations.ByIdentity["shared-selection"];
            UncheckedGenericCastReturnErasure.Apply(file, pending);
            var helper = new BirFunctionDeclaration { Name = "helper", DeclarationId = "physical-helper", Generated = true,
                Static = true, Ret = new TypeNode.Fqn("kotlin.Unit"), Params = Array.Empty<BirParameter>(),
                Body = Array.Empty<IBirStatement>() };
            other.Methods = new[] { sibling, helper };
            var bindings = new CurrentDeclarationBindings(roots);
            var originalBinding = bindings.GetCapturedSource(method);
            var selectionKeys = bindings.SourceLocalDeclarations;
            var projection = new SourceDeclarationProjection(roots, bindings);
            Reject(() => projection.ReportPhysicalIdentityConsumption(file, other, method, "shared-selection"));
            Reject(() => projection.ReportPhysicalIdentityConsumption(file, owner, method, "wrong-id"));
            projection.ReportPhysicalIdentityConsumption(file, other, helper, "physical-helper");
            helper.DeclarationId = default;
            BirRoundtripDeclarationIdentity metadata = null;
            if (transport == "metadata")
            {
                metadata = new() { DeclarationId = "shared-selection" };
                Reject(() => projection.ReportMetadataIdentityTransfer(file, owner, method, metadata));
                method.RoundtripDeclarationIdentity = metadata;
                Reject(() => projection.ReportMetadataIdentityTransfer(file, other, method, metadata));
                metadata.DeclarationId = "wrong-id";
                Reject(() => projection.ReportMetadataIdentityTransfer(file, owner, method, metadata));
                metadata.DeclarationId = "shared-selection";
            }
            if (change != "missing")
            {
                void Report()
                {
                    if (metadata == null) projection.ReportPhysicalIdentityConsumption(file, owner, method, "shared-selection");
                    else projection.ReportMetadataIdentityTransfer(file, owner, method, metadata);
                }
                Report();
                Reject(Report);
            }
            if (change != "uncleared") method.DeclarationId = default;
            if (change == "metadata-target-replaced")
                method.RoundtripDeclarationIdentity = (BirRoundtripDeclarationIdentity)metadata.DeepClone();
            if (change == "metadata-id-altered") metadata.DeclarationId = "invented";
            if (change == "replaced") owner.Methods = new[] { (BirFunctionDeclaration)method.DeepClone() };
            void Complete()
            {
                projection.SealSourceContinuations();
                if (change == "continuing-id") method.DeclarationId = "invented";
                if (change == "metadata-target-replaced-after-seal")
                    method.RoundtripDeclarationIdentity = (BirRoundtripDeclarationIdentity)metadata.DeepClone();
                if (validator == "value") values.CompleteSourceContinuation(roots, projection);
                else
                {
                    UncheckedGenericCastReturnErasure.CompleteSourceDeclarations(pending, roots, null, projection);
                    Need(original.SourceMethod == null && original.Return == variable,
                        "terminal completion recaptured the original unchecked return or kept ended source staging");
                }
                Need(ReferenceEquals(selectionKeys[(owner.Name.Value, "shared-selection")], originalBinding)
                    && ReferenceEquals(originalBinding.Source, method)
                    && !method.DeclarationId.IsPresent
                    && sibling.DeclarationId.Value == "shared-selection"
                    && projection.WasIdentityConsumed(helper, "physical-helper")
                    && (metadata == null || ReferenceEquals(method.RoundtripDeclarationIdentity.Value, metadata)),
                    "consumption replaced original keys, sibling identity, metadata target or helper occurrence");
                bindings.CompleteSourceCustody(projection);
                Reject(() => { _ = originalBinding.Source; });
                projection.Release();
            }
            if (change != "reported") Reject(Complete);
            else Complete();
        }
        Console.WriteLine("[physical identity consumption] self-test OK (exact allocation/metadata custody, source keys, sibling counts, helpers and strict value/unchecked handoff)");
    }
}
