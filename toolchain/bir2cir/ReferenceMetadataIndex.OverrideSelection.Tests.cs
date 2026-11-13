using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
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
        var metadataParameters = new JsonArray(new JsonObject {
            ["name"] = "T", ["constraints"] = new JsonArray(TypeJson.Write(ownerVariable)),
        });
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
            || index.TrySelectedNullableGenericSlotByShape("ShapeOwner", "get_physical", false, 0, 1,
                Array.Empty<TypeNode>(), result, declaringArgs, "0",
                out _, out _, out _, out _, out _, out _, out _, semanticConstraints: true,
                selectedPhysicalMember: "get_physical", selectedDeclarationId: "shape-selected"))
            throw new InvalidOperationException("Typed nullable slot selection bypassed selected constraints or changed the referenced declaration frame");
        parameters[0].Constraints.Value[0] = result;
        if (ReadReferencedTypeParameters(declaration.TypeParams)[0].Constraints.Value[0] != ownerVariable)
            throw new InvalidOperationException("Typed metadata selection returned a mutable alias of its declaration cache");
        Console.WriteLine("[source override metadata selection] self-test OK (accessor, identity, shape, owner frames, independent parameters)");
    }
}
