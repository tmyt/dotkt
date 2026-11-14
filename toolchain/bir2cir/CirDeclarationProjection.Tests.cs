using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CirDeclarationProjection
{
    // Current source fixtures enter the real owning projection directly. These
    // helpers are test-only, not a second implementation of physical projection.
    internal static CirFileDeclaration ReadCirFileDeclaration(JsonNode source, bool runtimeMode = false)
    {
        var graph = BirGraphDom.ReadBirFileDeclaration(source);
        RoundtripMetadata.CompleteMetadata(graph, runtimeMode);
        var emitted = graph.Types.OrEmpty().Select(TypedCirProjection.TypeName).ToHashSet(StringComparer.Ordinal);
        return new TypedCirProjection(emitted).ProjectCirFileDeclaration(graph);
    }

    static CirNode ReadCirNode(JsonNode source) => new TypedCirProjection(new HashSet<string>())
        .ProjectNode(BirGraphDom.ReadNode(source));

    internal static void SelfTest()
    {
        // Exercise the real producer -> metadata owner -> typed declaration boundary. No emitter is simulated.
        var nested = new TypeNode.Fqn("Physical.Container", [new TypeNode.Fqn("kotlin.collections.List",
            [new TypeNode.Fqn("kotlin.Int")])]);
        var method = Method();
        method["ret"] = TypeJson.Write(nested);
        method["params"] = new JsonArray(new JsonObject { ["name"] = "value", ["type"] = TypeJson.Write(nested) });
        var file = File(method);
        file["fields"] = new JsonArray(new JsonObject { ["name"] = "F", ["type"] = TypeJson.Write(nested) });
        file["properties"] = new JsonArray(new JsonObject { ["name"] = "P", ["type"] = TypeJson.Write(nested) });
        CollectionIdentityRecord.RecordMemberSlots(file);
        if (!method.ContainsKey("collIdentityRet") || !method["params"][0].AsObject().ContainsKey("collIdentity"))
            throw new Exception("Collection metadata handoff control did not exercise its producer");
        RoundtripMetadata.PrepareMetadata(file, runtimeMode: false);
        AddCarrierDefinition(file, "KotlinCollectionIdentityAttribute");
        var declarations = ReadCirFileDeclaration(file);
        AttributeApplication[][] sites = [declarations.Methods[0].RetAttrs.Value,
            declarations.Methods[0].Params[0].Attrs.Value, declarations.Fields[0].Attrs.Value,
            declarations.Properties.Value[0].Attrs.Value];
        foreach (var site in sites)
        {
            var attribute = site.Single(a => a.Attr is TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.KotlinCollectionIdentityAttribute" });
            if (attribute.Args[1] is not AttributeArgument.BytesValue bytes
                || Encoding.UTF8.GetString(bytes.Bytes) != TypeNode.ToJson(nested))
                throw new Exception("Collection handoff lost its exact source metadata payload");
        }

        var runtimeMethod = Method();
        runtimeMethod[DeclarationIdentityBinding.Key] = "probe:runtime";
        runtimeMethod["declarationSourceName"] = "M";
        runtimeMethod["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }, new JsonObject { ["name"] = "N" });
        runtimeMethod[ReifiedNullabilityWitnessLowering.SemanticIndicesKey] = new JsonArray(0);
        runtimeMethod[ReifiedNullabilityWitnessLowering.WitnessIndicesKey] = new JsonArray(1);
        runtimeMethod[FBoundStarProjectionErasure.InnerConstructorFactoryKey] = new JsonObject {
            ["inner"] = "Physical.Inner", ["declaration"] = "probe:inner", ["typeArgs"] = new JsonArray()
        };
        var runtime = File();
        runtime["types"] = new JsonArray(new JsonObject {
            ["name"] = "Physical.Carrier", ["kind"] = "class", ["base"] = null,
            ["methods"] = new JsonArray(runtimeMethod),
            ["companionCarrier"] = new JsonObject { ["kind"] = "sidecar", ["owner"] = "Physical.Owner",
                ["name"] = "Companion", ["visibility"] = "public", ["physicalOwner"] = "Physical.Owner", ["physicalOwnerArity"] = 1 },
            ["staticCarrier"] = new JsonObject { ["owner"] = "Physical.Owner" }
        });
        RoundtripMetadata.PrepareMetadata(runtime, runtimeMode: true);
        AddCarrierDefinition(runtime, "KotlinDeclarationIdentityAttribute");
        var runtimeDeclarations = ReadCirFileDeclaration(runtime, runtimeMode: true);
        var physical = runtimeDeclarations.Types[0].Methods.Value[0];
        if (physical.Name != "M" || physical.TypeParams.Value.Length != 2 || !physical.Body.HasValue
            || physical.Attrs.OrEmpty().Count(a => a.Attr is TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.KotlinDeclarationIdentityAttribute" }) != 1)
            throw new Exception("Runtime handoff consumption changed the physical declaration or its linkage");
        Console.WriteLine("[CIR declaration boundary] self-test OK (four collection carriers, five runtime handoffs, physical frames/linkage)");
    }

    static JsonObject Method() => new() {
        ["name"] = "M", ["static"] = true, ["override"] = false, ["virtual"] = false,
        ["params"] = new JsonArray(), ["ret"] = TypeJson.Fqn("void"), ["body"] = new JsonArray()
    };

    static JsonObject File(params JsonObject[] methods) => new() {
        ["fileClass"] = "Probe", ["hasMain"] = false, ["fields"] = new JsonArray(),
        ["methods"] = new JsonArray(methods.Cast<JsonNode>().ToArray()), ["types"] = new JsonArray()
    };

    static void AddCarrierDefinition(JsonObject file, string name)
    {
        // Attribute admission is part of this boundary test; declaration emission is covered by real binaries.
        ((JsonArray)file["types"]).Add(new JsonObject {
            ["name"] = "DotKt.Runtime.CompilerServices." + name, ["kind"] = "class"
        });
    }
}
