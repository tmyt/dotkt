using System.Text.Json.Nodes;
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
        var premature = false;
        try { preparation.BindDefinitions(); } catch (InvalidOperationException) { premature = true; }
        Require(premature && ownerShape.Node == null && ownerShape.Methods == null,
            "a DOM capability was fabricated before the owning frontier");

        // Physical proof demand may add constraints to the same declaration;
        // it does not replace this captured membership or supertype snapshot.
        owner.TypeParams.Value[0].Constraints = new TypeNode[] { new TypeNode.Fqn("System.IDisposable") };
        var firstDom = first.ToDom(bindings.ObserveProjection);
        var secondDom = second.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        var liveOwner = ownerBinding.Physical;
        liveOwner["name"] = "PhysicalOwner";
        liveOwner["base"] = TypeJson.Fqn("PhysicalBase");
        readBinding.Physical["name"] = "physicalRead";
        readBinding.Physical[DeclarationIdentityBinding.Key] = "physical-read";
        var liveAlias = bindings.SourceDefinitions["Alias"].Physical;
        liveAlias["attrs"][0]["args"][0]["value"] = "Physical.Alias.Target";
        var definitions = preparation.BindDefinitions();
        var methods = preparation.BindLocalMethods();
        var declarations = preparation.BindLocalDeclarations();
        var boundHierarchy = preparation.BindSourceSupertypes();
        Require(ReferenceEquals(definitions["Owner"], liveOwner) && !definitions.ContainsKey("PhysicalOwner")
            && ReferenceEquals(definitions["Shared"], firstDom["types"][8])
            && ReferenceEquals(methods["Owner\0read"].Single(), readBinding.Physical)
            && ReferenceEquals(methods["First\0top"].Single(), firstDom["methods"][0])
            && methods["Shared\0invoke"].Count == 2
            && ReferenceEquals(declarations[("Owner", "owner-read")], readBinding.Physical)
            && ReferenceEquals(declarations[("First", "facade-top")], firstDom["methods"][0])
            && preparation.LocalClrAliases["Alias"] == "System.Collections.Generic.List`1",
            "DOM continuation used names, identities, aliases or layout to recollect captured facts");
        Require(ReferenceEquals(boundHierarchy, hierarchy) && ReferenceEquals(boundHierarchy["Owner"], ownerShape)
            && ReferenceEquals(ownerShape.Node, liveOwner) && ReferenceEquals(ownerShape.Methods, liveOwner["methods"])
            && ownerShape.Name == "Owner" && ownerShape.Arity == 1
            && ownerShape.Base == new TypeNode.Fqn("Parent", new TypeNode[] { new TypeNode.Tv("type", 0) })
            && ownerShape.Interfaces is [TypeNode.Fqn { Name: "Contract", Args: [TypeNode.Tv { Scope: "type", I: 0 }] }]
            && ReferenceEquals(boundHierarchy["Shared"].Node, secondDom["types"][0])
            && ReferenceEquals(preparation.GenericOwners[0].Definition.Physical, liveOwner)
            && ReferenceEquals(preparation.GenericOwners[0].Root.Physical, firstDom),
            "hierarchy source shapes, occurrence winner or actual mutable capabilities were replaced");
        ((JsonArray)liveOwner["methods"]).Add(new JsonObject { ["name"] = "late" });
        Require(ownerShape.Methods.Count == 2 && methods["Owner\0read"].Count == 1
            && !preparation.BindLocalMethods().ContainsKey("Owner\0late"),
            "live method capability was frozen or captured method membership was recollected");
        Console.WriteLine("[current FBound declarations] self-test OK (source selection, occurrence handoff, hierarchy clock, method indexes)");
    }
}
