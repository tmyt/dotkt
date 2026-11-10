using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// BCL-only collection-interface slots. A Kotlin `MutableCollection<E>` is `@ClrTypeAlias("System.Collections.Generic.
// ICollection")` and `MutableList<E>` is `IList` — but `ICollection<T>`/`IList<T>` carry members Kotlin's collection
// interfaces do NOT: `Contains`, `CopyTo`, `IsReadOnly` (ICollection) and `IndexOf` (IList). Kotlin has the
// value-returning `contains`/`indexOf` under LOWERCASE names (not @ClrIntrinsic-renamed → DeclarationRename leaves them
// lowercase), and has NO equivalent for `CopyTo`/`IsReadOnly` at all. So a Kotlin class implementing the aliased
// interface (kotlin.collections.AbstractMutable{Collection,List,Set}, a MutableMap keys/values view, a user class) is
// missing those BCL slots → the CLR loader rejects any CONCRETE type in the hierarchy ("Method 'Contains' ... does not
// have an implementation" → TypeLoadException). Concrete Kotlin-backed collections need these native slots regardless
// of whether other collection factories happen to allocate native BCL storage.
//
// Fill each missing slot with an ordinary public forwarding member, following the constructed interface closure.
// A class base already supplies its native faces; inherit those implementations rather than replacing their policy.
// An IList face also reaches ICollection through that same graph. Contains and
// IndexOf dispatch through the already-resolved Kotlin query slots, which own the ordinary/storage argument
// boundary and the selected implementation (including renamed overrides). CopyTo consumes the exact CLR IEnumerable<E> face.
// IsReadOnly returns false. The return-DROPPING slots
// (Add/set_Item/RemoveAt) join the common late KotlinOverrideSlotBridge allocation. Non-ref builds only (the ref surface
// stays pure Kotlin). Modeled on ComparableBridgeSynthesis.
static class CollectionBclSlotSynthesis
{
    const string ICollection = "System.Collections.Generic.ICollection";
    const string IList = "System.Collections.Generic.IList";
    const string ContractKey = "dotktKotlinMutableCollectionContract";

    static string FaceKey(TypeNode.Fqn face) => SupertypeGraph.TypeKey(
        BirTypeLowering.CanonicalPhysicalSlotType(CollectionFace(face)));

    // Preserve the source obligation before physical lowering: a map or a foreign CLR interface can expose
    // ICollection as storage without implementing Kotlin's mutable-collection query/default contract.
    public static void RecordContracts(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var definitions = SupertypeGraph.Collect(roots);
        foreach (var definition in definitions.Values.Where(d => d.Kind == "class"))
        {
            var contracts = SupertypeGraph.Reachable(definition, definitions, refs,
                    sourceFrames: true, physicalSourceApplications: true)
                .Select(edge => new TypeNode.Fqn(ReferenceMetadataIndex.BareOwnerFqn(edge.spec.Name), edge.spec.Args))
                .Where(owner => owner.Name is "kotlin.collections.MutableCollection"
                    or "kotlin.collections.MutableList" or "kotlin.collections.MutableSet")
                .Select(owner => (JsonNode)JsonValue.Create(TypeJson.Write(owner).ToJsonString())).ToArray();
            // The final argument frame is already allocated. Keep these obligation facts opaque to value erasure;
            // an operational interface is not a collection value. Physical lowering consumes them once below.
            if (contracts.Length != 0) definition.Node[ContractKey] = new JsonArray(contracts);
        }
    }

