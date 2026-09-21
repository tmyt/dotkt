using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A read-only Kotlin collection must inhabit the invariant collection storage face without acquiring
// Kotlin mutability or losing object identity. A compiler-owned interface supplies explicit CLR default
// implementations; read operations dispatch through the original read-only face, mutation is unsupported.
static class ReadOnlyCollectionStorageSynthesis
{
    internal const string CarrierName = "DotKt.Runtime.CompilerServices.dotkt$ReadOnlyCollectionStorage";
    const string ListCarrierName = "DotKt.Runtime.CompilerServices.dotkt$ReadOnlyListStorage";
    static readonly TypeNode Element = new TypeNode.Tv("type", 0);
    static readonly TypeNode Void = new TypeNode.Fqn("void");
    static readonly TypeNode Bool = new TypeNode.Fqn("System.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("System.Int32");
    static TypeNode.Fqn Face(string name, TypeNode element) => new(name, new[] { element });
    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static JsonObject Self() => new() { ["k"] = "this" };
    static JsonObject Return(JsonNode value) => new() { ["k"] = "return", ["value"] = value };

    internal static IReadOnlyList<string> ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var definitions = SupertypeGraph.Collect(roots);
        var used = false;
        var listUsed = false;
        foreach (var definition in definitions.Values)
        {
            if (definition.Kind != "class") continue;
            var reached = SupertypeGraph.Reachable(definition, definitions, refs).Select(edge => edge.spec).ToArray();
            var mapStorage = refs.KotlinMapStorageDefinitions();
            var storageRoots = reached.Where(type =>
                ReferenceMetadataIndex.BareOwnerFqn(type.Name) is "System.Collections.Generic.IDictionary"
                    or "System.Collections.Generic.IReadOnlyDictionary"
                || mapStorage.Any(name => type.Name == name
                    || type.Name + "`" + (type.Args?.Length ?? 0) == name)).ToArray();
            bool StorageOwns(TypeNode.Fqn face) => storageRoots.Any(root =>
                SupertypeGraph.Reaches(root, face, definitions, refs));
            var independentRoots = reached.Where(type =>
                (IsFace(type, CollectionViewFaces.IReadOnlyList) || IsFace(type, CollectionViewFaces.IList)
                    || IsFace(type, "System.Collections.Generic.IReadOnlySet")
                    || IsFace(type, "System.Collections.Generic.ISet")) && !StorageOwns(type)).ToArray();
            bool StorageOnly(TypeNode.Fqn face) => StorageOwns(face)
                && !independentRoots.Any(root => SupertypeGraph.Reaches(root, face, definitions, refs));
            // Dictionary entry storage is not an authored Kotlin Collection. Giving it a mutable
            // storage carrier invents a second Count closure on an otherwise valid Kotlin subclass.
            var lists = reached.Where(type => IsFace(type, CollectionViewFaces.IReadOnlyList)
                && !StorageOnly(type)
                && !reached.Any(other => IsFace(other, CollectionViewFaces.IList)
                    && other.Args[0] == type.Args[0])).ToArray();
            foreach (var list in lists)
            {
                AddInterface(definition.Node, Face(ListCarrierName, list.Args[0]));
                used = listUsed = true;
            }
            foreach (var readOnly in reached.Where(type => IsFace(type, CollectionViewFaces.IReadOnlyCollection)))
            {
                if (StorageOnly(readOnly)) continue;
                var element = readOnly.Args[0];
                if (lists.Any(list => list.Args[0] == element)) continue;
                if (reached.Any(type => IsFace(type, CollectionViewFaces.ICollection)
                        && type.Args[0] == element)) continue;
                AddInterface(definition.Node, Face(CarrierName, element));
                used = true;
            }
        }
        if (!used) return Array.Empty<string>();
        var host = roots.OfType<JsonObject>().First();
        if (host["types"] is not JsonArray types) host["types"] = types = new JsonArray();
        types.Add(Carrier(refs));
        if (listUsed) types.Add(ListCarrier(refs));
        return listUsed ? new[] { CarrierName, ListCarrierName } : new[] { CarrierName };
    }

