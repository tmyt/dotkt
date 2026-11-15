using System.Text.Json.Nodes;
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
            ? new[] { "reported", "missing", "uncleared", "replaced", "projected-id" }
            : new[] { "reported", "missing", "uncleared", "replaced", "projected-id",
                "metadata-target-replaced", "metadata-projection-replaced", "metadata-id-altered" })
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
            var dom = file.ToDom(projection.ObserveProjection);
            projection.ProjectRetiredForests();
            if (change == "projected-id") projection.Get(method)[DeclarationIdentityBinding.Key] = "invented";
            if (change == "metadata-projection-replaced")
                projection.Get(method)["roundtripDeclarationIdentity"] =
                    projection.Get(method)["roundtripDeclarationIdentity"].DeepClone();
            void Bind()
            {
                if (validator == "value") values.BindDomDeclarations(new JsonNode[] { dom }, projection.RetiredMethods, projection);
                else
                {
                    var continued = UncheckedGenericCastReturnErasure.BindDomDeclarations(pending,
                        new[] { dom }, projection.RetiredMethods, declarations: projection);
                    Need(ReferenceEquals(continued.ByIdentity["shared-selection"], original)
                        && ReferenceEquals(original.Method, projection.Get(method)) && original.Return == variable,
                        "the original unchecked selection row or return was recaptured");
                }
            }
            if (change != "reported") Reject(Bind);
            else
            {
                bindings.BindProjected();
                Need(ReferenceEquals(selectionKeys[(owner.Name.Value, "shared-selection")], originalBinding)
                    && ReferenceEquals(originalBinding.Physical, projection.Get(method))
                    && !originalBinding.Physical.ContainsKey(DeclarationIdentityBinding.Key)
                    && sibling.DeclarationId.Value == "shared-selection"
                    && projection.WasIdentityConsumed(helper, "physical-helper"),
                    "consumption replaced original keys, a sibling identity or an uncaptured helper occurrence");
                Bind();
                projection.Release();
            }
        }
        Console.WriteLine("[physical identity consumption] self-test OK (exact allocation/metadata custody, source keys, sibling counts, helpers and strict value/unchecked handoff)");
    }
}