    internal static void SelfTest()
    {
        TypeNode.Fqn Face(string owner, TypeNode element) => new(owner, new[] { element });
        var keyword = Face(ICollection, new TypeNode.Array(new TypeNode.Fqn("int")));
        var qualified = Face(ICollection + "`1", new TypeNode.Array(new TypeNode.Fqn("System.Int32")));
        if (FaceKey(keyword) != FaceKey(qualified)
            || FaceKey(Face(ICollection, new TypeNode.Tv("type", 0)))
                == FaceKey(Face(ICollection, new TypeNode.Tv("type", 1)))
            || FaceKey(qualified) == FaceKey(Face(IList, qualified.Args[0])))
            throw new InvalidOperationException("Native collection faces lost physical identity or exact frames");
        Console.WriteLine("[collection native faces] self-test OK (physical element identity, distinct frames and faces)");
    }

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> lowerType)
    {
        // Snapshot the whole module before synthesis: a user interface/base can live in a later input file.
        var definitions = SupertypeGraph.Collect(roots);
        foreach (var definition in definitions.Values)
        {
            var contracts = (definition.Node[ContractKey] as JsonArray)?
                .Select(contract => lowerType(TypeJson.Read(JsonNode.Parse(contract.GetValue<string>()))) as TypeNode.Fqn)
                .Where(contract => contract != null).Select(CollectionFace).OfType<TypeNode.Fqn>().ToList()
                ?? new List<TypeNode.Fqn>();
            definition.Node.Remove(ContractKey);
            if (definition.Kind != "class") continue; // interfaces carry no bodies
            if (contracts.Count == 0) continue;
            var owned = contracts.Select(FaceKey).ToHashSet();
            foreach (var list in contracts.Where(contract => contract.Name == IList))
                owned.Add(FaceKey(new TypeNode.Fqn(ICollection, list.Args)));
            var to = definition.Node;
            if (to["interfaces"] is not JsonArray ifaces) continue;

            var faces = SupertypeGraph.Reachable(
                    new SupertypeGraph.Def { Interfaces = definition.Interfaces }, definitions, refs)
                .Where(edge => edge.isInterface).Select(edge => CollectionFace(edge.spec))
                .OfType<TypeNode.Fqn>().Where(face => owned.Contains(FaceKey(face))).ToList();
            if (faces.Count == 0) continue;
            var inherited = definition.Base == null ? new HashSet<string>()
                : SupertypeGraph.Reachable(new SupertypeGraph.Def { Base = definition.Base }, definitions, refs)
                    .Where(edge => edge.isInterface).Select(edge => CollectionFace(edge.spec))
                    .OfType<TypeNode.Fqn>().Select(FaceKey).ToHashSet();
            var stated = ifaces.Select(TypeJson.Read).OfType<TypeNode.Fqn>().Select(CollectionFace)
                .OfType<TypeNode.Fqn>().Select(FaceKey).ToHashSet();

            // Each edge has already been substituted into this class's exact physical generic frame.
            JsonNode collElem = null, listElem = null;
            foreach (var face in faces)
            {
                if (inherited.Contains(FaceKey(face))) continue;
                if (face.Name == IList) listElem ??= TypeJson.Write(face.Args[0]);
                else collElem ??= TypeJson.Write(face.Args[0]);
                // State the native face so subsequent slot and read-only-sibling synthesis consume the same facts.
                if (stated.Add(FaceKey(face)))
                    ifaces.Add(TypeJson.Write(face));
            }
            if (collElem == null && listElem == null) continue;

            if (to["methods"] is not JsonArray methods) { methods = new JsonArray(); to["methods"] = methods; }
            void AddSlot(JsonObject slot, string owner, JsonNode elem)
            {
                var name = slot["name"].GetValue<string>();
                string Key(JsonNode type) => SupertypeGraph.TypeKey(
                    BirTypeLowering.CanonicalPhysicalSlotType(TypeJson.Read(type)));
                var signature = FunctionSignatureIdentity.Signature((JsonArray)slot["params"]);
                if (methods.OfType<JsonObject>().Any(method => method["name"]?.GetValue<string>() == name
                    && method["vis"]?.GetValue<string>() == "public"
                    && method["virtual"]?.GetValue<bool>() == true
                    && method["static"]?.GetValue<bool>() != true && TypeParameterFrame.Count(method) == 0
                    && method["params"] is JsonArray parameters && parameters.Count == signature.Count
                    && FunctionSignatureIdentity.Signature(parameters).Select(Key).SequenceEqual(signature.Select(Key))
                    && Key(method["ret"]) == Key(slot["ret"]))) return;

                // A same-named user overload (or non-virtual member) cannot fill the native slot. Keep that
                // declaration intact and allocate an exact explicit implementation instead of a duplicate MethodDef.
                if (methods.OfType<JsonObject>().Any(method => method["name"]?.GetValue<string>() == name))
                {
                    var fresh = "dotkt$native$" + name;
                    while (methods.OfType<JsonObject>().Any(method => method["name"]?.GetValue<string>() == fresh))
                        fresh += "$";
                    slot["name"] = fresh;
                    slot["vis"] = "private";
                    slot["clrFinal"] = true;
                    slot["clrInterfaceImpls"] = new JsonArray(new JsonObject {
                        ["owner"] = TypeJson.Write(new TypeNode.Fqn(owner, new[] { TypeJson.Read(elem) })),
                        ["member"] = name, ["arity"] = 0, ["params"] = signature,
                        ["ret"] = slot["ret"].DeepClone(),
                    });
                }
                methods.Add(slot);
            }

            // ICollection<E> face: Contains / CopyTo / get_IsReadOnly.
            if (collElem != null)
            {
                AddSlot(QuerySlotForward("Contains", collElem, "System.Boolean",
                    "KotlinCollectionDefaultSlots", "dotktContains"), ICollection, collElem);
                AddSlot(ConstBoolGetter("get_IsReadOnly"), ICollection, collElem);
                AddSlot(CopyTo(collElem), ICollection, collElem);
            }
            // IList<E> face additionally needs IndexOf.
            if (listElem != null)
                AddSlot(QuerySlotForward("IndexOf", listElem, "System.Int32",
                    "KotlinListDefaultSlots", "dotktIndexOf"), IList, listElem);
        }
    }

    // A referenced graph uses exact CLR TypeDef punctuation; locally lowered aliases use the open name.
    // Compare that same declaration identity with its authoritative argument vector, never a source-name prefix.
    static TypeNode.Fqn CollectionFace(TypeNode.Fqn face)
    {
        if (face.Args is not { Length: 1 }) return null;
        var name = ReferenceMetadataIndex.BareOwnerFqn(face.Name);
        return name is ICollection or IList ? new TypeNode.Fqn(name, face.Args) : null;
    }

    static JsonNode Clone(JsonNode n) => n == null ? null : JsonNode.Parse(n.ToJsonString());

    static JsonObject Method(string name, JsonArray parameters, JsonNode ret, JsonArray body) => new()
    {
        ["name"] = name,
        ["static"] = false,
        ["override"] = false,
        ["virtual"] = true,
        ["abstract"] = false,
        ["objectOverride"] = false,
        ["vis"] = "public",
        ["params"] = parameters,
        ["ret"] = ret,
        ["body"] = body,
    };

    static JsonObject This() => new() { ["k"] = "this" };
    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };

    // The semantic slot accepts object and already carries the selected Kotlin override and its type-safe barrier.
    // Do not reconstruct a target by lowercase name or use the incoming storage type as its ordinary signature.
    static JsonObject QuerySlotForward(string name, JsonNode elem, string ret, string slotOwner, string target) =>
        Method(name,
            new JsonArray(new JsonObject { ["name"] = "element", ["type"] = Clone(elem) }),
            TypeJson.Fqn(ret),
            new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices." + slotOwner),
                    ["virtual"] = true,
                    ["recv"] = This(),
                    ["method"] = target,
                    ["sig"] = new JsonArray(TypeJson.Fqn("System.Object")),
                    ["ret"] = TypeJson.Fqn(ret),
                    ["args"] = new JsonArray(new JsonObject {
                        ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Object"), ["e"] = Local("element"),
                    }),
                },
            }));

    static JsonObject ConstBoolGetter(string name) =>
        Method(name, new JsonArray(), TypeJson.Fqn("System.Boolean"),
            new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Boolean"), ["value"] = false },
            }));

    // This slot is synthesized after type lowering: E is an exact CLR element, not a Kotlin type argument.
    // Enumerate that physical face directly and dispose even when an array write throws.
    internal static JsonObject CopyTo(JsonNode elem)
    {
        var element = TypeJson.Read(elem);
        var iterator = new TypeNode.Fqn("System.Collections.Generic.IEnumerator", new[] { element });
        JsonObject Call(TypeNode owner, string method, JsonNode receiver, TypeNode result) => new() {
            ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(owner), ["method"] = method,
            ["virtual"] = true,
            ["recv"] = receiver, ["sig"] = new JsonArray(), ["args"] = new JsonArray(), ["ret"] = TypeJson.Write(result),
        };
        var body = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "var", ["name"] = "it", ["type"] = TypeJson.Write(iterator),
                ["init"] = Call(new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new[] { element }),
                    "GetEnumerator", This(), iterator),
            },
            new JsonObject { ["k"] = "var", ["name"] = "i", ["type"] = TypeJson.Fqn("System.Int32"), ["init"] = Local("arrayIndex") },
            new JsonObject
            {
                ["k"] = "while",
                ["cond"] = Call(new TypeNode.Fqn("System.Collections.IEnumerator"), "MoveNext", Local("it"), new TypeNode.Fqn("System.Boolean")),
                ["body"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "exprStmt",
                        ["expr"] = new JsonObject
                        {
                            ["k"] = "arraySet", ["array"] = Local("array"), ["index"] = Local("i"), ["elem"] = Clone(elem),
                            ["value"] = Call(iterator, "get_Current", Local("it"), element),
                        },
                    },
                    new JsonObject
                    {
                        ["k"] = "setLocal", ["name"] = "i",
                        ["value"] = new JsonObject
                        {
                            ["k"] = "binOp", ["op"] = "+", ["lhs"] = Local("i"),
                            ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = 1 },
                        },
                    },
                },
            },
        };
        var loop = body[2];
        body.RemoveAt(2);
        body.Add(new JsonObject {
            ["k"] = "try", ["body"] = new JsonArray(loop), ["catches"] = new JsonArray(),
            ["finally"] = new JsonArray(new JsonObject { ["k"] = "exprStmt",
                ["expr"] = Call(new TypeNode.Fqn("System.IDisposable"), "Dispose", Local("it"), new TypeNode.Fqn("void")) }),
        });
        return Method("CopyTo",
            new JsonArray(
                new JsonObject { ["name"] = "array", ["type"] = new JsonObject { ["t"] = "array", ["elem"] = Clone(elem) } },
                new JsonObject { ["name"] = "arrayIndex", ["type"] = TypeJson.Fqn("System.Int32") }),
            TypeJson.Fqn("void"), body);
    }
}
