using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    internal static BirFileDeclaration[] Apply(IReadOnlyList<BirFileDeclaration> roots, bool referenceBuild)
    {
        var enriched = roots.Select(RichEnumComparisonSynthesis.Apply).ToArray();
        var helperNames = enriched.Where(root => root.FileClass.HasValue)
            .GroupBy(root => root.FileClass.Value).ToDictionary(group => group.Key, group => {
                var names = group.SelectMany(root => root.Methods.OrEmpty().Select(method => method.Name.OrDefault())).ToHashSet();
                var suffix = 0;
                string name;
                do { name = "$comparableDispatch$" + suffix++; } while (names.Contains(name));
                return name;
            });
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        return enriched.Select(root =>
        {
            if (!root.FileClass.HasValue) return root;
            var owner = root.FileClass.Value;
            var input = root.Methods.HasValue ? root : root with { Methods = Array.Empty<BirFunctionDeclaration>() };
            var helperName = helperNames[owner];
            var identity = "dotkt-comparable-dispatch:" + owner + ":" + helperName;
            var used = false;
            BirGraphValue Rewrite(BirGraphValue node)
            {
                if (node is BirAttribute or BirOverride) return node;
                node = node.RewriteChildren(Rewrite);
                if (node is not BirCallInstance call || call.Method.OrDefault() != "compareTo"
                    || ComparisonContract(call) is not TypeNode.Fqn comparable
                    || call.Args.OrDefault() is not { Length: 1 } args) return node;
                var argument = comparable.Args[0];
                if (argument is TypeNode.Projection projection) argument = projection.Of;
                if (argument is TypeNode.Star) argument = Object;
                used = true;
                return new BirCallStatic {
                    Owner = new TypeNode.Fqn(owner), Method = helperName, DeclarationId = identity,
                    TypeArgs = new[] { argument }, Sig = new[] { Object, Parameter },
                    Args = new[] { new BirCast { Type = Object, E = call.Recv }, args[0] }, Ret = Result, Sty = Result,
                };
            }
            var rewritten = (BirFileDeclaration)Rewrite(input);
            if (used && inserted.Add(owner)) rewritten = rewritten with { Methods = rewritten.Methods.Value.Append(Helper(helperName, identity)).ToArray() };
            return referenceBuild ? rewritten : (BirFileDeclaration)EraseBounds(rewritten);
        }).ToArray();
    }

    static TypeNode.Fqn ComparisonContract(BirCallInstance call)
    {
        if (call.OwnerType.OrDefault() is not TypeNode.Fqn owner) return null;
        if (owner is { Name: "kotlin.Comparable", Args.Length: 1 }) return owner;
        // An erased generic classifier cannot retain the generic comparison slot on its physical owner.
        // The frontend's override contract identifies the Kotlin operation; close that contract in the
        // selected owner's frame before nullable/storage companions change either frame. Ordinary class
        // overrides and explicit super calls retain their selected implementation.
        if (call.Super.HasValue || !call.Overrides.HasValue)
            return null;
        var comparisonSlots = call.Overrides.Value.Where(slot => slot.Member.OrDefault() == "compareTo" && slot.Kind.OrDefault() == "method");
        // A concrete enum reference selects the final Enum comparison through its concrete owner.
        // Its explicit override chain, not that owner's eventual CLR shape, identifies the operation.
        if (!BirTypeLowering.ErasesGenericApplicationToNonGenericClassifier(owner.Name)
            && !comparisonSlots.Any(slot => slot.Owner.OrDefault()
                is TypeNode.Fqn { Name: "kotlin.Enum", Args.Length: 1 }))
            return null;
        return comparisonSlots
            .Select(slot => slot.Owner.OrDefault())
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

    static BirGraphValue EraseBounds(BirGraphValue node) => SourceConstraintErasure.Apply(node, constraints => {
        var retained = constraints.Where(bound => bound is not TypeNode.Fqn { Name: "kotlin.Comparable" }).ToArray();
        return (retained, retained.Length != constraints.Length);
    }, includeEmptyBounds: false);

    // Authored sparse self-test inputs cross the same typed consumer boundary.
    internal static JsonObject[] ApplyFixtures(IEnumerable<JsonNode> roots, bool referenceBuild)
    {
        var inputs = roots.Select(root => BirGraphJson.ReadFile(Encoding.UTF8.GetBytes(root.ToJsonString(BirJson.Writer)))).ToArray();
        var before = inputs.Select(input => input.ToDom()).ToArray();
        var outputs = Apply(inputs, referenceBuild);
        for (var index = 0; index < inputs.Length; index++)
            if (!JsonNode.DeepEquals(before[index], inputs[index].ToDom()))
                throw new InvalidOperationException("Comparison rewrote its caller-owned typed input graph");
        return outputs.Select(root => root.ToDom()).ToArray();
    }

    internal static void SelfTest()
    {
        var formalOwners = typeof(BirGraphValue).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(BirGraphValue))
            && !type.IsSubclassOf(typeof(BirGraphNode))
            && type.GetProperty("TypeParams")?.PropertyType == typeof(OptionalField<BirTypeParameter[]>))
            .Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        if (!formalOwners.SetEquals(new[] { nameof(BirFunctionDeclaration), nameof(BirClassDefinition), nameof(BirInterfaceDefinition),
            nameof(BirEnumDefinition), nameof(BirClosureDefinition), nameof(BirRefTypeDeclaration), nameof(BirInheritedClassMethod),
            nameof(BirInheritedImplementation), nameof(BirMethodImpl) }))
            throw new InvalidOperationException("Audit comparable bound erasure for the current declaration families");
        SourceBoundsSnapshot.SelfTest();
        var comparable = new TypeNode.Fqn("kotlin.Comparable", new TypeNode[] { new TypeNode.Tv("method", 0) });
        var sibling = new TypeNode.Fqn("Sibling");
        var parameters = new[] {
            new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { comparable, sibling } },
            new BirTypeParameter { Name = "U", Constraints = new TypeNode[] { sibling } },
        };
        foreach (var (declaration, carrier) in new (BirGraphValue, string)[] {
            (new BirFunctionDeclaration { TypeParams = parameters }, "methodTypeParameterBoundsPre"),
            (new BirClassDefinition { Kind = "class", TypeParams = parameters }, "kotlinSupertypesPre"),
            (new BirInterfaceDefinition { Kind = "interface", TypeParams = parameters }, "kotlinSupertypesPre"),
            (new BirEnumDefinition { Kind = "enum", TypeParams = parameters }, "kotlinSupertypesPre"),
            (new BirClosureDefinition { TypeParams = parameters }, "methodTypeParameterBoundsPre"),
            (new BirRefTypeDeclaration { TypeParams = parameters }, "methodTypeParameterBoundsPre"),
            (new BirInheritedClassMethod { TypeParams = parameters }, "methodTypeParameterBoundsPre"),
            (new BirInheritedImplementation { Kind = "method", TypeParams = parameters }, "kotlinSupertypesPre"),
            (new BirMethodImpl { TypeParams = parameters }, "methodTypeParameterBoundsPre"),
        })
        {
            var before = declaration.ToDom();
            var erased = EraseBounds(declaration).ToDom();
            var bounds = JsonNode.Parse(erased[carrier].GetValue<string>())["bounds"];
            var wrong = carrier == "kotlinSupertypesPre" ? "methodTypeParameterBoundsPre" : "kotlinSupertypesPre";
            if (erased.ContainsKey(wrong) || erased["typeParams"][0]["constraints"].AsArray().Count != 1
                || !JsonNode.DeepEquals(bounds["0"], before["typeParams"][0]["constraints"])
                || !JsonNode.DeepEquals(bounds["1"], before["typeParams"][1]["constraints"])
                || !JsonNode.DeepEquals(before, declaration.ToDom()))
                throw new InvalidOperationException("Source bound carrier/ownership changed for " + declaration.GetType().Name);
        }
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
        first["methods"].AsArray().Add(new JsonObject { ["name"] = "$comparableDispatch$0", ["generated"] = false,
            ["params"] = new JsonArray(), ["ret"] = TypeJson.Fqn("kotlin.Unit"), ["body"] = new JsonArray() });
        var rewrittenFixtures = ApplyFixtures(new JsonNode[] { first, second }, referenceBuild: false);
        first = rewrittenFixtures[0]; second = rewrittenFixtures[1];
        var helpers = new[] { first, second }.SelectMany(root => root["methods"].AsArray().OfType<JsonObject>())
            .Where(method => method["generated"]?.GetValue<bool>() == true
                && Text(method["name"]).StartsWith("$comparableDispatch$", StringComparison.Ordinal)).ToArray();
        if (helpers.Length != 1 || Text(helpers[0]["name"]) != "$comparableDispatch$1")
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
        var inherited = new BirCallInstance {
            OwnerType = new TypeNode.Fqn("kotlin.Enum", new[] { new TypeNode.Tv("method", 1) }),
            Overrides = new[] { new BirOverride {
                Owner = new TypeNode.Fqn("kotlin.Comparable", new[] { new TypeNode.Tv("type", 0) }),
                Member = "compareTo", Kind = "method",
            } },
        };
        if (ComparisonContract(inherited) is not TypeNode.Fqn { Args.Length: 1 } inheritedOwner
            || inheritedOwner.Args[0] is not TypeNode.Tv { Scope: "method", I: 1 })
            throw new InvalidOperationException("Inherited comparison contract lost the selected owner's frame");
        inherited = inherited with { OwnerType = new TypeNode.Fqn("AuthoredComparison") };
        if (ComparisonContract(inherited) != null)
            throw new InvalidOperationException("Ordinary class comparison lost its selected implementation");
        var enumReference = new BirCallInstance {
            OwnerType = new TypeNode.Fqn("ConcreteEnum"),
            Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("kotlin.Enum", new TypeNode[] { new TypeNode.Fqn("ConcreteEnum") }),
                Member = "compareTo", Kind = "method" },
                new BirOverride { Owner = new TypeNode.Fqn("kotlin.Comparable", new TypeNode[] { new TypeNode.Fqn("ConcreteEnum") }),
                    Member = "compareTo", Kind = "method" } },
        };
        if (ComparisonContract(enumReference) is not TypeNode.Fqn { Args.Length: 1 } enumContract
            || TypeJson.OwnerName(TypeJson.Write(enumContract.Args[0])) != "ConcreteEnum")
            throw new InvalidOperationException("Concrete enum reference lost its selected comparison contract");
        enumReference = enumReference with { Super = true };
        if (ComparisonContract(enumReference) != null)
            throw new InvalidOperationException("Explicit super comparison lost its selected implementation");
        if (ComparisonContract(enumReference with { Super = false }) != null
            || ComparisonContract(enumReference with { Super = OptionalField<bool>.Null }) == null)
            throw new InvalidOperationException("Comparison contract changed explicit super presence");
        var enumRoot = JsonNode.Parse("""
        {"fileClass":"EnumComparisons","types":[{"name":"RichOrder","kind":"class","interfaces":[],
          "methods":[],"ctors":[{"params":[],"baseArgs":null,"thisArgs":null,"delegationDeclarationId":"replace-base-target","body":[]},
          {"params":[],"baseArgs":null,"thisArgs":[],"delegationSig":[],"delegationDeclarationId":"retain-this-target","body":[]}],
          "richEnum":{"ordinal":"selectedOrdinalField"}}],"methods":[]}
        """).AsObject();
        enumRoot = ApplyFixtures(new JsonNode[] { enumRoot }, referenceBuild: false)[0];
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
            || enumRoot["types"][0]["ctors"][0].AsObject().ContainsKey("delegationDeclarationId")
            || enumRoot["types"][0]["ctors"][1]["baseArgs"] != null
            || enumRoot["types"][0]["ctors"][1]["thisArgs"] is not JsonArray { Count: 0 }
            || Text(enumRoot["types"][0]["ctors"][1]["delegationDeclarationId"]) != "retain-this-target"
            || !KotlinPropertyAccessors.IsPhysicalSlotBridge(enumBridge.AsObject())
            || Text(enumBridge["clrInterfaceImpls"][0]["member"]) != "compareEnumTo")
            throw new InvalidOperationException("Enum comparison lost the shared value view or the producer's ordinal map");
        Console.WriteLine("[comparable representation] self-test OK (shared facade, evaluation, source bounds, generic-first dispatch, inherited owner frame)");
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
