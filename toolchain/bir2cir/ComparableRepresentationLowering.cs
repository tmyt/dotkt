using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Kotlin Comparable admits both a generic CLR comparison implementation and native CLR enums,
// which only implement non-generic IComparable. A CLR generic constraint cannot express that union.
// Keep the source bound in metadata and author an ordinary, verifier-clean dispatch helper.
static class ComparableRepresentationLowering
{
    static readonly TypeNode Object = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode Result = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode Parameter = new TypeNode.Tv("method", 0);
    static JsonObject Cast(TypeNode type, JsonNode value) => new() {
        ["k"] = "cast", ["type"] = TypeJson.Write(type), ["e"] = value,
    };

    internal static void Apply(IReadOnlyList<JsonNode> roots, bool referenceBuild)
    {
        ConstrainedTypeParameterReceiverBinding.CloseOpenOwners(roots);
        RichEnumComparisonSynthesis.Apply(roots);
        var helperNames = roots.OfType<JsonObject>().Where(root => Text(root["fileClass"]) != null)
            .GroupBy(root => Text(root["fileClass"])).ToDictionary(group => group.Key, group => {
                var names = group.SelectMany(root => (root["methods"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>().Select(method => Text(method["name"]))).ToHashSet();
                var suffix = 0;
                string name;
                do { name = "$comparableDispatch$" + suffix++; } while (names.Contains(name));
                return name;
            });
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.OfType<JsonObject>())
        {
            var owner = Text(root["fileClass"]);
            if (owner == null) continue;
            var methods = root["methods"] as JsonArray ?? new JsonArray();
            if (root["methods"] == null) root["methods"] = methods;
            var helperName = helperNames[owner];
            var identity = "dotkt-comparable-dispatch:" + owner + ":" + helperName;
            var used = false;
            void Rewrite(JsonNode node)
            {
                if (node is JsonArray array)
                {
                    foreach (var child in array.ToArray()) Rewrite(child);
                    return;
                }
                if (node is not JsonObject call) return;
                foreach (var (key, child) in call.ToArray())
                    if (key is not ("attrs" or "overrides")) Rewrite(child);
                if (Text(call["k"]) != "callInstance" || Text(call["method"]) != "compareTo"
                    || ComparisonContract(call) is not TypeNode.Fqn comparable
                    || call["args"] is not JsonArray { Count: 1 } args) return;
                var argument = comparable.Args[0];
                if (argument is TypeNode.Projection projection) argument = projection.Of;
                if (argument is TypeNode.Star) argument = Object;
                var receiver = call["recv"].DeepClone();
                var value = args[0].DeepClone();
                var position = call["pos"]?.DeepClone();
                call.Clear();
                call["k"] = "callStatic";
                call["owner"] = TypeJson.Fqn(owner);
                call["method"] = helperName;
                call[DeclarationIdentityBinding.Key] = identity;
                call["typeArgs"] = new JsonArray(TypeJson.Write(argument));
                call["sig"] = new JsonArray(TypeJson.Write(Object), TypeJson.Write(Parameter));
                call["args"] = new JsonArray(Cast(Object, receiver), value);
                call["ret"] = TypeJson.Write(Result);
                call["sty"] = TypeJson.Write(Result);
                if (position != null) call["pos"] = position;
                used = true;
            }
            Rewrite(root);
            if (used && inserted.Add(owner)) methods.Add(Helper(helperName, identity).ToDom());
            if (!referenceBuild) EraseBounds(root);
        }
    }

    static TypeNode.Fqn ComparisonContract(JsonObject call)
    {
        if (TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn owner) return null;
        if (owner is { Name: "kotlin.Comparable", Args.Length: 1 }) return owner;
        // An erased generic classifier cannot retain the generic comparison slot on its physical owner.
        // The frontend's override contract identifies the Kotlin operation; close that contract in the
        // selected owner's frame before nullable/storage companions change either frame. Ordinary class
        // overrides and explicit super calls retain their selected implementation.
        if (call["super"] != null || call["overrides"] is not JsonArray overrides)
            return null;
        var comparisonSlots = overrides.OfType<JsonObject>()
            .Where(slot => Text(slot["member"]) == "compareTo" && Text(slot["kind"]) == "method");
        // A concrete enum reference selects the final Enum comparison through its concrete owner.
        // Its explicit override chain, not that owner's eventual CLR shape, identifies the operation.
        if (!BirTypeLowering.ErasesGenericApplicationToNonGenericClassifier(owner.Name)
            && !comparisonSlots.Any(slot => TypeJson.Read(slot["owner"])
                is TypeNode.Fqn { Name: "kotlin.Enum", Args.Length: 1 }))
            return null;
        return comparisonSlots
            .Select(slot => TypeJson.Read(slot["owner"]))
            .OfType<TypeNode.Fqn>()
            .Where(contract => contract is { Name: "kotlin.Comparable", Args.Length: 1 })
            .Select(contract => SupertypeGraph.SubstOwnerTvs(contract, owner.Args ?? Array.Empty<TypeNode>()))
            .OfType<TypeNode.Fqn>()
            .DistinctBy(SupertypeGraph.TypeKey)
            .SingleOrDefault();
    }

    static BirFunctionDeclaration Helper(string name, string identity)
    {
        var generic = new TypeNode.Fqn("System.IComparable`1", new[] { Parameter });
        var enumFace = new TypeNode.Fqn(RichEnumComparisonSynthesis.Face);
        BirLocal Read(string local) => new() { Name = local };
        BirCast Convert(TypeNode type, IBirExpression value) => new() { Type = type, E = OptionalField<IBirExpression>.Of(value) };
        BirIsInstance Is(TypeNode type, IBirExpression value) => new() { Type = type, E = OptionalField<IBirExpression>.Of(value) };
        BirConditional Choose(IBirExpression condition, IBirExpression yes, IBirExpression no) => new() {
            Type = Result, Cond = OptionalField<IBirExpression>.Of(condition),
            Then = OptionalField<IBirExpression>.Of(yes), Else = OptionalField<IBirExpression>.Of(no),
        };
        BirNativeInstance Compare(TypeNode owner, TypeNode argument) => new() {
            Type = owner, Method = "CompareTo", Recv = Convert(owner, Read("receiver")),
            ArgTypes = new[] { argument }, Args = new IBirExpression[] { Convert(argument, Read("other")) }, Ret = Result,
        };
        BirCallStatic CompareNativeEnum() => new() {
            Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.EnumRuntimeKt"), Method = "kotlinEnumCompareTo",
            Sig = new[] { Object, Object },
            Args = new IBirExpression[] { Convert(Object, Read("receiver")), Convert(Object, Read("other")) }, Ret = Result,
        };
        BirCallStatic CompareErased() => new() {
            Owner = new TypeNode.Fqn("kotlin.comparisons.ComparisonsKt"), Method = "clrErasedCompareTo",
            Sig = new[] { Object, new TypeNode.Nullable(Object) },
            Args = new IBirExpression[] { Convert(Object, Read("receiver")), Convert(new TypeNode.Nullable(Object), Read("other")) },
            Ret = Result,
        };
        BirConditional FloatingComparison(string primitive, string method, IBirExpression otherwise)
        {
            var floating = new TypeNode.Fqn(primitive);
            var comparison = FaithfulHints.FloatCall(method,
                Convert(floating, Read("receiver")), Convert(floating, Convert(Object, Read("other"))));
            return Choose(new BirConditional {
                    Type = new TypeNode.Fqn("kotlin.Boolean"),
                    Cond = Is(floating, Read("receiver")),
                    Then = Is(floating, Convert(Object, Read("other"))),
                    Else = new BirConstant {
                        Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(false),
                    },
                }, comparison with { Ret = Result, Sty = Result }, otherwise);
        }
        var dispatch = Choose(Is(enumFace, Read("receiver")), new BirCallInstance {
            OwnerType = enumFace, Method = "compareEnumTo", Virtual = true,
            Recv = Convert(enumFace, Read("receiver")), Sig = new[] { Object },
            Args = new IBirExpression[] { Convert(Object, Read("other")) }, Ret = Result,
        }, Choose(Is(generic, Read("receiver")), Compare(generic, Parameter),
            Choose(Is(new TypeNode.Fqn("System.Enum"), Read("receiver")), CompareNativeEnum(), CompareErased())));
        // Kotlin's floating total order precedes native interface dispatch. Both
        // operands must have the same floating type; all other slot selection and
        // invalid-argument behavior remains with the existing native paths.
        return new BirFunctionDeclaration {
            Name = name, DeclarationId = identity, Static = true, Vis = "public", Generated = true,
            Virtual = false, Override = false, Abstract = false,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ret = Result,
            Attrs = new[] { new BirAttribute {
                Attr = new TypeNode.Fqn("System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
                Args = Array.Empty<IBirAttributeValue>(),
            } },
            Params = new[] { new BirParameter { Name = "receiver", Type = Object }, new BirParameter { Name = "other", Type = Parameter } },
            Body = new IBirStatement[] { new BirReturn {
                Value = FloatingComparison("kotlin.Double", "clrDoubleCompare",
                    FloatingComparison("kotlin.Float", "clrFloatCompare", dispatch)),
            } },
        };
    }

    static void EraseBounds(JsonNode node)
    {
        if (node is JsonArray array) { foreach (var child in array) EraseBounds(child); return; }
        if (node is not JsonObject declaration) return;
        if (declaration["k"] == null && declaration["typeParams"] is JsonArray parameters)
        {
            var bounds = new JsonObject();
            var changed = false;
            for (var index = 0; index < parameters.Count; index++)
                if (parameters[index] is JsonObject parameter && parameter["constraints"] is JsonArray constraints
                    && constraints.Count > 0)
                {
                    // This carrier freezes the method's source bounds before companion-frame expansion.
                    // A partial snapshot would make later passes mistake unrelated bounds for already-saved facts.
                    bounds[index.ToString()] = constraints.DeepClone();
                    if (!constraints.Any(bound => TypeJson.Read(bound) is TypeNode.Fqn { Name: "kotlin.Comparable" }))
                        continue;
                    changed = true;
                    parameter["constraints"] = new JsonArray(constraints.Where(bound =>
                        TypeJson.Read(bound) is not TypeNode.Fqn { Name: "kotlin.Comparable" }).Select(bound => bound.DeepClone()).ToArray());
                }
            if (changed)
            {
                if (declaration["kind"] != null)
                    KotlinSupertypesRecord.Merge(declaration, new JsonObject { ["bounds"] = bounds });
                else
                {
                    var payload = Text(declaration[NullableGenericErasure.MethodTypeParameterBoundsPre]) is string existing
                        ? JsonNode.Parse(existing).AsObject() : new JsonObject();
                    var saved = payload["bounds"] as JsonObject;
                    if (saved == null) payload["bounds"] = saved = new JsonObject();
                    foreach (var (index, value) in bounds)
                        if (!saved.ContainsKey(index)) saved[index] = value.DeepClone();
                    declaration[NullableGenericErasure.MethodTypeParameterBoundsPre] = payload.ToJsonString();
                }
            }
        }
        foreach (var (key, child) in declaration.ToArray())
            if (key is not ("attrs" or "overrides")) EraseBounds(child);
    }

    internal static void SelfTest()
    {
        var boundHelpers = new List<BirCallStatic>();
        BirFunctionDeclaration typedHelper = null;
        FaithfulHints.WithHelperBinding(call => {
            boundHelpers.Add(call);
            return call with { DeclarationId = "selected:" + call.Method.Value };
        }, () => typedHelper = Helper("typedComparison", "typed-comparison"));
        var typedDispatch = (BirConditional)((BirReturn)typedHelper.Body.Value.Single()).Value.Value;
        if (typedHelper.DeclarationId.Value != "typed-comparison"
            || typedHelper.TypeParams.Value.Single().Name.Value != "T"
            || boundHelpers.Count != 2
            || boundHelpers.Any(call => call.TypeArgs.IsPresent || call.Sig.Value.Length != 2
                || call.Args.Value.Length != 2 || call.DeclarationId.IsPresent)
            || ((BirCallStatic)typedDispatch.Then.Value).DeclarationId.Value != "selected:clrDoubleCompare"
            || ((BirCallStatic)((BirConditional)typedDispatch.Else.Value).Then.Value).DeclarationId.Value
                != "selected:clrFloatCompare"
            || FaithfulHints.FloatCall("clrDoubleCompare", new BirLocal { Name = "a" },
                new BirLocal { Name = "b" }).DeclarationId.IsPresent)
            throw new InvalidOperationException("Typed comparison construction lost its scoped declaration binding or frame");
        var first = JsonNode.Parse("""
        {"fileClass":"Comparisons","methods":[{"name":"first","static":true,
         "typeParams":[{"name":"T","constraints":[{"t":"fqn","name":"kotlin.Comparable",
          "args":[{"t":"tv","scope":"method","i":0}]}]},
          {"name":"U","constraints":[{"t":"fqn","name":"Box","args":[
           {"t":"nullable","of":{"t":"fqn","name":"kotlin.Int"}}]}]}],
         "params":[],"ret":{"t":"fqn","name":"kotlin.Int"},"body":[{"k":"return","value":{
          "k":"callInstance","ownerType":{"t":"fqn","name":"kotlin.Comparable",
           "args":[{"t":"tv","scope":"method","i":0}]},"method":"compareTo",
          "recv":{"k":"callStatic","method":"ReadLeft"},
          "args":[{"k":"callStatic","method":"ReadRight"}]}}]}]}
        """).AsObject();
        var second = first.DeepClone().AsObject();
        second["methods"][0]["name"] = "second";
        var sourceBounds = first["methods"][0]["typeParams"][0]["constraints"].DeepClone();
        var otherBounds = first["methods"][0]["typeParams"][1]["constraints"].DeepClone();
        Apply(new JsonNode[] { first, second }, referenceBuild: false);
        var helpers = new[] { first, second }.SelectMany(root => root["methods"].AsArray().OfType<JsonObject>())
            .Where(method => Text(method["name"]).StartsWith("$comparableDispatch$", StringComparison.Ordinal)).ToArray();
        if (helpers.Length != 1)
            throw new InvalidOperationException("Comparable dispatch duplicated a shared facade helper");
        foreach (var root in new[] { first, second })
        {
            var method = root["methods"][0];
            var call = method["body"][0]["value"];
            var bounds = JsonNode.Parse(Text(method[NullableGenericErasure.MethodTypeParameterBoundsPre]));
            if (Text(call["k"]) != "callStatic"
                || Text(call["args"][0]["e"]["method"]) != "ReadLeft"
                || Text(call["args"][1]["method"]) != "ReadRight"
                || method["typeParams"][0]["constraints"].AsArray().Count != 0
                || !JsonNode.DeepEquals(bounds["bounds"]["0"], sourceBounds)
                || !JsonNode.DeepEquals(bounds["bounds"]["1"], otherBounds))
                throw new InvalidOperationException("Comparable dispatch lost evaluation or source bound metadata");
        }
        var dispatch = helpers[0]["body"][0]["value"];
        foreach (var (primitive, comparison) in new[] {
            ("kotlin.Double", "clrDoubleCompare"), ("kotlin.Float", "clrFloatCompare") })
        {
            if (TypeJson.OwnerName(dispatch["cond"]["cond"]["type"]) != primitive
                || Text(dispatch["cond"]["cond"]["e"]["name"]) != "receiver"
                || TypeJson.OwnerName(dispatch["cond"]["then"]["type"]) != primitive
                || Text(dispatch["cond"]["then"]["e"]["e"]["name"]) != "other"
                || Text(dispatch["then"]["method"]) != comparison
                || TypeJson.OwnerName(dispatch["then"]["ret"]) != "kotlin.Int"
                || dispatch["then"]["args"].AsArray().Count != 2)
                throw new InvalidOperationException("Comparable dispatch lost Kotlin floating total order");
            dispatch = dispatch["else"];
        }
        var branch = dispatch["else"];
        if (TypeJson.Read(branch["then"]["type"]) is not TypeNode.Fqn { Name: "System.IComparable`1", Args.Length: 1 }
            || TypeJson.OwnerName(branch["else"]["cond"]["type"]) != "System.Enum"
            || Text(branch["else"]["then"]["method"]) != "kotlinEnumCompareTo"
            || TypeJson.OwnerName(branch["else"]["else"]["owner"]) != "kotlin.comparisons.ComparisonsKt"
            || Text(branch["else"]["else"]["method"]) != "clrErasedCompareTo")
            throw new InvalidOperationException("Comparable dispatch lost generic-first/erased dispatch order");
        var inherited = new JsonObject {
            ["ownerType"] = TypeJson.Write(new TypeNode.Fqn("kotlin.Enum", new[] { new TypeNode.Tv("method", 1) })),
            ["overrides"] = new JsonArray(new JsonObject {
                ["owner"] = TypeJson.Write(new TypeNode.Fqn("kotlin.Comparable", new[] { new TypeNode.Tv("type", 0) })),
                ["member"] = "compareTo", ["kind"] = "method",
            }),
        };
        if (ComparisonContract(inherited) is not TypeNode.Fqn { Args.Length: 1 } inheritedOwner
            || inheritedOwner.Args[0] is not TypeNode.Tv { Scope: "method", I: 1 })
            throw new InvalidOperationException("Inherited comparison contract lost the selected owner's frame");
        inherited["ownerType"] = TypeJson.Fqn("AuthoredComparison");
        if (ComparisonContract(inherited) != null)
            throw new InvalidOperationException("Ordinary class comparison lost its selected implementation");
        var enumReference = JsonNode.Parse("""
        {"ownerType":{"t":"fqn","name":"ConcreteEnum"},"overrides":[
          {"owner":{"t":"fqn","name":"kotlin.Enum","args":[{"t":"fqn","name":"ConcreteEnum"}]},
           "member":"compareTo","kind":"method"},
          {"owner":{"t":"fqn","name":"kotlin.Comparable","args":[{"t":"fqn","name":"ConcreteEnum"}]},
           "member":"compareTo","kind":"method"}]}
        """).AsObject();
        if (ComparisonContract(enumReference) is not TypeNode.Fqn { Args.Length: 1 } enumContract
            || TypeJson.OwnerName(TypeJson.Write(enumContract.Args[0])) != "ConcreteEnum")
            throw new InvalidOperationException("Concrete enum reference lost its selected comparison contract");
        enumReference["super"] = TypeJson.Fqn("kotlin.Enum");
        if (ComparisonContract(enumReference) != null)
            throw new InvalidOperationException("Explicit super comparison lost its selected implementation");
        var enumRoot = JsonNode.Parse("""
        {"fileClass":"EnumComparisons","types":[{"name":"RichOrder","kind":"class","interfaces":[],
          "methods":[],"ctors":[{"params":[],"baseArgs":null,"thisArgs":null,"body":[]}],
          "richEnum":{"ordinal":"selectedOrdinalField"}}],"methods":[]}
        """).AsObject();
        Apply(new JsonNode[] { enumRoot }, referenceBuild: false);
        var enumBridge = enumRoot["types"][0]["methods"][0];
        if (BirTypeLowering.PhysicalHead("kotlin.Enum", "System.Object", new[] { Parameter })
                is not TypeNode.Fqn { Name: "System.Object", Args: null }
            || !BirTypeLowering.AliasHeadRequiresSourceIdentity("kotlin.Enum", "System.Object")
            || BirTypeLowering.ProjectedAliasHasReifiedGenericHead("kotlin.Enum", "System.Object",
                new TypeNode[] { new TypeNode.Star() }, new Dictionary<string, string>())
            || Text(enumBridge["body"][0]["value"]["lhs"]["name"]) != "selectedOrdinalField"
            || TypeJson.OwnerName(enumRoot["types"][0]["base"]) != "DotKt.Runtime.CompilerServices.KotlinEnumComparable"
            || enumRoot["types"][0]["ctors"][0]["baseArgs"] is not JsonArray { Count: 0 }
            || enumRoot["types"][0]["ctors"][0]["delegationSig"] is not JsonArray { Count: 0 }
            || !KotlinPropertyAccessors.IsPhysicalSlotBridge(enumBridge.AsObject())
            || Text(enumBridge["clrInterfaceImpls"][0]["member"]) != "compareEnumTo")
            throw new InvalidOperationException("Enum comparison lost the shared value view or the producer's ordinal map");
        Console.WriteLine("[comparable representation] self-test OK (shared facade, evaluation, source bounds, generic-first dispatch, inherited owner frame)");
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
