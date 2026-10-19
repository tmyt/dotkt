using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// TOP-LEVEL REIFIED ENUM INTRINSICS (#73). kotc emits the FAITHFUL top-level call for `enumValues<T>()` /
// `enumValueOf<T>(name)` — a plain
// `callStatic owner:null method:"enumValues" typeArgs:[T] args:[…]` (the Kotlin fact; it names no CLR shape and
// carries no `kotlin.*` FQN of the intrinsic). On the CLR every type arg is REIFIED, so these lower at the call
// site exactly like `T.values()` / `T.valueOf(name)` — a Kotlin<->CLR equivalence, so it lives HERE:
//
//   enumValues<T>()                    -> rich enum: callStatic <T>.values(); otherwise an enumValues semantic node
//   enumValueOf<T>(name)                -> rich enum: callStatic <T>.valueOf(name)      ; basic/gp: {k:enumParse,type:T,arg}
//
// RICH vs BASIC comes from an explicit producer fact: local BIR carries `enumRich:true`, while a referenced producer
// carries trusted [KotlinRichEnum]. EnumValueLowering resolves the semantic values node to declaration-ordered
// physical tables or the ordered runtime helper for a generic parameter. No values()/valueOf() name/signature
// convention is used to classify a referenced owner.
//
// Runs EARLY (grouped with the range/array/char faithful recognitions, before MemberCallSubstitution / BirTypeLowering)
// so the produced enumValues/enumParse/values nodes enter the ordinary CIR pipeline with their semantic enum identity
// still available.
static class EnumIntrinsicLowering
{
    static readonly HashSet<string> Names = new(System.StringComparer.Ordinal)
    { "enumValues", "enumValueOf" };

