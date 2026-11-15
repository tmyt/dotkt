using System;
using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ReferenceMetadataIndex
{
    static void SelectedSourceConstraintShapeSelfTest()
    {
        var index = Build(Array.Empty<string>());
        var bound = new TypeNode.Fqn("Bound");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var result = new TypeNode.Fqn("object");
        var metadataParameters = new[] { new BirTypeParameter {
            Name = "T", Constraints = new TypeNode[] { ownerVariable },
        } };
        index._membersByOwner["ShapeOwner"] = new List<MemberBinding> {
            new("ShapeOwner", "get_physical", 0, null, false, false, MethodArity: 1,
                ParamTypeNodes: Array.Empty<TypeNode>(), ReturnTypeNode: result,
                KotlinReturnType: new TypeNode.Tv("method", 0),
                SourcePropertyName: "value", AccessorKind: "get", DeclarationId: "shape-selected",
                MethodTypeParams: metadataParameters, SemanticMethodTypeParams: metadataParameters),
        };
        var selected = new[] { new BirTypeParameter {
            Name = "T", Constraints = new TypeNode[] { new TypeNode.Tv("type", 1) },
        } };
        var declaringArgs = new TypeNode[] { bound };
        var implementationArgs = new TypeNode[] { new TypeNode.Fqn("Other"), bound };
        if (!index.TrySelectedSourceOverrideDeclaration("ShapeOwner", "value", 1,
                Array.Empty<TypeNode>(), declaringArgs, selected, implementationArgs, false,
                out var declaration, accessorKind: "get", selectedDeclarationId: "shape-selected")
            || declaration.PhysicalMember != "get_physical"
            || index.TrySelectedSourceOverrideDeclaration("ShapeOwner", "value", 1,
                Array.Empty<TypeNode>(), declaringArgs, selected, implementationArgs, false,
                out _, accessorKind: "set", selectedDeclarationId: "shape-selected")
            || index.TrySelectedSourceOverrideDeclaration("ShapeOwner", "value", 1,
                Array.Empty<TypeNode>(), declaringArgs, selected, implementationArgs, false,
                out _, accessorKind: "get", selectedDeclarationId: "not-selected"))
            throw new InvalidOperationException("Typed override selection lost accessor, exact identity, or separate owner constraint frames");

        var selectedShape = KotlinOverrideSlotBridge.SourceMethodTypeParameterShapeKey(selected, implementationArgs);
        if (!index.TrySelectedNullableGenericSlotByShape("ShapeOwner", "get_physical", false, 0, 1,
                Array.Empty<TypeNode>(), result, declaringArgs, selectedShape,
                out var returned, out _, out _, out var physical, out var parameters, out _, out _,
                semanticConstraints: true, selectedPhysicalMember: "get_physical", selectedDeclarationId: "shape-selected")
            || physical != "get_physical" || returned != result || parameters.Length != 1
            || parameters[0].Constraints.Value[0] != ownerVariable
            || !index.TrySelectedNullableGenericSlotByShape("ShapeOwner", "get_physical", false, 0, 1,
                Array.Empty<TypeNode>(), result, declaringArgs, null,
                out _, out _, out _, out _, out _, out _, out _, semanticConstraints: true,
                selectedPhysicalMember: "get_physical", selectedDeclarationId: "shape-selected")
            || index.TrySelectedNullableGenericSlotByShape("ShapeOwner", "get_physical", false, 0, 1,
                Array.Empty<TypeNode>(), result, declaringArgs, "0",
                out _, out _, out _, out _, out _, out _, out _, semanticConstraints: true,
                selectedPhysicalMember: "get_physical", selectedDeclarationId: "shape-selected"))
            throw new InvalidOperationException("Typed nullable slot selection bypassed selected constraints or changed the referenced declaration frame");
        parameters[0].Constraints.Value[0] = result;
        if (declaration.TypeParams[0].Constraints.Value[0] != ownerVariable
            || metadataParameters[0].Constraints.Value[0] != ownerVariable)
            throw new InvalidOperationException("Typed metadata selection returned a mutable alias of its declaration cache");
        var presence = new[] {
            new BirTypeParameter { Name = "Absent" },
            new BirTypeParameter { Name = "Null", Constraints = OptionalField<TypeNode[]>.Null,
                SpecialConstraints = OptionalField<string[]>.Null, Reified = false, Variance = "out",
                ErasedInnerConstraints = new TypeNode[] { ownerVariable } },
            new BirTypeParameter { Name = "Empty", Constraints = Array.Empty<TypeNode>(),
                SpecialConstraints = Array.Empty<string>() },
        };
        var copy = CloneReferencedTypeParameters(presence);
        if (CloneReferencedTypeParameters(null) != null || CloneReferencedTypeParameters(Array.Empty<BirTypeParameter>()).Length != 0
            || copy[0].Constraints.IsPresent || copy[0].SpecialConstraints.IsPresent
            || !copy[1].Constraints.IsNull || !copy[1].SpecialConstraints.IsNull || !copy[1].Reified.HasValue
            || copy[1].Reified.Value || copy[1].Variance.Value != "out"
            || copy[1].ErasedInnerConstraints.Value.Single() != ownerVariable
            || !copy[2].Constraints.HasValue || copy[2].Constraints.Value.Length != 0
            || !copy[2].SpecialConstraints.HasValue || copy[2].SpecialConstraints.Value.Length != 0)
            throw new InvalidOperationException("Selected reference descriptors changed vector or optional-field presence");
        copy[1].ErasedInnerConstraints.Value[0] = result;
        if (presence[1].ErasedInnerConstraints.Value[0] != ownerVariable)
            throw new InvalidOperationException("Selected reference descriptor copies share erased-inner bounds");
        Console.WriteLine("[source override metadata selection] self-test OK (accessor, identity, shape, owner frames, independent parameters)");
    }
}