    static void AddInterface(JsonObject definition, TypeNode.Fqn face)
    {
        var interfaces = definition["interfaces"] as JsonArray;
        if (interfaces == null) definition["interfaces"] = interfaces = new JsonArray();
        if (!interfaces.Any(node => TypeJson.Read(node) == face)) interfaces.Add(TypeJson.Write(face));
    }

    static bool IsFace(TypeNode.Fqn type, string name) => type.Args is { Length: 1 }
        && (type.Name == name || type.Name == name + "`1");

    static JsonObject Parameter(string name, TypeNode type) => new() { ["name"] = name, ["type"] = TypeJson.Write(type) };

    static JsonObject Method(string slot, TypeNode result, JsonArray parameters, JsonArray body,
        string face = CollectionViewFaces.ICollection)
    {
        return new JsonObject {
            ["name"] = "dotkt$storage$" + slot, ["static"] = false, ["virtual"] = true,
            ["abstract"] = false, ["override"] = false, ["vis"] = "private", ["generated"] = true,
            [KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey] = true,
            ["params"] = parameters, ["ret"] = TypeJson.Write(result), ["body"] = body,
            ["clrInterfaceImpls"] = new JsonArray(new JsonObject {
                ["owner"] = TypeJson.Write(Face(face, Element)),
                ["member"] = slot, ["arity"] = 0,
                ["params"] = new JsonArray(parameters.OfType<JsonObject>().Select(p => p["type"].DeepClone()).ToArray()),
                ["ret"] = TypeJson.Write(result),
            }),
        };
    }

    static JsonArray Unsupported() => new(new JsonObject {
        ["k"] = "throw", ["value"] = new JsonObject {
            ["k"] = "new", ["type"] = TypeJson.Fqn("System.NotSupportedException"),
            ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
        },
    });

    static JsonObject ReadOnlySelf(string face = CollectionViewFaces.IReadOnlyCollection) => new() {
        ["k"] = "cast", ["type"] = TypeJson.Write(Face(face, Element)),
        ["e"] = Self(),
    };

    static JsonObject Carrier(ReferenceMetadataIndex refs)
    {
        var copy = CollectionBclSlotSynthesis.CopyTo(TypeJson.Write(Element));
        var methods = new JsonArray {
            Method("get_Count", Int, new JsonArray(), new JsonArray(Return(new JsonObject {
                ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(Face(CollectionViewFaces.IReadOnlyCollection, Element)),
                ["virtual"] = true, ["recv"] = ReadOnlySelf(), ["method"] = "get_Count",
                ["sig"] = new JsonArray(), ["ret"] = TypeJson.Write(Int), ["args"] = new JsonArray(),
            }))),
            Method("get_IsReadOnly", Bool, new JsonArray(), new JsonArray(Return(new JsonObject {
                ["k"] = "const", ["type"] = TypeJson.Write(Bool), ["value"] = true,
            }))),
            Method("Contains", Bool, new JsonArray(Parameter("element", Element)), new JsonArray(Return(
                StorageHelper(refs, "clrCollContains", "kotlin.collections.Collection", Bool, ReadOnlySelf())))),
            Method("CopyTo", Void, (JsonArray)copy["params"].DeepClone(), (JsonArray)copy["body"].DeepClone()),
            Method("Add", Void, new JsonArray(Parameter("element", Element)), Unsupported()),
            Method("Remove", Bool, new JsonArray(Parameter("element", Element)), Unsupported()),
            Method("Clear", Void, new JsonArray(), Unsupported()),
        };
        return new JsonObject {
            ["name"] = CarrierName, ["kind"] = "interface", ["vis"] = "internal", ["generated"] = true,
            ["typeParams"] = new JsonArray("T"),
            ["base"] = null, ["fields"] = new JsonArray(), ["ctors"] = new JsonArray(),
            ["interfaces"] = new JsonArray(TypeJson.Write(Face(CollectionViewFaces.ICollection, Element)),
                TypeJson.Write(Face(CollectionViewFaces.IReadOnlyCollection, Element))),
            ["methods"] = methods,
        };
    }