    // Collect the local RICH-enum type names (a `kind:"class"` decl carrying the faithful `enumRich:true` marker),
    // across ALL input files (a cross-file `enumValues<E>()` may name an enum declared in another input).
    public static HashSet<string> CollectRichEnums(IEnumerable<JsonNode> roots)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var root in roots)
            if (root is JsonObject ro && ro["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to && Str(to["kind"]) == "class"
                        && (to["enumRich"] as JsonValue)?.TryGetValue<bool>(out var r) == true && r
                        && Str(to["name"]) is string n)
                        set.Add(n);
        return set;
    }

    public static void Apply(JsonNode root, ISet<string> richEnums, ISet<string> localTopLevelFns, bool appBuild,
        ReferenceMetadataIndex refs, IReadOnlyDictionary<string, NullableRepresentationFrame> methodFrames)
        => Walk(root, richEnums, localTopLevelFns, appBuild, refs, methodFrames);

    static void Walk(JsonNode node, ISet<string> rich, ISet<string> local, bool app, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methodFrames)
    {
        switch (node)
        {
            case JsonObject obj:
                Rewrite(obj, rich, local, app, refs, methodFrames);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, rich, local, app, refs, methodFrames);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, rich, local, app, refs, methodFrames);
                break;
        }
    }

    static void Rewrite(JsonObject o, ISet<string> rich, ISet<string> local, bool app, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methodFrames)
    {
        // A referenced rich enum is projected into KLIB as an enum, so kotc faithfully emits the ordinary enum
        // semantic nodes for its synthesized values()/valueOf() declarations. The trusted producer carrier is the
        // sole fact that those operations physically live on a class. Consume its exact API map here; a basic enum
        // remains an enumValues/enumParse node and no member name or class shape is used as a fallback.
        var semanticKind = Str(o["k"]);
        if (semanticKind is "enumValues" or "enumParse"
            && o["type"] is JsonNode semanticTypeNode
            && TypeJson.Read(semanticTypeNode) is TypeNode.Fqn semanticType)
        {
            var values = "values";
            var valueOf = "valueOf";
            if (rich.Contains(semanticType.Name)
                || refs.TryKotlinRichEnumStaticApis(semanticType.Name, out values, out valueOf))
            {
                Replace(o, RichEnumCall(semanticTypeNode, semanticKind == "enumParse" ? valueOf : values,
                    semanticKind == "enumParse" ? o["arg"] : null));
                return;
            }
        }

        if (Str(o["k"]) != "callStatic" || o["owner"] != null) return;
        var method = Str(o["method"]);
        if (method == null || !Names.Contains(method)) return;
        if (o["typeArgs"] is not JsonArray ta) return;
        var declarationId = Str(o[DeclarationIdentityBinding.Key]);
        var frame = declarationId == null ? null : methodFrames.GetValueOrDefault(declarationId)
            ?? refs.NullableMethodFrame(declarationId);
        var tArg = SourceArgument(ta, frame);
        if (tArg == null) return;
        var args = o["args"] as JsonArray ?? new JsonArray();
        var isValueOf = method == "enumValueOf";
        // Match the Kotlin intrinsic's source-visible argument count.
        if (args.Count != (isValueOf ? 1 : 0)) return;
        // A user top-level `fun enumValues<T>()` shadow (app build) is NOT the kotlin.* intrinsic — leave it be.
        if (app && local.Contains(method)) return;
        var richName = TypeJson.Read(tArg) is TypeNode.Fqn f ? f.Name : null;
        var isLocalRich = richName != null && rich.Contains(richName);
        string mappedValuesApi = null;
        string mappedValueOfApi = null;
        var isReferencedRich = richName != null &&
            refs.TryKotlinRichEnumStaticApis(richName, out mappedValuesApi, out mappedValueOfApi);

        JsonNode repl;
        if (isLocalRich || isReferencedRich)
            repl = RichEnumCall(
                tArg,
                isValueOf
                    ? (isReferencedRich ? mappedValueOfApi : "valueOf")
                    : (isReferencedRich ? mappedValuesApi : "values"),
                isValueOf ? args[0] : null);
        else if (isValueOf)
            repl = new JsonObject { ["k"] = "enumParse", ["type"] = tArg?.DeepClone(), ["arg"] = args[0]?.DeepClone() };
        else
            repl = new JsonObject { ["k"] = "enumValues", ["type"] = tArg?.DeepClone() };

        Replace(o, repl);
    }

    // Companions are CLR representations of the selected method's source parameter, not additional
    // enum parameters. Read its ordinary role through the declaration frame, never by physical position.
    static JsonNode SourceArgument(JsonArray arguments, NullableRepresentationFrame frame)
        => frame == null ? arguments.Count == 1 ? arguments[0] : null
            : frame.SourceArity == 1 && arguments.Count == frame.PhysicalArity
                ? arguments[frame.Variable(new TypeNode.Tv("method", 0), NullableRepresentationFrame.Role.Ordinary).I]
                : null;

    internal static void SelfTest()
    {
        var parameter = new TypeNode.Tv("method", 0);
        var genericValues = new JsonObject { ["k"] = "enumValues", ["type"] = TypeJson.Write(parameter) };
        var runtimeLookup = EnumValueLowering.Apply(genericValues, ReferenceMetadataIndex.Build(System.Array.Empty<string>()),
            new HashSet<string>(), new Dictionary<string, BasicEnumMetadata>());
        if (!runtimeLookup || Str(genericValues["k"]) != "cast" || TypeJson.Read(genericValues["type"]) != new TypeNode.Array(parameter)
            || Str(genericValues["e"]?["method"]) != "kotlinEnumValues"
            || Str(genericValues["e"]?["args"]?[0]?["k"]) != "classRef"
            || TypeJson.Read(genericValues["e"]?["args"]?[0]?["type"]) != parameter)
            throw new System.InvalidOperationException("Generic enum values lost their ordered runtime operation or exact type frame");
        var ordinary = TypeJson.Fqn("EnumType");
        var companion = TypeJson.Fqn("StorageType");
        var frame = new NullableRepresentationFrame(1, System.Array.Empty<int>(),
            physicalOrder: new[] { 1, 0 }, storageIndices: new[] { 0 });
        var arguments = new JsonArray(companion, ordinary);
        if (!ReferenceEquals(SourceArgument(arguments, frame), ordinary)
            || SourceArgument(arguments, null) != null
            || SourceArgument(new JsonArray(TypeJson.Fqn("EnumType")), frame) != null
            || NodeType.Of(new JsonObject { ["k"] = "enumParse", ["type"] = ordinary.DeepClone() })
                != TypeJson.Read(ordinary))
            throw new System.InvalidOperationException("Enum intrinsic lost its selected source argument frame");
        System.Console.WriteLine("[enum intrinsic frame] self-test OK (ordinary role, physical permutation, exact arity)");
    }

    static void Replace(JsonObject target, JsonNode replacement)
    {
        foreach (var key in target.Select(kv => kv.Key).ToList()) target.Remove(key);
        foreach (var kv in (JsonObject)replacement) target[kv.Key] = kv.Value?.DeepClone();
    }

    // A rich enum's synthesized API is a real physical static method. State its exact declaration descriptor at the
    // semantic-to-physical boundary so the ordinary referenced-member resolver can select the MethodDef and attach
    // its durable memberRef; argument expression types are never an overload-resolution substitute.
    static JsonObject RichEnumCall(JsonNode owner, string method, JsonNode valueOfArg)
    {
        var valueOf = valueOfArg != null;
        return new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = owner?.DeepClone(),
            ["method"] = method,
            ["args"] = valueOf ? new JsonArray(valueOfArg.DeepClone()) : new JsonArray(),
            ["sig"] = valueOf
                ? new JsonArray(TypeJson.Fqn("System.String"))
                : new JsonArray(),
        };
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
