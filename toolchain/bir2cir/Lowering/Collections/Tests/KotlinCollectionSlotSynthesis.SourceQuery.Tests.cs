using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinCollectionSlotSynthesis
{
    static void SourceQueryParameterSelfTest()
    {
        BirFunctionDeclaration Query(string member, string owner, params TypeNode[] parameters) => new() {
            Name = member,
            Params = parameters.Select((type, index) => new BirParameter { Name = "p" + index, Type = type }).ToArray(),
            Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn(owner), Member = member, Kind = "method", Arity = parameters.Length } },
        };
        var variable = new TypeNode.Tv("type", 0);
        var valueVariable = new TypeNode.Tv("type", 1);
        var generic = Query("contains", Collection, variable);
        var typed = Query("contains", Collection, new TypeNode.Fqn("kotlin.Int"));
        var bounded = Query("contains", Collection, variable);
        var nullableBounded = Query("contains", Collection, new TypeNode.Nullable(variable));
        var native = Query("contains", Collection, variable);
        var mapKey = Query("containsKey", Map, variable);
        var mapValue = Query("containsValue", Map, valueVariable);
        var mapGet = Query("get", Map, variable);
        var mapDefault = Query("getOrDefault", Map, variable, valueVariable);
        var mapRemove = Query("remove", MutableMap, variable, valueVariable);
        var mapReplace = Query("replace", MutableMap, variable, valueVariable, valueVariable);
        var captured = Query("contains", Collection, valueVariable);
        var framed = Query("contains", Collection, variable);
        var methodBound = Query("contains", Collection, new TypeNode.Tv("method", 0));
        methodBound.TypeParams = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { new TypeNode.Fqn("Changed") } } };
        methodBound.MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { new TypeNode.Tv("type", 1) },
        });
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Generic", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "E" } }, Methods = new[] { generic, typed } },
            new BirClassDefinition { Name = "Bounded", Kind = "class", TypeParams = new[] { new BirTypeParameter {
                Name = "E", Constraints = new TypeNode[] { new TypeNode.Fqn("Bound") },
            } }, Methods = new[] { bounded, nullableBounded } },
            new BirInterfaceDefinition { Name = "Native", Kind = "interface", TypeParams = new[] { new BirTypeParameter { Name = "E" } }, Methods = new[] { native } },
            new BirClassDefinition { Name = "GenericMap", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "K" }, new BirTypeParameter { Name = "V" } },
                Methods = new[] { mapKey, mapValue, mapGet, mapDefault, mapRemove, mapReplace } },
            new BirClassDefinition { Name = "Captured", Kind = "class", CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } },
                TypeParams = new[] { new BirTypeParameter { Name = "Own", Constraints = new TypeNode[] { new TypeNode.Fqn("InterfaceBound"), new TypeNode.Fqn("Changed") } } },
                KotlinSupertypesPre = new SourceTypeFacts(new Dictionary<int, TypeNode[]> { [1] = new TypeNode[] {
                    new TypeNode.Fqn("InterfaceBound"), new TypeNode.Fqn("Bound"),
                } }), Methods = new[] { captured, methodBound } },
            new BirInterfaceDefinition { Name = "InterfaceBound", Kind = "interface" },
            new BirClassDefinition { Name = "Framed", Kind = "class", TypeParams = new[] {
                    new BirTypeParameter { Name = "Storage", Constraints = new TypeNode[] { new TypeNode.Fqn("Changed") } },
                    new BirTypeParameter { Name = "Source", Constraints = new TypeNode[] { new TypeNode.Fqn("Changed") } },
                }, KotlinSupertypesPre = SourceTypeFacts.ReadFrozenText("""{"bounds":{"0":[{"t":"fqn","name":"Bound"}]},"nullableFrame":{"sourceArity":1,"nullable":[],"storage":[0],"nullableStorage":[],"order":[1,0]}}"""),
                Methods = new[] { framed } },
        } };
        EraseGenericQueryParameters(new[] { root }, null, new Dictionary<string, string> { ["Native"] = "System.Native" });
        bool Erased(BirFunctionDeclaration method, int index, TypeNode source) =>
            method.Params.Value[index].Type.Value is TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Any" } }
            && method.Params.Value[index].KotlinType.Value == source;
        if (!Erased(generic, 0, variable) || typed.Params.Value[0].Type.Value is not TypeNode.Fqn { Name: "kotlin.Int" }
            || bounded.Params.Value[0].Type.Value is not TypeNode.Fqn { Name: "Bound" }
            || nullableBounded.Params.Value[0].Type.Value is not TypeNode.Nullable { Of: TypeNode.Fqn { Name: "Bound" } }
            || native.Params.Value[0].Type.Value != variable)
            throw new InvalidOperationException("Query entry erasure lost source bounds, concrete guards or native signatures");
        if (!Erased(mapKey, 0, variable) || !Erased(mapValue, 0, valueVariable) || !Erased(mapGet, 0, variable)
            || !Erased(mapDefault, 0, variable) || mapDefault.Params.Value[1].Type.Value != valueVariable
            || !Erased(mapRemove, 0, variable) || !Erased(mapRemove, 1, valueVariable)
            || !Erased(mapReplace, 0, variable) || !Erased(mapReplace, 1, valueVariable)
            || mapReplace.Params.Value[2].Type.Value != valueVariable
            || !IsErasedQueryParameter(mapReplace.Overrides.Value, 0) || !IsErasedQueryParameter(mapReplace.Overrides.Value, 1)
            || IsErasedQueryParameter(mapReplace.Overrides.Value, 2) || IsErasedQueryParameter(mapDefault.Overrides.Value, 1))
            throw new InvalidOperationException("Map query erasure changed an input role or lost its source variable");
        if (captured.Params.Value[0].Type.Value is not TypeNode.Fqn { Name: "Bound" }
            || methodBound.Params.Value[0].Type.Value is not TypeNode.Fqn { Name: "Bound" }
            || framed.Params.Value[0].Type.Value is not TypeNode.Fqn { Name: "Bound" }
            || captured.Params.Value[0].KotlinType.Value != valueVariable
            || methodBound.Params.Value[0].KotlinType.Value != new TypeNode.Tv("method", 0))
            throw new InvalidOperationException("Query bound selection lost captured/own frames, saved bounds, recursive variables or class-before-interface choice");
    }
}
