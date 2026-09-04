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
    static readonly TypeNode TypeN = new TypeNode.Nullable(Type);
    static readonly TypeNode InvocationOutcome = new TypeNode.Fqn(
        "DotKt.Runtime.CompilerServices.StarProjectionInvocationOutcome");
    static HashSet<string> _reservedNames = new(StringComparer.Ordinal);
    static Dictionary<string, TypeNode.Fqn> _closedViewHints = new(StringComparer.Ordinal);
    static Dictionary<string, TypeNode> _dependentLocalTypes = new(StringComparer.Ordinal);
    static List<JsonNode> _materializedDelegateAdapters = new();
    static IReadOnlyDictionary<string, string> _localExistentialOwners =
        new Dictionary<string, string>(StringComparer.Ordinal);
    static int _nextTemp;
    static int _nextDelegateAdapter;
    public static bool UsedRuntimeFallback { get; private set; }

    internal static void RequireRuntimeFallback() => UsedRuntimeFallback = true;

    public static void ApplyAll(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs)
    {
        ApplyAll(roots, localExistentialOwners, refs, resetUsage: true);
    }

    // Some type-parameter calls acquire their final constrained/member owner after the main existential pass. Re-run
    // only the same exact binding once that owner exists, preserving whether the earlier pass already required the
    // runtime support assembly.
    public static void ApplyLate(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs)
    {
        ApplyAll(roots, localExistentialOwners, refs, resetUsage: false);
    }

    static void ApplyAll(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs, bool resetUsage)
    {
        var rootList = roots.ToList();
        if (resetUsage)
        {
            UsedRuntimeFallback = false;
            _nextDelegateAdapter = 0;
        }
        _localExistentialOwners = localExistentialOwners
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _reservedNames = new HashSet<string>(StringComparer.Ordinal);
        _closedViewHints = CollectClosedViewHints(rootList, refs);
        _dependentLocalTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        _nextTemp = 0;
        foreach (var root in rootList) CollectNames(root);
        foreach (var root in rootList)
        {
            _materializedDelegateAdapters = new List<JsonNode>();
            Rewrite(root, refs);
            var materializedTypes = ClosureSynthesis.ApplyMaterialized(root, _materializedDelegateAdapters, refs);
            // Only these declarations were born after the main existential/type-projection pass. Rewriting the whole
            // file here would revisit deliberately opaque results such as Holder<*> and manufacture Holder<object>.
            FBoundStarProjectionErasure.RewriteLateTypes(materializedTypes, _localExistentialOwners, refs);
            foreach (var materializedType in materializedTypes)
                SharedSyntheticSynthesis.DropSyntheticTypeArgs(materializedType);
        }
    }

    static void Rewrite(JsonNode node, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) == "local" && Str(obj["name"]) is string localName
                    && _dependentLocalTypes.TryGetValue(localName, out var localType))
                    obj["sty"] = TypeJson.Write(localType);
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null || key == "name") continue;
                    Rewrite(value, refs);
                }
                if (Str(obj["k"]) == "var" && Str(obj["name"]) is string declaredName
                    && obj["init"] is JsonNode init && NodeType.Of(init) is TypeNode initType
                    && IsForeignStarType(initType, refs))
                {
                    obj["type"] = TypeJson.Write(initType);
                    _dependentLocalTypes[declaredName] = initType;
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
                    if (ContainsSyntheticClass(obj))
                        _materializedDelegateAdapters.Add(obj);
                }
                break;
            case JsonArray array:
                foreach (var value in array.ToList()) if (value != null) Rewrite(value, refs);
                break;
        }
    }

    static bool ContainsSyntheticClass(JsonNode node) => node switch
    {
        JsonObject obj => obj["synthClass"] != null
            || obj.Any(pair => pair.Value != null && ContainsSyntheticClass(pair.Value)),
        JsonArray array => array.Any(value => value != null && ContainsSyntheticClass(value)),
        _ => false,
    };

    static bool TryRewriteClassifier(JsonObject obj, ReferenceMetadataIndex refs, out JsonObject rewritten)
    {
        rewritten = null;
        var kind = Str(obj["k"]);
        if (kind is not ("isInst" or "isInstRef" or "cast") || obj["e"] is not JsonNode operand
            || !TryForeignStarOwner(TypeJson.Read(obj["type"]), refs, out var owner, out var nullable)) return false;
        if (refs.IsByRefLikeFqn(owner))
            throw new NotSupportedException(
                $"bir2cir: foreign byref-like generic star projection `{owner.Name}<*>` has no boxable CLR existential representation");

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
        if (kind is not ("callInstance" or "clrInstance" or "clrGenericInstance" or "clrPropGet" or "clrPropSet"
                or "constrainedCall")
            || Flag(obj["static"]) || obj["recv"] is not JsonNode receiver
            || TypeJson.Read(kind == "constrainedCall" ? obj["iface"] : obj["ownerType"] ?? obj["type"])
                is not TypeNode.Fqn authoredOwner)
        {
            return false;
        }
        TypeNode.Fqn owner;
        if (!TryForeignStarOwner(authoredOwner, refs, out owner, out _))
        {
            var receiverType = NodeType.Of(receiver);
            if (!TryForeignStarOwner(receiverType, refs, out owner, out _)) return false;
        }
        if (refs.IsByRefLikeFqn(owner))
            throw new NotSupportedException(
                $"bir2cir: foreign byref-like generic star projection `{owner.Name}<*>` has no boxable CLR existential representation");
        var valueReceiver = refs.IsValueType(owner);

        var signature = ((obj["sig"] ?? obj["argTypes"] ?? obj["resolvedMemberParams"]) as JsonArray)?.Select(TypeJson.Read).ToArray();
        if (signature == null && kind == "clrPropSet" && obj["value"] is JsonNode setValue)
            signature = new[] { NodeType.Of(setValue) };
        signature ??= Array.Empty<TypeNode>();
        if (signature.Any(t => t == null))
            throw new NotSupportedException($"bir2cir: foreign star call `{owner.Name}.{sourceName}` has an incomplete signature");
        var methodArity = (obj["typeArgs"] as JsonArray)?.Count ?? 0;
        var methodFound = refs.TryForeignStarMethod(owner, sourceName, propertyAccess, methodArity, signature,
            out var openType, out var declaringView, out var token, out var runtimeName, out var runtimeParameterKeys,
            out var declarationReturn, out var returnsVoid);
        if (!methodFound && kind is "clrPropGet" or "clrPropSet"
            && refs.TryForeignStarField(owner, sourceName, out openType, out declaringView, out token,
                out var fieldDeclarationType))
        {
            var fieldClosedViewHint = ClosedViewHint(receiver, owner, declaringView);
            var fieldCall = kind == "clrPropGet"
                ? Call("starProjectionGetField", new TypeNode[] { Any, Type, TypeN, Int, String }, AnyN,
                    receiver.DeepClone(), ClassRef(openType), fieldClosedViewHint,
                    new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = token },
                    new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = sourceName })
                : Call("starProjectionSetField", new TypeNode[] { Any, Type, TypeN, Int, String, AnyN },
                    new TypeNode.Fqn("kotlin.Unit"), receiver.DeepClone(), ClassRef(openType), fieldClosedViewHint,
                    new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = token },
                    new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = sourceName },
                    obj["value"]?.DeepClone());
            if (kind == "clrPropSet")
            {
                rewritten = valueReceiver
                    ? BuildValueReceiverFieldSet(receiver, openType, fieldClosedViewHint, token, sourceName, obj["value"])
                    : fieldCall;
                return true;
            }
            var fieldSemanticResult = TypeJson.Read(obj["ret"]) ?? NodeType.Of(obj);
            var fieldAdapterResult = SubstituteProjectedOwnerSlots(fieldSemanticResult, owner.Args);
            if (UnwrapFunction(fieldDeclarationType) is TypeNode.Fn
                && DependsOnProjectedOwnerSlot(fieldDeclarationType, owner.Args)
                && UnwrapFunction(fieldAdapterResult) is TypeNode.Fn fieldFunction)
            {
                rewritten = WrapDependentDelegateResult(fieldCall, fieldAdapterResult, fieldFunction);
                return true;
            }
            var fieldResult = ProjectResult(fieldSemanticResult, owner.Args, refs);
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
        var closedViewHint = ClosedViewHint(receiver, owner, declaringView);
        if (signature.Any(t => t is TypeNode.ByRef))
            throw new NotSupportedException(
                $"bir2cir: foreign star member `{owner.Name}.{sourceName}` has ref/out parameters; "
                + "the object[] reflection ABI cannot preserve managed-reference aliasing");
        if (declarationReturn is TypeNode.ByRef)
            throw new NotSupportedException(
                $"bir2cir: foreign star member `{owner.Name}.{sourceName}` has a ref return; "
                + "the object reflection ABI cannot preserve lvalue identity");
        if (signature.Any(t => ContainsByRefLike(t, refs)) || ContainsByRefLike(declarationReturn, refs))
            throw new NotSupportedException(
                $"bir2cir: foreign star member `{owner.Name}.{sourceName}` contains a byref-like parameter or result; "
                + "the object reflection ABI cannot box that signature");

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
        JsonObject RuntimeInvoke(string runtimeMethod, TypeNode result, JsonNode runtimeReceiver, JsonNode runtimeArguments) =>
            Call(runtimeMethod,
            new TypeNode[] { Any, Type, TypeN, Int, String, Int, new TypeNode.Array(String),
                new TypeNode.Array(Type), new TypeNode.Array(AnyN) },
            result,
            runtimeReceiver, ClassRef(openType), closedViewHint.DeepClone(),
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = token },
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = runtimeName },
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = methodArity },
            new JsonObject { ["k"] = "newArray", ["elem"] = TypeJson.Write(String), ["elems"] = parameterKeys },
            new JsonObject { ["k"] = "newArray", ["elem"] = TypeJson.Write(Type), ["elems"] = methodTypes },
            runtimeArguments);

        // `sty` is the sole result carrier on some generic calls. Preserve the raw result for ordinary projection:
        // a nested Holder<T> must remain one opaque object, never the fictitious Holder<object>. A delegate is the
        // exceptional readable surface we implement with an adapter, so close only that adapter's owner slots exactly
        // once. An argument such as `out Tcaller` then remains in the caller frame instead of becoming foreign !0.
        var semanticResult = TypeJson.Read(obj["ret"]) ?? NodeType.Of(obj);
        var adapterResult = SubstituteProjectedOwnerSlots(semanticResult, owner.Args);
        var semanticFunction = UnwrapFunction(adapterResult);
        var adaptDelegate = UnwrapFunction(declarationReturn) is TypeNode.Fn declarationFunction
            && !declarationFunction.Suspend
            && DependsOnProjectedOwnerSlot(declarationReturn, owner.Args)
            && semanticFunction != null;

        if (valueReceiver)
        {
            var valueInvocation = BuildValueReceiverInvocation(receiver, arguments, signature, returnsVoid,
                projectedResult: adaptDelegate ? AnyN : ProjectResult(semanticResult, owner.Args, refs),
                RuntimeInvoke);
            rewritten = adaptDelegate
                ? WrapDependentDelegateResult(valueInvocation, adapterResult, semanticFunction)
                : valueInvocation;
            return true;
        }

        var invoke = RuntimeInvoke(returnsVoid ? "starProjectionInvokeUnit" : "starProjectionInvoke",
            returnsVoid ? new TypeNode.Fqn("kotlin.Unit") : AnyN,
            receiver.DeepClone(),
            new JsonObject { ["k"] = "newArray", ["elem"] = TypeJson.Write(AnyN), ["elems"] = arguments });
        if (returnsVoid)
        {
            rewritten = invoke;
            return true;
        }

        var projectedResult = ProjectResult(semanticResult, owner.Args, refs);
        if (adaptDelegate)
        {
            rewritten = WrapDependentDelegateResult(invoke, adapterResult, semanticFunction);
            return true;
        }
        rewritten = IsObjectish(projectedResult) ? invoke : new JsonObject
        {
            ["k"] = "cast", ["type"] = TypeJson.Write(projectedResult), ["e"] = invoke,
        };
        return true;
    }

    static TypeNode.Fn UnwrapFunction(TypeNode type) => type switch
    {
        TypeNode.Fn function => function,
        TypeNode.Nullable n => UnwrapFunction(n.Of),
        TypeNode.Oblivious o => UnwrapFunction(o.Of),
        _ => null,
    };

    static JsonObject WrapDependentDelegateResult(JsonNode value, TypeNode semanticResult, TypeNode.Fn target)
    {
        if (semanticResult is not (TypeNode.Nullable or TypeNode.Oblivious))
            return BuildDelegateResultAdapter(value, target);

        var temp = FreshTemp();
        JsonObject Temp() => new() { ["k"] = "local", ["name"] = temp };
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray(new JsonObject
            {
                ["k"] = "var", ["name"] = temp, ["type"] = TypeJson.Write(AnyN),
                ["init"] = value.DeepClone(),
            }),
            ["result"] = new JsonObject
            {
                ["k"] = "cond",
                ["cond"] = new JsonObject
                {
                    ["k"] = "objEq", ["lhs"] = Temp(),
                    ["rhs"] = new JsonObject
                    {
                        ["k"] = "const", ["type"] = TypeJson.Write(AnyN), ["value"] = null,
                    },
                },
                ["then"] = new JsonObject
                {
                    ["k"] = "const", ["type"] = TypeJson.Write(semanticResult), ["value"] = null,
                },
                ["else"] = BuildDelegateResultAdapter(Temp(), target),
            },
        };
    }

    // The actual delegate closes over an existential owner argument and therefore has no statically nameable CLR
    // type in this compilation. Capture it as object and expose the frontend-authored safe function surface through
    // a generated closure. DynamicInvoke is confined to the runtime boundary; every caller and emitted Invoke body
    // retains an exact, verifiable signature.
    static JsonObject BuildDelegateResultAdapter(JsonNode value, TypeNode.Fn target)
    {
        var name = FreshDelegateAdapterName();
        var owner = TypeJson.Write(new TypeNode.Fqn(name));
        var targetType = TypeJson.Write(target);
        var fields = new JsonArray
        {
            new JsonObject { ["name"] = "source", ["type"] = TypeJson.Write(AnyN) },
        };
        var parameters = new JsonArray();
        var invokeArguments = new JsonArray();
        for (var index = 0; index < target.DelegateParams.Length; index++)
        {
            var parameterName = "p" + index;
            parameters.Add(new JsonObject
            {
                ["name"] = parameterName,
                ["type"] = TypeJson.Write(target.DelegateParams[index]),
            });
            invokeArguments.Add(new JsonObject { ["k"] = "local", ["name"] = parameterName });
        }
        var sourceRead = new JsonObject
        {
            ["k"] = "field", ["sty"] = TypeJson.Write(AnyN), ["ownerType"] = owner.DeepClone(),
            ["recv"] = new JsonObject { ["k"] = "this" }, ["name"] = "source",
        };
        var dynamicInvoke = Call("starProjectionInvokeDelegate",
            new TypeNode[] { AnyN, new TypeNode.Array(AnyN) }, AnyN,
            sourceRead,
            new JsonObject { ["k"] = "newArray", ["elem"] = TypeJson.Write(AnyN), ["elems"] = invokeArguments });
        var body = new JsonArray();
        if (target.Ret is TypeNode.Fqn { Args: null, Name: "kotlin.Unit" or "void" })
        {
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = dynamicInvoke });
            body.Add(new JsonObject { ["k"] = "return" });
        }
        else
        {
            JsonNode result = IsObjectish(target.Ret) ? dynamicInvoke : new JsonObject
            {
                ["k"] = "cast", ["type"] = TypeJson.Write(target.Ret), ["e"] = dynamicInvoke,
            };
            body.Add(new JsonObject { ["k"] = "return", ["value"] = result });
        }

        var typeArguments = new List<TypeNode.Tv>();
        CollectTypeVariables(target, typeArguments);
        var synthClass = new JsonObject
        {
            ["name"] = name, ["fields"] = fields, ["params"] = parameters,
            ["ret"] = TypeJson.Write(target.Ret), ["body"] = body,
        };
        if (typeArguments.Count > 0)
            synthClass["typeParams"] = new JsonArray(typeArguments.Select((_, index) =>
                (JsonNode)JsonValue.Create("T" + index)).ToArray());
        var closure = new JsonObject
        {
            ["k"] = "newClosure", ["closureType"] = owner, ["captures"] = new JsonArray { value.DeepClone() },
            ["method"] = "invoke", ["funcType"] = targetType, ["synthClass"] = synthClass,
        };
        if (typeArguments.Count > 0)
            closure["typeArgs"] = new JsonArray(typeArguments.Select(TypeJson.Write).ToArray());
        return closure;
    }

    static void CollectTypeVariables(TypeNode type, List<TypeNode.Tv> result)
    {
        switch (type)
        {
            case TypeNode.Tv tv:
                if (!result.Contains(tv)) result.Add(tv);
                break;
            case TypeNode.Fqn { Args: { } args }:
                foreach (var argument in args) CollectTypeVariables(argument, result);
                break;
            case TypeNode.Projection p: CollectTypeVariables(p.Of, result); break;
            case TypeNode.Nullable n: CollectTypeVariables(n.Of, result); break;
            case TypeNode.Oblivious o: CollectTypeVariables(o.Of, result); break;
            case TypeNode.Array a: CollectTypeVariables(a.Elem, result); break;
            case TypeNode.ByRef b: CollectTypeVariables(b.Of, result); break;
            case TypeNode.Ptr p: CollectTypeVariables(p.Of, result); break;
            case TypeNode.Mod m: CollectTypeVariables(m.M, result); CollectTypeVariables(m.Of, result); break;
            case TypeNode.Fn function:
                CollectTypeVariables(function.Ret, result);
                foreach (var parameter in function.DelegateParams) CollectTypeVariables(parameter, result);
                if (function.Ctx != null)
                    foreach (var context in function.Ctx) CollectTypeVariables(context, result);
                break;
        }
    }

    static string FreshDelegateAdapterName()
    {
        string name;
        do name = "dotkt$ForeignProjectionDelegateAdapter$" + _nextDelegateAdapter++;
        while (!_reservedNames.Add(name));
        return name;
    }

    static JsonObject BuildValueReceiverFieldSet(JsonNode receiver, string openType, JsonNode closedViewHint,
        int token, string sourceName, JsonNode value)
    {
        var pointer = FreshTemp();
        var boxed = FreshTemp();
        JsonObject BoxedLocal() => new() { ["k"] = "local", ["name"] = boxed };
        var unit = new TypeNode.Fqn("kotlin.Unit");
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray(
                new JsonObject
                {
                    ["k"] = "var", ["name"] = pointer,
                    ["type"] = TypeJson.Write(new TypeNode.ByRef(Any)),
                    ["init"] = new JsonObject { ["k"] = "byrefOf", ["inner"] = receiver.DeepClone() },
                },
                new JsonObject
                {
                    ["k"] = "var", ["name"] = boxed, ["type"] = TypeJson.Write(Any),
                    ["init"] = Call("starProjectionCloneValue", new[] { Any }, Any,
                        new JsonObject { ["k"] = "byrefLoad", ["local"] = pointer, ["elem"] = TypeJson.Write(Any) }),
                },
                new JsonObject
                {
                    ["k"] = "exprStmt",
                    ["expr"] = Call("starProjectionSetField", new TypeNode[] { Any, Type, TypeN, Int, String, AnyN }, unit,
                        BoxedLocal(), ClassRef(openType), closedViewHint.DeepClone(),
                        new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = token },
                        new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = sourceName },
                        value?.DeepClone()),
                },
                new JsonObject
                {
                    ["k"] = "exprStmt",
                    ["expr"] = new JsonObject
                    {
                        ["k"] = "byrefStore", ["local"] = pointer, ["elem"] = TypeJson.Write(Any),
                        ["value"] = BoxedLocal(),
                    },
                }),
            ["result"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(unit), ["value"] = null },
        };
    }

    static JsonObject BuildValueReceiverInvocation(JsonNode receiver, JsonArray arguments, TypeNode[] signature,
        bool returnsVoid, TypeNode projectedResult,
        Func<string, TypeNode, JsonNode, JsonNode, JsonObject> runtimeInvoke)
    {
        if (arguments.Count != signature.Length)
            throw new NotSupportedException("bir2cir: foreign star value-receiver call lost its positional argument vector");
        if (signature.Any(t => t is TypeNode.ByRef))
            throw new InvalidOperationException("bir2cir: foreign star ref/out signature reached value-receiver lowering");

        var statements = new JsonArray();
        var receiverPointer = FreshTemp();
        statements.Add(new JsonObject
        {
            ["k"] = "var", ["name"] = receiverPointer,
            ["type"] = TypeJson.Write(new TypeNode.ByRef(Any)),
            ["init"] = new JsonObject { ["k"] = "byrefOf", ["inner"] = receiver.DeepClone() },
        });

        var receiverTemp = FreshTemp();
        JsonNode receiverValue = new JsonObject
        {
            ["k"] = "byrefLoad", ["local"] = receiverPointer, ["elem"] = TypeJson.Write(Any),
        };
        receiverValue = Call("starProjectionCloneValue", new[] { Any }, Any, receiverValue);
        statements.Add(new JsonObject
        {
            ["k"] = "var", ["name"] = receiverTemp, ["type"] = TypeJson.Write(Any),
            ["init"] = receiverValue,
        });

        var arrayElements = new JsonArray();
        for (var i = 0; i < arguments.Count; i++)
        {
            // Materialize in source order. Reflection receives boxed values only; ref/out signatures have already
            // been refused because object[] cannot preserve aliasing between managed-reference arguments.
            var value = FreshTemp();
            statements.Add(new JsonObject
            {
                ["k"] = "var", ["name"] = value, ["type"] = TypeJson.Write(AnyN),
                ["init"] = arguments[i]?.DeepClone(),
            });
            arrayElements.Add(new JsonObject { ["k"] = "local", ["name"] = value });
        }

        var arrayTemp = FreshTemp();
        statements.Add(new JsonObject
        {
            ["k"] = "var", ["name"] = arrayTemp, ["type"] = TypeJson.Write(new TypeNode.Array(AnyN)),
            ["init"] = new JsonObject
            {
                ["k"] = "newArray", ["elem"] = TypeJson.Write(AnyN), ["elems"] = arrayElements,
            },
        });
        JsonObject ArrayLocal() => new() { ["k"] = "local", ["name"] = arrayTemp };

        var outcomeTemp = FreshTemp();
        statements.Add(new JsonObject
        {
            ["k"] = "var", ["name"] = outcomeTemp, ["type"] = TypeJson.Write(InvocationOutcome),
            ["init"] = runtimeInvoke("starProjectionInvokeCaptured", InvocationOutcome,
                new JsonObject { ["k"] = "local", ["name"] = receiverTemp }, ArrayLocal()),
        });

        statements.Add(new JsonObject
        {
            ["k"] = "exprStmt", ["expr"] = new JsonObject
            {
                ["k"] = "byrefStore", ["local"] = receiverPointer, ["elem"] = TypeJson.Write(Any),
                ["value"] = new JsonObject { ["k"] = "local", ["name"] = receiverTemp },
            },
        });

        var outcome = new JsonObject { ["k"] = "local", ["name"] = outcomeTemp };
        JsonNode result;
        if (returnsVoid)
        {
            // The CLR method and the failure-checking helper both physically return void, while Kotlin Unit is a
            // value when the call appears in an expression. Keep the call signature honest, then materialize Unit.
            statements.Add(new JsonObject
            {
                ["k"] = "exprStmt",
                ["expr"] = Call("starProjectionInvocationUnit", new[] { InvocationOutcome },
                    new TypeNode.Fqn("void"), outcome),
            });
            result = new JsonObject
            {
                ["k"] = "staticField",
                ["ownerType"] = TypeJson.Write(new TypeNode.Fqn("kotlin.Unit")),
                ["name"] = "INSTANCE",
            };
        }
        else
        {
            result = Call("starProjectionInvocationValue", new[] { InvocationOutcome }, AnyN, outcome);
        }
        if (!returnsVoid && !IsObjectish(projectedResult)) result = new JsonObject
        {
            ["k"] = "cast", ["type"] = TypeJson.Write(projectedResult), ["e"] = result,
        };
        return new JsonObject { ["k"] = "valueBlock", ["stmts"] = statements, ["result"] = result };
    }

    static bool TryForeignStarOwner(TypeNode type, ReferenceMetadataIndex refs,
        out TypeNode.Fqn owner, out bool nullable)
    {
        nullable = false;
        while (type is TypeNode.Nullable n) { nullable = true; type = n.Of; }
        while (type is TypeNode.Oblivious o) type = o.Of;
        owner = type as TypeNode.Fqn;
        return owner?.Args is { Length: > 0 } args && args.Any(ContainsExistential)
            && !refs.TryExistentialPhysicalOwner(owner.Name, out _)
            && refs.ResolveForeignProjectionType(owner.Name, args.Length) != null;
    }

    public static bool IsForeignStarType(TypeNode type, ReferenceMetadataIndex refs) =>
        TryForeignStarOwner(type, refs, out _, out _);

    static bool ContainsByRefLike(TypeNode type, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Fqn f => refs.IsByRefLikeFqn(f)
            || (f.Args?.Any(a => ContainsByRefLike(a, refs)) ?? false),
        TypeNode.Projection p => ContainsByRefLike(p.Of, refs),
        TypeNode.Nullable n => ContainsByRefLike(n.Of, refs),
        TypeNode.Oblivious o => ContainsByRefLike(o.Of, refs),
        TypeNode.Array a => ContainsByRefLike(a.Elem, refs),
        TypeNode.ByRef b => ContainsByRefLike(b.Of, refs),
        TypeNode.Fn fn => ContainsByRefLike(fn.Ret, refs)
            || fn.Params.Any(p => ContainsByRefLike(p, refs))
            || (fn.Recv != null && ContainsByRefLike(fn.Recv, refs)),
        _ => false,
    };

    static string OpenType(TypeNode.Fqn owner, ReferenceMetadataIndex refs)
    {
        var type = refs.ResolveForeignProjectionType(owner.Name, owner.Args.Length);
        if (type == null) return null;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        return type.IsGenericTypeDefinition ? type.FullName : null;
    }

    static JsonObject ClassRef(string openType) => new()
    {
        ["k"] = "classRef", ["type"] = TypeJson.Write(new TypeNode.Fqn(openType)),
    };

    static JsonObject ClassRef(TypeNode type) => new()
    {
        ["k"] = "classRef", ["type"] = TypeJson.Write(type),
    };

    static JsonNode ClosedViewHint(JsonNode receiver, TypeNode.Fqn owner, TypeNode declaringView)
    {
        if (receiver is JsonObject { } obj && Str(obj["k"]) == "local"
            && Str(obj["name"]) is string name && _closedViewHints.TryGetValue(name, out var hint)
            && hint.Name == owner.Name
            && hint.Args?.Length == owner.Args?.Length)
        {
            // MetadataLoadContext reports a method declared directly on an open generic as the bare definition.
            // In that case the authored exact closure already is the declaring view; emitting the bare generic as a
            // classRef would be an invalid/absent CLR type token.
            if (declaringView is TypeNode.Fqn { Args: null } direct
                && direct.Name == owner.Name)
                return ClassRef(hint);
            var translated = SubstituteOwnerSlots(declaringView, hint.Args);
            if (translated is TypeNode.Fqn translatedFqn && !ContainsExistential(translatedFqn))
                return ClassRef(translatedFqn);
        }
        return new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(TypeN), ["value"] = null };
    }

    static TypeNode SubstituteOwnerSlots(TypeNode type, IReadOnlyList<TypeNode> arguments) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < arguments.Count => arguments[tv.I],
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(arg => SubstituteOwnerSlots(arg, arguments)).ToArray()),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, SubstituteOwnerSlots(p.Of, arguments)),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteOwnerSlots(n.Of, arguments)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteOwnerSlots(o.Of, arguments)),
        TypeNode.Array a => new TypeNode.Array(SubstituteOwnerSlots(a.Elem, arguments)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteOwnerSlots(b.Of, arguments)),
        _ => type,
    };

    static TypeNode SubstituteProjectedOwnerSlots(TypeNode type, IReadOnlyList<TypeNode> arguments) => type switch
    {
        null => null,
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < arguments.Count => arguments[tv.I] switch
        {
            TypeNode.Star => AnyN,
            TypeNode.Projection projection => ProjectionBound(projection.Of),
            var argument => argument,
        },
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(arg => SubstituteProjectedOwnerSlots(arg, arguments)).ToArray()),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance,
            SubstituteProjectedOwnerSlots(p.Of, arguments)),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteProjectedOwnerSlots(n.Of, arguments)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteProjectedOwnerSlots(o.Of, arguments)),
        TypeNode.Array a => new TypeNode.Array(
            SubstituteProjectedOwnerSlots(a.Elem, arguments), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteProjectedOwnerSlots(b.Of, arguments)),
        TypeNode.Ptr p => new TypeNode.Ptr(SubstituteProjectedOwnerSlots(p.Of, arguments)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req,
            SubstituteProjectedOwnerSlots(m.M, arguments),
            SubstituteProjectedOwnerSlots(m.Of, arguments)),
        TypeNode.Fn function => new TypeNode.Fn(
            function.Suspend,
            SubstituteProjectedOwnerSlots(function.Ret, arguments),
            function.Params?.Select(parameter => SubstituteProjectedOwnerSlots(parameter, arguments)).ToArray(),
            function.Recv == null ? null : SubstituteProjectedOwnerSlots(function.Recv, arguments),
            function.Clr,
            function.Ctx?.Select(context => SubstituteProjectedOwnerSlots(context, arguments)).ToArray()),
        _ => type,
    };

    static TypeNode ProjectionBound(TypeNode type) => type switch
    {
        TypeNode.Projection projection => ProjectionBound(projection.Of),
        TypeNode.Oblivious oblivious => ProjectionBound(oblivious.Of),
        _ => type,
    };

    // A foreign star value remains the original object to preserve Kotlin reference identity. When an immutable local
    // is initialized from one exact CLR closure, carry that frontend-authored view separately to reflection dispatch;
    // this distinguishes `I<X>` and `I<Y>` implemented by the same CLR object without wrapping the value. Mutable
    // locals are deliberately excluded: a compile-time hint would become stale after an assignment.
    static Dictionary<string, TypeNode.Fqn> CollectClosedViewHints(IReadOnlyList<JsonNode> roots,
        ReferenceMetadataIndex refs)
    {
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        void CollectWrites(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) == "setLocal" && Str(obj["name"]) is string name) assigned.Add(name);
                foreach (var child in obj.Select(p => p.Value).Where(v => v != null)) CollectWrites(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(v => v != null)) CollectWrites(child);
        }
        foreach (var root in roots) CollectWrites(root);

        var result = new Dictionary<string, TypeNode.Fqn>(StringComparer.Ordinal);
        var pending = new List<(string Name, TypeNode.Fqn StarOwner, JsonNode Init)>();
        void CollectVars(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) == "var" && Str(obj["name"]) is string name && !assigned.Contains(name)
                    && obj["init"] is JsonNode init
                    && TryForeignStarOwner(TypeJson.Read(obj["type"]), refs, out var starOwner, out _))
                    pending.Add((name, starOwner, init));
                foreach (var child in obj.Select(p => p.Value).Where(v => v != null)) CollectVars(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(v => v != null)) CollectVars(child);
        }
        foreach (var root in roots) CollectVars(root);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, starOwner, init) in pending)
            {
                if (result.ContainsKey(name)) continue;
                TypeNode candidate = NodeType.Of(init);
                while (candidate is TypeNode.Nullable nullable) candidate = nullable.Of;
                while (candidate is TypeNode.Oblivious oblivious) candidate = oblivious.Of;
                TypeNode.Fqn exact = candidate as TypeNode.Fqn;
                if ((exact == null || exact.Args?.Any(ContainsExistential) == true)
                    && init is JsonObject local && Str(local["k"]) == "local"
                    && Str(local["name"]) is string source && result.TryGetValue(source, out var inherited))
                    exact = inherited;
                if (exact?.Args is not { Length: > 0 } args || exact.Name != starOwner.Name
                    || args.Length != starOwner.Args.Length || args.Any(ContainsExistential)
                    || refs.ResolveNetType(exact.Name, args.Length) == null) continue;
                result[name] = exact;
                changed = true;
            }
        }
        return result;
    }

    static JsonObject Call(string method, IReadOnlyList<TypeNode> signature, TypeNode result, params JsonNode[] args) => new()
    {
        ["k"] = "callStatic",
        ["owner"] = TypeJson.Write(new TypeNode.Fqn(RuntimeOwner)),
        ["method"] = method,
        ["sig"] = new JsonArray(signature.Select(TypeJson.Write).ToArray()),
        ["ret"] = TypeJson.Write(result),
        ["args"] = new JsonArray(args),
    };

    // Substitute only a DIRECT readable owner slot. If a projected slot occurs inside another reified construction,
    // replacing it recursively with object would manufacture the same invalid CLR fiction this lowering exists to
    // avoid (`Holder<T>` -> `Holder<object>`, `T[]` -> `object[]`, ...). A trusted DotKt existential can retain that
    // nested classifier; every other dependent construction crosses the reflection boundary as one opaque object.
    static TypeNode ProjectResult(TypeNode type, TypeNode[] ownerArgs, ReferenceMetadataIndex refs)
    {
        if (type == null) return AnyN;
        if (type is TypeNode.Tv { Scope: "type" } tv && tv.I >= 0 && tv.I < ownerArgs.Length)
            return ownerArgs[tv.I] switch
            {
                TypeNode.Star => AnyN,
                TypeNode.Projection { Variance: "in" } => AnyN,
                // The selected argument is written in the CALLER's generic frame. A caller `type:0` is not the
                // foreign declaration's `type:0`; substituting it again recursively either changes its meaning or,
                // for `Foreign<out T>` returning T, recurses forever. Continue projection with no owner frame.
                TypeNode.Projection projectedArgument => ProjectResult(projectedArgument.Of, Array.Empty<TypeNode>(), refs),
                var argument => ProjectResult(argument, Array.Empty<TypeNode>(), refs),
            };
        if (type is TypeNode.Star) return AnyN;
        if (type is TypeNode.Projection projection)
            return projection.Variance == "in" ? AnyN : ProjectResult(projection.Of, ownerArgs, refs);

        if (DependsOnProjectedOwnerSlot(type, ownerArgs))
        {
            if (type is TypeNode.Fqn f)
            {
                if (_localExistentialOwners.TryGetValue(f.Name, out var localExistential))
                    return new TypeNode.Fqn(localExistential);
                if (refs.TryExistentialPhysicalOwner(f.Name, out var referencedExistential))
                    return new TypeNode.Fqn(referencedExistential);
            }
            if (type is TypeNode.Fqn dependentFqn && dependentFqn.Args is { Length: > 0 } dependentArgs
                && refs.ResolveNetType(dependentFqn.Name, dependentArgs.Length) != null)
                return new TypeNode.Fqn(dependentFqn.Name, dependentArgs.Select(a =>
                    DependsOnProjectedOwnerSlot(a, ownerArgs)
                        ? (TypeNode)new TypeNode.Star()
                        : ProjectResult(a, ownerArgs, refs)).ToArray());
            if (type is TypeNode.Nullable dependentNullable)
                return new TypeNode.Nullable(ProjectResult(dependentNullable.Of, ownerArgs, refs));
            if (type is TypeNode.Oblivious dependentOblivious)
                return new TypeNode.Oblivious(ProjectResult(dependentOblivious.Of, ownerArgs, refs));
            return AnyN;
        }

        return type switch
        {
            TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
                args.Select(a => ProjectResult(a, ownerArgs, refs)).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(ProjectResult(n.Of, ownerArgs, refs)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(ProjectResult(o.Of, ownerArgs, refs)),
            TypeNode.Array a => new TypeNode.Array(ProjectResult(a.Elem, ownerArgs, refs)),
            TypeNode.ByRef b => new TypeNode.ByRef(ProjectResult(b.Of, ownerArgs, refs)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, ProjectResult(fn.Ret, ownerArgs, refs),
                fn.Params.Select(p => ProjectResult(p, ownerArgs, refs)).ToArray(),
                fn.Recv == null ? null : ProjectResult(fn.Recv, ownerArgs, refs), fn.Clr,
                fn.Ctx?.Select(c => ProjectResult(c, ownerArgs, refs)).ToArray()),
            _ => type,
        };
    }

    static bool DependsOnProjectedOwnerSlot(TypeNode type, TypeNode[] ownerArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Length
            => ownerArgs[tv.I] is TypeNode.Star or TypeNode.Projection
                || DependsOnProjectedOwnerSlot(ownerArgs[tv.I], ownerArgs),
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(a => DependsOnProjectedOwnerSlot(a, ownerArgs)),
        TypeNode.Nullable n => DependsOnProjectedOwnerSlot(n.Of, ownerArgs),
        TypeNode.Oblivious o => DependsOnProjectedOwnerSlot(o.Of, ownerArgs),
        TypeNode.Array a => DependsOnProjectedOwnerSlot(a.Elem, ownerArgs),
        TypeNode.ByRef b => DependsOnProjectedOwnerSlot(b.Of, ownerArgs),
        TypeNode.Ptr p => DependsOnProjectedOwnerSlot(p.Of, ownerArgs),
        TypeNode.Mod m => DependsOnProjectedOwnerSlot(m.M, ownerArgs)
            || DependsOnProjectedOwnerSlot(m.Of, ownerArgs),
        TypeNode.Fn fn => DependsOnProjectedOwnerSlot(fn.Ret, ownerArgs)
            || fn.Params.Any(p => DependsOnProjectedOwnerSlot(p, ownerArgs))
            || (fn.Recv != null && DependsOnProjectedOwnerSlot(fn.Recv, ownerArgs))
            || (fn.Ctx?.Any(c => DependsOnProjectedOwnerSlot(c, ownerArgs)) ?? false),
        _ => false,
    };

    static bool ContainsExistential(TypeNode type) => type switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsExistential),
        TypeNode.Nullable n => ContainsExistential(n.Of),
        TypeNode.Oblivious o => ContainsExistential(o.Of),
        TypeNode.Array a => ContainsExistential(a.Elem),
        TypeNode.ByRef b => ContainsExistential(b.Of),
        TypeNode.Ptr p => ContainsExistential(p.Of),
        TypeNode.Mod m => ContainsExistential(m.M) || ContainsExistential(m.Of),
        TypeNode.Fn fn => ContainsExistential(fn.Ret) || fn.Params.Any(ContainsExistential)
            || (fn.Recv != null && ContainsExistential(fn.Recv))
            || (fn.Ctx?.Any(ContainsExistential) ?? false),
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
