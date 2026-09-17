using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// One binding-owned policy for both demand analysis and type materialization. Compiler-owned generic
// applications use their declared frame; readonly aliases retain the same face in ordinary and nested slots.
sealed class GenericRepresentationPolicy
{
    readonly IReadOnlyDictionary<string, string> _aliases;

    public GenericRepresentationPolicy(IReadOnlyDictionary<string, string> aliases) => _aliases = aliases;

    public bool UsesStorageArguments(string owner) => false;

    public NullableRepresentationFrame.Role ApplicationRole(string owner, NullableRepresentationFrame.Role role) =>
        role;

    public bool IsStorageElement(string kind, string key) => false;

    // Frame expansion must retain the source owner until declaration matching is complete. Lowering only a
    // call's type arguments here would compare CLR IReadOnlyList<T> with a selected Kotlin List<T> descriptor.
    // BirTypeLowering owns the eventual alias substitution for both positions.
    public TypeNode ProjectArgumentHead(TypeNode.Fqn source, bool storage, NullableRepresentationFrame frame) => source;

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
        if (choose[NullableRepresentationTypes.MethodFrameKey] != null
            || ((JsonArray)choose["typeParams"]).Count != 1
            || TypeJson.Read(choose["params"][0]["type"]) != new TypeNode.Array(new TypeNode.Tv("method", 0))
            || TypeJson.Read(choose["params"][1]["type"]) is not TypeNode.Fqn { Args: { } mapArguments }
            || mapArguments[1] != new TypeNode.Tv("method", 0))
            throw new InvalidOperationException("Binding policy changed a native array or map element's canonical representation");
        var arguments = ((JsonArray)root["methods"][1]["body"][0]["typeArgs"]).Select(TypeJson.Read).ToArray();
        if (arguments.Length != 1 || arguments[0] is not TypeNode.Fqn { Name: "kotlin.collections.Collection" })
            throw new InvalidOperationException("Frame expansion changed a collection's source declaration identity");

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
