using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    internal static void SourceParameterSnapshotSelfTest()
    {
        var own = new TypeNode.Fqn("Own");
        var outer = new TypeNode.Fqn("Outer");
        var sourceType = new TypeNode.Fqn("Container.Inner", new TypeNode[] { own, outer });
        var physicalType = new TypeNode.Fqn(sourceType.Name, new TypeNode[] { outer, own });
        BirFunctionDeclaration Method(string id) => new() { Name = "same", DeclarationId = id,
            Params = new[] { new BirParameter { Name = "value", Type = sourceType } } };
        var discarded = Method("earlier-owner");
        var retained = Method("selected-owner");
        var first = new BirFileDeclaration { FileClass = "First", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Shared", Methods = new[] { discarded } },
        } };
        var second = new BirFileDeclaration { FileClass = "Second", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Shared", Methods = new[] { retained } },
        } };
        var files = new[] { first, second };
        var pending = CaptureSourceParameters(files);
        retained.Params.Value[0].Type = physicalType;
        var projected = files.Select(file => (JsonNode)file.ToDom()).ToArray();
        var associated = pending.BindDom(projected);
        var actual = (JsonObject)projected[1]["types"][0]["methods"][0];
        if (associated.Count != 1 || !associated.ContainsKey(actual)
            || associated[actual] is not [TypeNode.Fqn { Args: [var originalOwn, var originalOuter] }]
            || originalOwn != own || originalOuter != outer
            || TypeJson.Read(actual["params"][0]["type"]) != physicalType)
            throw new InvalidOperationException("Source override parameters lost their capture clock or actual method association");
        var repeated = false;
        try { pending.BindDom(projected); } catch (InvalidOperationException) { repeated = true; }
        if (!repeated) throw new InvalidOperationException("Source override parameters were bound twice");

        var replaced = CaptureSourceParameters(files);
        second.Types.Value[0].Methods.Value[0] = (BirFunctionDeclaration)retained.DeepClone();
        var detached = false;
        try { replaced.BindDom(files.Select(file => (JsonNode)file.ToDom()).ToArray()); }
        catch (InvalidOperationException) { detached = true; }
        if (!detached) throw new InvalidOperationException("Source override parameters rebound a copied declaration by name or identity");
        Console.WriteLine("[source override parameter snapshots] self-test OK (last owner, pre-projection types, exact continuation, one-time handoff)");
    }
}