    static JsonObject StorageHelper(ReferenceMetadataIndex refs, string name, string sourceCollection,
        TypeNode result, JsonObject receiver)
    {
        const string owner = "kotlin.collections.ClrCollectionDefaultsKt";
        var formal = new TypeNode.Tv("method", 0);
        var helper = refs.AuthoredKotlinHelper(owner, name, 1,
            new TypeNode[] { Face(sourceCollection, formal), formal });
        // This carrier is declared over a CLR STORAGE element, not a Kotlin source variable. Both its
        // enumerated value and its Contains/IndexOf argument already have that same representation.
        // Storage projection is idempotent here; nullable witnesses are not owned by this physical carrier.
        TypeNode NoNullableWitness(TypeNode _) => throw new InvalidOperationException(
            "Physical collection storage carrier has no nullable representation witness");
        var arguments = helper.NullableFrame?.Close(new TypeNode[] { Element },
            type => type, NoNullableWitness, type => type, NoNullableWitness) ?? new TypeNode[] { Element };
        TypeNode Physical(TypeNode type) => BirTypeLowering.LowerPhysicalType(type, refs.Aliases,
            refs.IsValueType, refs.PhysicalTypeNames, typeArg: false, nullableFrames: refs.NullableTypeFrames);
        return new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(owner), ["method"] = name,
            [DeclarationIdentityBinding.Key] = helper.DeclarationId,
            ["typeArgs"] = new JsonArray(arguments.Select(TypeJson.Write).ToArray()),
            ["sig"] = new JsonArray(helper.ParamTypeNodes.Select(Physical).Select(TypeJson.Write).ToArray()),
            ["ret"] = TypeJson.Write(result), ["args"] = new JsonArray(receiver, Local("element")),
        };
    }

    static JsonObject ListCarrier(ReferenceMetadataIndex refs)
    {
        const string face = CollectionViewFaces.IList;
        const string readOnly = CollectionViewFaces.IReadOnlyList;
        var methods = new JsonArray {
            Method("get_Item", Element, new JsonArray(Parameter("index", Int)), new JsonArray(Return(new JsonObject {
                ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(Face(readOnly, Element)),
                ["virtual"] = true, ["recv"] = ReadOnlySelf(readOnly), ["method"] = "get_Item",
                ["sig"] = new JsonArray(TypeJson.Write(Int)), ["ret"] = TypeJson.Write(Element),
                ["args"] = new JsonArray(Local("index")),
            })), face),
            Method("IndexOf", Int, new JsonArray(Parameter("element", Element)), new JsonArray(Return(
                StorageHelper(refs, "clrListIndexOf", "kotlin.collections.List", Int, ReadOnlySelf(readOnly)))), face),
            Method("set_Item", Void, new JsonArray(Parameter("index", Int), Parameter("value", Element)), Unsupported(), face),
            Method("Insert", Void, new JsonArray(Parameter("index", Int), Parameter("element", Element)), Unsupported(), face),
            Method("RemoveAt", Void, new JsonArray(Parameter("index", Int)), Unsupported(), face),
        };
        return new JsonObject {
            ["name"] = ListCarrierName, ["kind"] = "interface", ["vis"] = "internal", ["generated"] = true,
            ["typeParams"] = new JsonArray("T"),
            ["base"] = null, ["fields"] = new JsonArray(), ["ctors"] = new JsonArray(),
            ["interfaces"] = new JsonArray(TypeJson.Write(Face(CarrierName, Element)),
                TypeJson.Write(Face(face, Element)), TypeJson.Write(Face(readOnly, Element))),
            ["methods"] = methods,
        };
    }
}
