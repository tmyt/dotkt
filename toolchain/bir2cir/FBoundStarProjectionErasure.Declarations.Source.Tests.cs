using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void CurrentDeclarationPreparationSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Current FBound declaration preparation: " + message); }
        BirFunctionDeclaration Method(string name, string identity = null) => new() {
            Name = name, DeclarationId = identity == null ? default : OptionalField<string>.Of(identity),
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>(),
        };
        BirClassDefinition Generic(string name, OptionalField<string> visibility = default) => new() {
            Kind = "class", Name = name, Vis = visibility, TypeParams = new[] { new BirTypeParameter { Name = "T" } },
        };
        var read = Method("read", "owner-read");
        var top = Method("top", "facade-top");
        var owner = Generic("Owner", "public");
        owner.Base = new TypeNode.Fqn("Parent", new TypeNode[] { new TypeNode.Tv("type", 0) });
        owner.Interfaces = new TypeNode[] { new TypeNode.Fqn("Contract", new TypeNode[] { new TypeNode.Tv("type", 0) }) };
        owner.Methods = new[] { read };
        var privateOwner = Generic("Private", "private");
        var internalOwner = Generic("Internal", "internal");
        var protectedOwner = Generic("Protected", "protected");
        var absentVisibility = Generic("AbsentVisibility");
        var nullVisibility = Generic("NullVisibility", OptionalField<string>.Null);
        var generated = Generic("Generated"); generated.Generated = true;
        var alias = Generic("Alias");
        alias.Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrTypeAlias"), Args = new IBirAttributeValue[] {
            new BirConstant { Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral("System.Collections.Generic.List`1") },
        } } };
        // Shared generated declarations can occur independently in several
        // roots before final module de-duplication. Their shapes agree, while
        // source-definition and hierarchy pointers have different winner rules.
        var shared = new BirClassDefinition { Kind = "class", Name = "Shared", Generated = true,
            Methods = new[] { Method("invoke") } };
        var sharedAgain = (BirClassDefinition)shared.DeepClone();
        var first = new BirFileDeclaration { FileClass = "First", Methods = new[] { top }, Types = new IBirTypeDeclaration[] {
            owner, privateOwner, internalOwner, protectedOwner, absentVisibility, nullVisibility, generated, alias, shared,
        } };
        var second = new BirFileDeclaration { FileClass = "Second", Types = new IBirTypeDeclaration[] { sharedAgain } };
        var bindings = new CurrentDeclarationBindings(new[] { first, second });
        var preparation = PrepareDeclarationContinuation(bindings);
        var hierarchy = bindings.SourceSupertypes;
        var ownerShape = hierarchy["Owner"];
        var ownerBinding = bindings.GetSource(owner);
        var readBinding = bindings.GetSource(read);
        var expectedNames = new[] { "Owner", "Private", "Internal", "Protected", "AbsentVisibility", "NullVisibility" };
        Require(ReferenceEquals(preparation.Bindings, bindings) && ReferenceEquals(preparation.SourceSupertypes, hierarchy)
            && preparation.GenericOwners.Select(row => row.Name).SequenceEqual(expectedNames)
            && preparation.GenericOwners.All(row => row.Arity == 1 && ReferenceEquals(row.Root, bindings.SourceRoots[0]))
            && preparation.GenericOwners.Single(row => row.Name == "Owner").Definition == ownerBinding
            && preparation.GenericOwners.All(row => row.Needed == (row.Name != "Private"))
            && preparation.LocalClrAliases["Alias"] == "System.Collections.Generic.List`1",
            "eligibility, visibility, aliases, declaration occurrence or root association was reselected");
        // Physical proof demand changes live declarations, not the original
        // selection keys, aliases, eligibility or scalar supertype snapshot.
        var ledger = new SourceDeclarationProjection(new[] { first, second }, bindings);
        owner.TypeParams.Value[0].Constraints = new TypeNode[] { new TypeNode.Fqn("System.IDisposable") };
        RoundtripMetadata.PrepareMetadata(first, false, ledger.ReportMetadataIdentityTransfer);
        RoundtripMetadata.PrepareMetadata(second, false, ledger.ReportMetadataIdentityTransfer);
        owner.Name = "PhysicalOwner";
        owner.Base = new TypeNode.Fqn("PhysicalBase");
        read.Name = "physicalRead";
        ((BirConstant)alias.Attrs.Value[0].Args.Value[0]).Value = new BirLiteral.TextLiteral("Physical.Alias.Target");
        var late = Method("late");
        owner.Methods = owner.Methods.Value.Append(late).ToArray();
        bindings.GetSource(late); // Actual producer birth, not original membership.
        ledger.PublishMethods(first, owner, new[] { late });
        ledger.SealSourceContinuations();
        Require(ReferenceEquals(bindings.SourceDefinitions["Owner"].Source, owner)
            && !bindings.SourceDefinitions.ContainsKey("PhysicalOwner")
            && ReferenceEquals(bindings.SourceDefinitions["Shared"].Source, shared)
            && ReferenceEquals(bindings.SourceLocalMethods["Owner\0read"].Single().Source, read)
            && ReferenceEquals(bindings.SourceLocalMethods["First\0top"].Single().Source, top)
            && bindings.SourceLocalMethods["Shared\0invoke"].Count == 2
            && ReferenceEquals(bindings.SourceLocalDeclarations[("Owner", "owner-read")].Source, read)
            && ReferenceEquals(bindings.SourceLocalDeclarations[("First", "facade-top")].Source, top)
            && ledger.WasIdentityConsumed(read, "owner-read")
            && preparation.LocalClrAliases["Alias"] == "System.Collections.Generic.List`1",
            "continuation used current names, consumed IDs, aliases or layout to recollect captured facts");
        Require(ReferenceEquals(preparation.SourceSupertypes, hierarchy)
            && ReferenceEquals(hierarchy["Owner"], ownerShape)
            && ownerShape.Name == "Owner" && ownerShape.Arity == 1
            && ownerShape.Base == new TypeNode.Fqn("Parent", new TypeNode[] { new TypeNode.Tv("type", 0) })
            && ownerShape.Interfaces is [TypeNode.Fqn { Name: "Contract", Args: [TypeNode.Tv { Scope: "type", I: 0 }] }]
            && ReferenceEquals(preparation.GenericOwners[0].Definition.Source, owner)
            && ReferenceEquals(preparation.GenericOwners[0].Root.Source, first),
            "hierarchy scalar shapes, original owner identity or root membership was replaced");
        Require(owner.Methods.Value.Length == 2 && ReferenceEquals(ownerBinding.Source, owner)
            && ((BirFunctionDeclaration)readBinding.Source).Name.Value == "physicalRead"
            && bindings.SourceLocalMethods["Owner\0read"].Count == 1
            && !bindings.SourceLocalMethods.ContainsKey("Owner\0late") && ledger.IsPublished(late),
            "current method facts froze or appended membership was recaptured");
        bindings.CompleteSourceCustody(ledger);
        var released = false;
        try { _ = readBinding.Source; } catch (InvalidOperationException) { released = true; }
        Require(released, "completed custody kept its declaration capability");
        ledger.Release();
        Console.WriteLine("[current FBound declarations] self-test OK (source selection, occurrence handoff, hierarchy clock, method indexes)");
    }
}
