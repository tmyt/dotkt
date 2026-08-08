using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A foreign CLR G<X> cannot implement a DotKt-synthesized existential after its assembly has already been emitted,
// and G<object> is a different reified invariant type. Keep a foreign G<*> value in an object slot and route only the
// operations that need its erased classifier through the stdlib runtime. bir2cir resolves every member to an exact
// declaring generic definition + exact member identity (metadata token, with a structural declaration key for a
// ref.dll/runtime twin); the runtime maps that declaration onto the receiver's constructed type and therefore
// performs no overload selection from runtime argument values or Kotlin-semantic inference.
static class ForeignStarProjectionBinding
{
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    static readonly TypeNode Any = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode AnyN = new TypeNode.Nullable(Any);
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode String = new TypeNode.Fqn("kotlin.String");
    static readonly TypeNode Type = new TypeNode.Fqn("System.Type");
    static HashSet<string> _reservedNames = new(StringComparer.Ordinal);
    static int _nextTemp;
    public static bool UsedRuntimeFallback { get; private set; }

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        UsedRuntimeFallback = false;
        _reservedNames = new HashSet<string>(StringComparer.Ordinal);
        _nextTemp = 0;
        foreach (var root in rootList) CollectNames(root);
        foreach (var root in rootList) Rewrite(root, refs);
    }

    static void Rewrite(JsonNode node, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null || key == "name") continue;
                    Rewrite(value, refs);
                }
                if (TryRewriteClassifier(obj, refs, out var classifier))
                {
                    UsedRuntimeFallback = true;
                    Replace(obj, classifier);
                }
                else if (TryRewriteCall(obj, refs, out var call))
                {
                    UsedRuntimeFallback = true;
                    Replace(obj, call);
                }
                break;
            case JsonArray array:
                foreach (var value in array.ToList()) if (value != null) Rewrite(value, refs);
                break;
        }
    }

    static bool TryRewriteClassifier(JsonObject obj, ReferenceMetadataIndex refs, out JsonObject rewritten)
    {
        rewritten = null;
        var kind = Str(obj["k"]);
        if (kind is not ("isInst" or "isInstRef" or "cast") || obj["e"] is not JsonNode operand
            || !TryForeignStarOwner(TypeJson.Read(obj["type"]), refs, out var owner, out var nullable)) return false;

        var openType = OpenType(owner, refs);
        if (openType == null)
            throw new NotSupportedException($"bir2cir: cannot resolve foreign star classifier `{owner.Name}`/{owner.Args.Length}");

        var method = kind switch
        {
            "isInst" => "starProjectionIsInstance",
            "isInstRef" => "starProjectionSafeCast",
            _ => "starProjectionCast",
        };
        var result = kind == "isInst" ? Bool : kind == "cast" ? Any : AnyN;
        JsonObject RuntimeCall(JsonNode value) => Call(method,
            new[] { AnyN, Type }, result,
            value.DeepClone(), ClassRef(openType));

        if (!nullable || kind == "isInstRef")
        {
            rewritten = RuntimeCall(operand);
            return true;
        }

        // Nullable `is/as` admits null. Evaluate an arbitrary operand once, then preserve Kotlin's null branch before
        // asking the non-null foreign classifier runtime.
        var temp = FreshTemp();
        var local = new JsonObject { ["k"] = "local", ["name"] = temp };
        JsonNode whenNull = kind == "isInst"
            ? new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Bool), ["value"] = true }
            : new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(AnyN), ["value"] = null };
        rewritten = new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray(new JsonObject
            {
                ["k"] = "var", ["name"] = temp, ["type"] = TypeJson.Write(AnyN), ["init"] = operand.DeepClone(),
            }),
            ["result"] = new JsonObject
            {
                ["k"] = "cond",
                ["cond"] = new JsonObject
                {
                    ["k"] = "objEq", ["lhs"] = local.DeepClone(),
                    ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(AnyN), ["value"] = null },
                },
                ["then"] = whenNull,
                ["else"] = RuntimeCall(local),
            },
        };
        return true;
    }

    static bool TryRewriteCall(JsonObject obj, ReferenceMetadataIndex refs, out JsonObject rewritten)
    {
        rewritten = null;
        var kind = Str(obj["k"]);
        var propertyAccess = kind switch { "clrPropGet" => "get", "clrPropSet" => "set", _ => Str(obj["prop"]) };
        var sourceName = kind is "clrPropGet" or "clrPropSet" ? Str(obj["name"]) : Str(obj["method"]);
        if (kind is not ("callInstance" or "clrInstance" or "clrGenericInstance" or "clrPropGet" or "clrPropSet")
            || Flag(obj["static"]) || obj["recv"] is not JsonNode receiver
            || TypeJson.Read(obj["ownerType"] ?? obj["type"]) is not TypeNode.Fqn owner
            || !TryForeignStarOwner(owner, refs, out owner, out _))
        {
            return false;
        }

        var signature = ((obj["sig"] ?? obj["argTypes"] ?? obj["memberSig"]) as JsonArray)?.Select(TypeJson.Read).ToArray();
        if (signature == null && kind == "clrPropSet" && obj["value"] is JsonNode setValue)
            signature = new[] { NodeType.Of(setValue) };
        signature ??= Array.Empty<TypeNode>();
        if (signature.Any(t => t == null))
            throw new NotSupportedException($"bir2cir: foreign star call `{owner.Name}.{sourceName}` has an incomplete signature");
        var methodArity = (obj["typeArgs"] as JsonArray)?.Count ?? 0;
        var methodFound = refs.TryForeignStarMethod(owner, sourceName, propertyAccess, methodArity, signature,
            out var openType, out var token, out var runtimeName, out var runtimeParameterKeys,
            out _, out var returnsVoid);
        if (!methodFound && kind is "clrPropGet" or "clrPropSet"
            && refs.TryForeignStarField(owner, sourceName, out openType, out token, out _))
        {
            var fieldCall = kind == "clrPropGet"
                ? Call("starProjectionGetField", new TypeNode[] { Any, Type, Int, String }, AnyN,
                    receiver.DeepClone(), ClassRef(openType),
                    new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = token },
                    new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = sourceName })
                : Call("starProjectionSetField", new TypeNode[] { Any, Type, Int, String, AnyN },
                    new TypeNode.Fqn("kotlin.Unit"), receiver.DeepClone(), ClassRef(openType),
                    new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = token },
                    new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = sourceName },
                    obj["value"]?.DeepClone());
            if (kind == "clrPropSet")
            {
                rewritten = fieldCall;
                return true;
            }
            var fieldResult = ProjectResult(TypeJson.Read(obj["ret"]), owner.Args);
            rewritten = IsObjectish(fieldResult) ? fieldCall : new JsonObject
            {
                ["k"] = "cast", ["type"] = TypeJson.Write(fieldResult), ["e"] = fieldCall,
            };
            return true;
        }
        if (!methodFound)
            throw new NotSupportedException(
                $"bir2cir: cannot bind exact foreign star member `{owner.Name}.{sourceName}`/"
                + $"{signature.Length}<{methodArity}>");

        var methodTypes = new JsonArray();
        if (obj["typeArgs"] is JsonArray typeArgs)
            foreach (var typeArg in typeArgs)
                methodTypes.Add(new JsonObject { ["k"] = "classRef", ["type"] = typeArg?.DeepClone() });
        var arguments = new JsonArray();
        if (obj["args"] is JsonArray args)
            foreach (var argument in args) arguments.Add(argument?.DeepClone());
        if (kind == "clrPropSet" && obj["value"] is JsonNode value)
            arguments.Add(value.DeepClone());

        var parameterKeys = new JsonArray(runtimeParameterKeys.Select(key => (JsonNode)new JsonObject
        {
            ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = key,
        }).ToArray());
        var invoke = Call(returnsVoid ? "starProjectionInvokeUnit" : "starProjectionInvoke",
            new TypeNode[] { Any, Type, Int, String, Int, new TypeNode.Array(String),
                new TypeNode.Array(Type), new TypeNode.Array(AnyN) },
            returnsVoid ? new TypeNode.Fqn("kotlin.Unit") : AnyN,
            receiver.DeepClone(), ClassRef(openType),
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = token },
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = runtimeName },
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = methodArity },
            new JsonObject { ["k"] = "newArray", ["elem"] = TypeJson.Write(String), ["elems"] = parameterKeys },
            new JsonObject { ["k"] = "newArray", ["elem"] = TypeJson.Write(Type), ["elems"] = methodTypes },
            new JsonObject { ["k"] = "newArray", ["elem"] = TypeJson.Write(AnyN), ["elems"] = arguments });
        if (returnsVoid)
        {
            rewritten = invoke;
            return true;
        }

        var projectedResult = ProjectResult(TypeJson.Read(obj["ret"]), owner.Args);
        rewritten = IsObjectish(projectedResult) ? invoke : new JsonObject
        {
            ["k"] = "cast", ["type"] = TypeJson.Write(projectedResult), ["e"] = invoke,
        };
        return true;
    }

    static bool TryForeignStarOwner(TypeNode type, ReferenceMetadataIndex refs,
        out TypeNode.Fqn owner, out bool nullable)
    {
        nullable = false;
        while (type is TypeNode.Nullable n) { nullable = true; type = n.Of; }
        while (type is TypeNode.Oblivious o) type = o.Of;
        owner = type as TypeNode.Fqn;
        return owner?.Args is { Length: > 0 } args && args.Any(ContainsStar)
            && !refs.HasDotKtOwner(owner.Name)
            && !refs.TryExistentialPhysicalOwner(owner.Name, out _)
            && refs.ResolveNetType(owner.Name, args.Length) != null;
    }

    static string OpenType(TypeNode.Fqn owner, ReferenceMetadataIndex refs)
    {
        var type = refs.ResolveNetType(owner.Name, owner.Args.Length);
        if (type == null) return null;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        return type.IsGenericTypeDefinition ? type.FullName : null;
    }

    static JsonObject ClassRef(string openType) => new()
    {
        ["k"] = "classRef", ["type"] = TypeJson.Write(new TypeNode.Fqn(openType)),
    };

    static JsonObject Call(string method, IReadOnlyList<TypeNode> signature, TypeNode result, params JsonNode[] args) => new()
    {
        ["k"] = "callStatic",
        ["owner"] = TypeJson.Write(new TypeNode.Fqn(RuntimeOwner)),
        ["method"] = method,
        ["sig"] = new JsonArray(signature.Select(TypeJson.Write).ToArray()),
        ["ret"] = TypeJson.Write(result),
        ["args"] = new JsonArray(args),
    };

    static TypeNode ProjectResult(TypeNode type, TypeNode[] ownerArgs) => type switch
    {
        null => AnyN,
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Length
            => ownerArgs[tv.I] is TypeNode.Star ? AnyN : ProjectResult(ownerArgs[tv.I], ownerArgs),
        TypeNode.Star => AnyN,
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(a => ProjectResult(a, ownerArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(ProjectResult(n.Of, ownerArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(ProjectResult(o.Of, ownerArgs)),
        TypeNode.Array a => new TypeNode.Array(ProjectResult(a.Elem, ownerArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(ProjectResult(b.Of, ownerArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, ProjectResult(fn.Ret, ownerArgs),
            fn.Params.Select(p => ProjectResult(p, ownerArgs)).ToArray(),
            fn.Recv == null ? null : ProjectResult(fn.Recv, ownerArgs), fn.Clr,
            fn.Ctx?.Select(c => ProjectResult(c, ownerArgs)).ToArray()),
        _ => type,
    };

    static bool ContainsStar(TypeNode type) => type switch
    {
        TypeNode.Star => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsStar),
        TypeNode.Nullable n => ContainsStar(n.Of),
        TypeNode.Oblivious o => ContainsStar(o.Of),
        TypeNode.Array a => ContainsStar(a.Elem),
        TypeNode.ByRef b => ContainsStar(b.Of),
        TypeNode.Fn fn => ContainsStar(fn.Ret) || fn.Params.Any(ContainsStar)
            || (fn.Recv != null && ContainsStar(fn.Recv)),
        _ => false,
    };

    static bool IsObjectish(TypeNode type) => type switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Fqn { Args: null, Name: "kotlin.Any" or "object" or "System.Object" } => true,
        _ => false,
    };

    static void Replace(JsonObject target, JsonObject replacement)
    {
        foreach (var key in target.Select(kv => kv.Key).ToList()) target.Remove(key);
        foreach (var pair in replacement.ToList())
        {
            replacement.Remove(pair.Key);
            target[pair.Key] = pair.Value;
        }
    }

    static void CollectNames(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["name"]) is string name) _reservedNames.Add(name);
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList())
                    CollectNames(child);
                break;
            case JsonArray array:
                foreach (var child in array.Where(v => v != null).ToList()) CollectNames(child);
                break;
        }
    }

    static string FreshTemp()
    {
        string candidate;
        do candidate = "dotkt$foreignStar$value$" + _nextTemp++;
        while (!_reservedNames.Add(candidate));
        return candidate;
    }

    static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static bool Flag(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
}
