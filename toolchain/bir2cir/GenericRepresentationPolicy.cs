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
    readonly ReferenceMetadataIndex _references;
    readonly HashSet<string> _localOwners;
    readonly HashSet<string> _existentialOwners = new(StringComparer.Ordinal);
    readonly HashSet<JsonObject> _nativeFields = new();
    readonly HashSet<(string Owner, string Name, bool Static)> _nativeFieldNames = new();

    public GenericRepresentationPolicy(IReadOnlyDictionary<string, string> aliases,
        ReferenceMetadataIndex references = null, IEnumerable<JsonNode> roots = null)
    {
        _aliases = aliases;
        _references = references;
        _localOwners = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode node)
        {
            if (node is not JsonObject declaration) return;
            if (declaration["kind"] != null && declaration["name"] is JsonValue name
                && name.TryGetValue<string>(out var owner))
            {
                _localOwners.Add(owner);
                if (FBoundStarProjectionErasure.IsSourceGeneric(declaration)) _existentialOwners.Add(owner);
            }
            foreach (var child in declaration["types"] as JsonArray ?? new JsonArray()) Collect(child);
            foreach (var cell in (declaration["refTypes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(cell["name"]) is string cellOwner) _localOwners.Add(cellOwner);
        }
        var inputs = (roots ?? Array.Empty<JsonNode>()).ToArray();
        foreach (var root in inputs) Collect(root);
        var fields = new Dictionary<(string Owner, string Name, bool Static), JsonObject>();
        void IndexFields(JsonObject declaration)
        {
            var owner = Text(declaration["name"]) ?? Text(declaration["fileClass"]);
            foreach (var field in (declaration["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (owner != null && Text(field["name"]) is string name
                    && TypeJson.Read(field["type"]) is TypeNode.Tv)
                {
                    var key = (owner, name, field["static"]?.GetValue<bool>() == true);
                    fields.Add(key, field);
                    // A published field is a native location even when this module never takes its address.
                    // Property backing fields are private; compiler-generated captures are not user exports.
                    if (Text(field["vis"]) != "private"
                        && declaration["generated"]?.GetValue<bool>() != true)
                    {
                        _nativeFields.Add(field);
                        _nativeFieldNames.Add(key);
                    }
                }
            foreach (var cell in (declaration["refTypes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(cell["name"]) is string cellOwner) fields.Add((cellOwner, "v", false), cell);
            foreach (var child in (declaration["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                IndexFields(child);
        }
        foreach (var root in inputs.OfType<JsonObject>()) IndexFields(root);
        void FindAddresses(JsonNode node)
        {
            if (node is JsonArray array)
                foreach (var child in array) FindAddresses(child);
            else if (node is JsonObject obj)
            {
                // The selected declaration supplies the managed-reference shape.
                // Decide field layout while source bodies still exist in both
                // reference and runtime builds, not after reference-body removal.
                if (Text(obj["k"]) is "callStatic" or "callInstance" or "constrainedCall" or "callLocal" or "new"
                    && (obj["shapeTypes"] ?? obj["sig"] ?? obj["memberSignature"] ?? obj["argTypes"]) is JsonArray parameters
                    && obj["args"] is JsonArray arguments && parameters.Count == arguments.Count)
                    for (var i = 0; i < parameters.Count; i++)
                    {
                        if (SignatureValueTypes.Of(TypeJson.Read(parameters[i])) is not TypeNode.ByRef
                            || arguments[i] is not JsonObject argument) continue;
                        var location = Text(argument["k"]) == "byrefOf" ? argument["inner"] as JsonObject : argument;
                        if (location == null || Text(location["k"]) is not ("field" or "staticField")
                            || TypeJson.OwnerName(location["ownerType"] ?? location["owner"]) is not string owner
                            || Text(location["name"]) is not string name
                            || !fields.TryGetValue((owner, name, Text(location["k"]) == "staticField"), out var declaration)
                            || TypeJson.Read(declaration["type"] ?? declaration["elem"]) is not TypeNode.Tv) continue;
                        _nativeFields.Add(declaration);
                        _nativeFieldNames.Add((owner, name, Text(location["k"]) == "staticField"));
                    }
                foreach (var (key, child) in obj)
                    if (key != "attrs") FindAddresses(child);
            }
        }
        foreach (var root in inputs) FindAddresses(root);
    }

    internal bool IsNativeField(JsonObject node) => _nativeFields.Contains(node)
        || Text(node["k"]) is "field" or "staticField" or "setField" or "setStaticField"
            && TypeJson.OwnerName(node["ownerType"] ?? node["owner"]) is string owner
            && Text(node["name"]) is string name
            && _nativeFieldNames.Contains((owner, name, Text(node["k"]) is "staticField" or "setStaticField"));

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;

    internal bool OwnsValueSlots(string owner) => owner != null && !UsesNativeOwnerSlots(owner)
        && (_localOwners.Contains(owner) || _references?.HasDotKtOwner(owner) == true);

    internal bool UsesNativeOwnerSlots(string owner) => owner != null
        && (_aliases.ContainsKey(ReferenceMetadataIndex.BareOwnerFqn(owner))
            || _references?.TryResolveClrOwner(owner, out _, out _) == true);

    internal bool HasNativeConstraint(TypeNode.Fqn bound) => UsesNativeOwnerSlots(bound.Name)
        || !_localOwners.Contains(bound.Name)
            && _references?.ResolveNetType(bound.Name, bound.Args?.Length ?? 0) != null;

    internal TypeNode StorageConstraint(TypeNode.Fqn bound) => OwnsValueSlots(bound.Name)
        ? bound.Args is { Length: > 0 }
            ? new TypeNode.Fqn(bound.Name, bound.Args.Select(_ => (TypeNode)new TypeNode.Star()).ToArray()) : bound
        : HasNativeConstraint(bound) ? bound : null;

    internal bool OwnsMethodValueSlots(JsonObject declaration) =>
        !(declaration["attrs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Any(attribute => TypeJson.OwnerName(attribute["attr"]) == "kotlin.clr.ClrIntrinsic")
        && !(declaration["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Any(edge => !OwnsValueSlots(TypeJson.OwnerName(edge["owner"])));

    public bool UsesStorageArguments(string owner) => false;

    public NullableRepresentationFrame.Role ApplicationRole(string owner, NullableRepresentationFrame.Role role) =>
        role;

    public bool IsStorageElement(string kind, string key) => false;

    // Frame expansion must retain the source owner until declaration matching is complete. Lowering only a
    // call's type arguments here would compare CLR IReadOnlyList<T> with a selected Kotlin List<T> descriptor.
    // BirTypeLowering owns the eventual alias substitution for both positions.
    public TypeNode ProjectArgumentHead(TypeNode.Fqn source, bool storage, NullableRepresentationFrame frame) =>
        storage && OwnsValueSlots(source.Name) && source.Args is { Length: > 0 }
            && (_localOwners.Contains(source.Name) ? _existentialOwners.Contains(source.Name)
                : _references?.TryExistentialPhysicalOwner(source.Name, out _) == true)
            ? new TypeNode.Fqn(source.Name, source.Args.Select(_ => (TypeNode)new TypeNode.Star()).ToArray())
            : source;

    public static void SelfTest()
    {
        var heads = JsonNode.Parse("""
        {"types":[
          {"kind":"class","name":"SourceHead","typeParams":["T"]},
          {"kind":"class","name":"GeneratedHead","generated":true,"typeParams":["T"]}]}
        """)!.AsObject();
        var headPolicy = new GenericRepresentationPolicy(new Dictionary<string, string>(), roots: new[] { heads });
        var headArguments = new TypeNode[] { new TypeNode.Tv("method", 0) };
        var sourceHead = new TypeNode.Fqn("SourceHead", headArguments);
        var generatedHead = new TypeNode.Fqn("GeneratedHead", headArguments);
        if (headPolicy.ProjectArgumentHead(sourceHead, true, null)
                != new TypeNode.Fqn("SourceHead", new TypeNode[] { new TypeNode.Star() })
            || headPolicy.ProjectArgumentHead(sourceHead, false, null) != sourceHead
            || headPolicy.ProjectArgumentHead(generatedHead, true, null) != generatedHead)
            throw new InvalidOperationException("Storage heads must use only declaration-owned existential views");

        var fieldRoot = JsonNode.Parse("""
        {"fileClass":"FieldFrames","types":[{"kind":"class","name":"FieldFrame","typeParams":["T"],
          "fields":[{"name":"native","type":{"t":"tv","scope":"type","i":0}},
                    {"name":"ordinary","vis":"private","type":{"t":"tv","scope":"type","i":0}}],
          "methods":[{"name":"pass","params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"callStatic","sig":[{"t":"byRef","of":{"t":"tv","scope":"method","i":0}}],
             "typeArgs":[{"t":"tv","scope":"type","i":0}],"args":[
              {"k":"field","ownerType":{"t":"fqn","name":"FieldFrame","args":[{"t":"tv","scope":"type","i":0}]},
               "recv":{"k":"this"},"name":"native","memberType":{"t":"tv","scope":"type","i":0},
               "memberOwnerTypeParams":["T"]}]}]}]}]}
        """)!.AsObject();
        ((JsonArray)fieldRoot["types"]).Add(JsonNode.Parse("""
        {"kind":"object","name":"FieldSingleton","fields":[
          {"name":"INSTANCE","vis":"private","type":{"t":"fqn","name":"kotlin.String"}},
          {"name":"INSTANCE","static":true,"type":{"t":"fqn","name":"FieldSingleton"}}]}
        """));
        var fieldPolicy = new GenericRepresentationPolicy(new Dictionary<string, string>(), roots: new[] { fieldRoot });
        NullableRepresentationMaterialization.Apply(new[] { fieldRoot }, _ => false, policy: fieldPolicy);
        var fieldOwner = fieldRoot["types"][0];
        var addressedField = fieldOwner["methods"][0]["body"][0]["args"][0];
        if (TypeJson.Read(fieldOwner["fields"][0]["type"]) != new TypeNode.Tv("type", 0)
            || TypeJson.Read(fieldOwner["fields"][1]["type"]) != new TypeNode.Tv("type", 1)
            || TypeJson.Read(addressedField["memberType"]) != new TypeNode.Tv("type", 0)
            || ((JsonArray)addressedField["ownerType"]["args"]).Count != 2)
            throw new InvalidOperationException("Native field allocation changed an unrelated field or lost declaration correspondence");
        var policy = new GenericRepresentationPolicy(new Dictionary<string, string> {
            ["kotlin.collections.Map"] = "System.Collections.Generic.IDictionary",
            ["kotlin.collections.Collection"] = "System.Collections.Generic.IReadOnlyCollection",
        });
        if (!policy.UsesNativeOwnerSlots("kotlin.collections.Map`2")
            || !policy.UsesNativeOwnerSlots("kotlin.collections.Collection`1")
            || policy.UsesNativeOwnerSlots("Unrelated`1"))
            throw new InvalidOperationException("Native owner roles must use the selected alias identity, including exact metadata owners");
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
        if (((JsonArray)choose["typeParams"]).Count != 2
            || TypeJson.Read(choose["ret"]) != new TypeNode.Tv("method", 1)
            || TypeJson.Read(choose["params"][0]["type"]) != new TypeNode.Array(new TypeNode.Tv("method", 0))
            || TypeJson.Read(choose["params"][1]["type"]) is not TypeNode.Fqn { Args: { } mapArguments }
            || mapArguments[1] != new TypeNode.Tv("method", 0))
            throw new InvalidOperationException("Binding policy changed a native array or map element's canonical representation");
        var arguments = ((JsonArray)root["methods"][1]["body"][0]["typeArgs"]).Select(TypeJson.Read).ToArray();
        if (arguments.Length != 2 || arguments.Any(argument => argument is not TypeNode.Fqn { Name: "kotlin.collections.Collection" }))
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
