using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// One binding-owned policy for both demand analysis and type materialization. Compiler-owned generic
// applications use their declared frame; a trusted CLR alias uses the binding's invariant argument form.
sealed class GenericRepresentationPolicy
{
    readonly IReadOnlyDictionary<string, string> _aliases;

    public GenericRepresentationPolicy(IReadOnlyDictionary<string, string> aliases) => _aliases = aliases;

    public bool UsesStorageArguments(string owner) => _aliases.ContainsKey(owner);

    public NullableRepresentationFrame.Role ApplicationRole(string owner, NullableRepresentationFrame.Role role) =>
        UsesStorageArguments(owner) ? role switch {
            NullableRepresentationFrame.Role.Ordinary => NullableRepresentationFrame.Role.Storage,
            NullableRepresentationFrame.Role.Nullable => NullableRepresentationFrame.Role.NullableStorage,
            _ => role,
        } : role;

    public bool IsStorageElement(string kind, string key) => kind switch {
        "newList" or "newSet" => key == "elem",
        "newMap" => key is "keyType" or "valType",
        _ => false,
    };

    public TypeNode ProjectArgumentHead(TypeNode.Fqn source, bool storage, NullableRepresentationFrame frame)
    {
        if (!_aliases.TryGetValue(source.Name, out var ordinaryHead)
            || !BirTypeLowering.TryInvariantSibling(source.Name, out var storageHead)) return source;
        var arguments = source.Args;
        if (frame != null && arguments != null) arguments = frame.OrdinaryArguments(arguments);
        return new TypeNode.Fqn(storage ? storageHead : ordinaryHead, arguments);
    }

    public static void SelfTest()
    {
        var policy = new GenericRepresentationPolicy(new Dictionary<string, string> {
            ["kotlin.collections.Map"] = "System.Collections.Generic.IDictionary",
            ["kotlin.collections.Collection"] = "System.Collections.Generic.IReadOnlyCollection",
        });
        var root = JsonNode.Parse("""
        {"fileClass":"BindingRoles","methods":[
          {"name":"choose","declarationId":"choose","typeParams":["T"],"params":[
            {"name":"array","type":{"t":"array","elem":{"t":"tv","scope":"method","i":0}}},
            {"name":"map","type":{"t":"fqn","name":"kotlin.collections.Map","args":[
              {"t":"fqn","name":"kotlin.String"},{"t":"tv","scope":"method","i":0}]}}],
           "ret":{"t":"tv","scope":"method","i":0},"body":[{"k":"return","value":
             {"k":"arrayGet","array":{"k":"local","name":"array"},"index":{"k":"const","type":{"t":"fqn","name":"kotlin.Int"},"value":0},
              "elem":{"t":"tv","scope":"method","i":0}}}]},
          {"name":"use","params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"callStatic","declarationId":"choose","typeArgs":[
              {"t":"fqn","name":"kotlin.collections.Collection","args":[{"t":"fqn","name":"kotlin.String"}]}],"args":[]}]}]}
        """)!.AsObject();
        NullableRepresentationMaterialization.Apply(new[] { root }, _ => false, policy: policy);
        var choose = root["methods"][0];
        var frame = NullableRepresentationFrame.Read(JsonNode.Parse(choose[NullableRepresentationTypes.MethodFrameKey].GetValue<string>()));
        if (!frame.StorageIndices.SequenceEqual(new[] { 0 }) || frame.PhysicalArity != 2
            || TypeJson.Read(choose["params"][0]["type"]) != new TypeNode.Array(new TypeNode.Tv("method", 0))
            || TypeJson.Read(choose["params"][1]["type"]) is not TypeNode.Fqn { Args: { } mapArguments }
            || mapArguments[1] != new TypeNode.Tv("method", 1))
            throw new InvalidOperationException("Binding policy did not separate native array and invariant map argument roles");
        var arguments = ((JsonArray)root["methods"][1]["body"][0]["typeArgs"]).Select(TypeJson.Read).ToArray();
        if (arguments.Length != 2 || arguments[0] is not TypeNode.Fqn { Name: "System.Collections.Generic.IReadOnlyCollection" }
            || arguments[1] is not TypeNode.Fqn { Name: "System.Collections.Generic.ICollection" })
            throw new InvalidOperationException("Binding policy did not close ordinary and storage call arguments separately");

        var sourceOverride = TypeJson.Write(new TypeNode.Fqn("Outer.Inner", new TypeNode[] { new TypeNode.Tv("type", 0) }));
        var innerRoot = new JsonObject { ["fileClass"] = "InnerRoles", ["types"] = new JsonArray(new JsonObject {
            ["kind"] = "class", ["name"] = "Outer.Inner", ["semanticOwner"] = "Outer",
            ["typeParams"] = new JsonArray("T", "$storage0"),
            ["outerTypeParamCount"] = 2, ["mods"] = new JsonObject { ["inner"] = true },
            ["methods"] = new JsonArray(new JsonObject { ["overrides"] = new JsonArray(new JsonObject {
                ["owner"] = sourceOverride.DeepClone(), ["member"] = "run",
            }) }),
        }) };
        TypeOwnershipLowering.ProjectInnerApplications(new[] { innerRoot }, null);
        if (!JsonNode.DeepEquals(innerRoot["types"][0]["methods"][0]["overrides"][0]["owner"], sourceOverride))
            throw new InvalidOperationException("Physical inner capture projection rewrote a source override edge");
        Console.WriteLine("[generic binding roles] self-test OK (array root, map storage, exact source override frames)");
    }
}
