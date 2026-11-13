using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class InheritedClassInterfaceBridge
{
    internal static void SourceSelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var sourceParameters = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { ownerVariable } } };
        var original = new BirFunctionDeclaration {
            Name = "read", DeclarationId = "source-read", Static = false, Virtual = false,
            Params = new[] { new BirParameter { Name = "input", Type = methodVariable } },
            Ret = text, TypeParams = sourceParameters, Mods = new BirModifiers { Suspend = true },
        };
        var fact = new BirInheritedClassMethod {
            Member = "read", Params = new[] { new BirParameter { Name = "input", Type = methodVariable } },
            Ret = text, Mods = new BirModifiers { Suspend = true },
            TypeParams = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { text } } },
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { text } }),
            InheritedImplementation = new BirInheritedImplementation {
                Owner = new TypeNode.Fqn("Base"), Member = "read", Sig = new TypeNode[] { methodVariable },
                TypeParams = sourceParameters,
            },
            Overrides = new[] {
                new BirOverride { Owner = new TypeNode.Fqn("Base"), Member = "read", DeclarationId = "source-read" },
                new BirOverride { Owner = new TypeNode.Fqn("Contract"), Member = "read", DeclarationId = "contract-read" },
            },
        };
        var derived = new BirClassDefinition {
            Name = "Derived", Kind = "class", Base = new TypeNode.Fqn("Base", new TypeNode[] { text }),
            Interfaces = new TypeNode[] { new TypeNode.Fqn("Contract") },
            Methods = Array.Empty<BirFunctionDeclaration>(), InheritedClassMethods = new[] { fact },
        };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Base", Kind = "class", Methods = new[] { original },
                TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
            new BirInterfaceDefinition { Name = "Contract", Kind = "interface", Methods = Array.Empty<BirFunctionDeclaration>() },
            derived,
        } };
        MaterializeSuspendDeclarations(new[] { file }, null);
        var bridge = derived.Methods.Value.Single();
        if (bridge.Name.Value != "read" || !bridge.Virtual.Value || bridge.Static.Value || bridge.Override.Value
            || bridge.Abstract.Value || bridge.Vis.Value != "public" || bridge.SuspendRet.Value != text
            || bridge.DeclarationId.Value != DeclarationIdentityBinding.InheritedProjectionId("Derived", new[] { "source-read" })
            || !bridge.Generated.Value || bridge.DeclarationSourceName.Value != "read"
            || bridge.Overrides.Value.Single().DeclarationId.Value != "contract-read"
            || bridge.Body.Value.Single() is not BirReturn { Value.HasValue: true } returned
            || returned.Value.Value is not BirCallInstance call || call.Method.Value != "read"
            || call.DeclarationId.Value != "source-read" || !call.SuspendCall.Value || !call.ClrOwnerResolved.Value
            || call.OwnerType.Value != new TypeNode.Fqn("Base", new TypeNode[] { text })
            || call.TypeArgs.Value.Single() != methodVariable || call.Sig.Value.Single() != methodVariable
            || call.Args.Value.Single() is not BirLocal { Name.Value: "input" }
            || bridge.MethodTypeParameterBoundsPre.Value.Bounds[0].Single() != text)
            throw new InvalidOperationException("Owning inherited suspend materialization changed the selected slot, source facts or generic frames");
        bridge.Params.Value[0].Name = "changed";
        bridge.Overrides.Value[0].Member = "changed";
        if (fact.Params.Value[0].Name.Value != "input" || fact.Overrides.Value[1].Member.Value != "read")
            throw new InvalidOperationException("Inherited bridge and retained source fact share mutable occurrences");

        original.Virtual = true;
        derived.Methods = Array.Empty<BirFunctionDeclaration>();
        MaterializeSuspendDeclarations(new[] { file }, null);
        if (derived.Methods.Value.Length != 0)
            throw new InvalidOperationException("An already-virtual inherited target acquired an unnecessary suspend bridge");

        var sourceShape = KotlinOverrideSlotBridge.SourceMethodTypeParameterShapeKey(sourceParameters, new TypeNode[] { text });
        var domParameters = new System.Text.Json.Nodes.JsonArray(sourceParameters.Select(parameter =>
            (System.Text.Json.Nodes.JsonNode)parameter.ToDom()).ToArray());
        if (sourceShape != KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(domParameters, new TypeNode[] { text })
            || KotlinOverrideSlotBridge.SameSourceMethodTypeParameterShape(sourceParameters,
                new[] { new BirTypeParameter { Name = "Other", Constraints = new TypeNode[] { new TypeNode.Fqn("OtherBound") } } },
                new TypeNode[] { text }, new TypeNode[] { text }))
            throw new InvalidOperationException("Owning generic declaration shape disagrees with the shared constraint policy");
        Console.WriteLine("[owning inherited suspend] self-test OK (selected nonvirtual slot, constraints, source identity, frames, independent occurrences, virtual suppression)");
    }
}
