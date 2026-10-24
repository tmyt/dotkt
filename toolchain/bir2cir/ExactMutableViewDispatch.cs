using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// An opaque value slot does not make its ordinary invariant source view unknown. Prove the
// exact eligible native face at runtime, then call the already-selected MethodDef directly.
// Use-site projections and CLR-variant interface tests are not such a proof.
static class ExactMutableViewDispatch
{
    static readonly TypeNode Any = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");

    internal static bool TrySourceView(TypeNode.Fqn owner, ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectValue,
        out TypeNode.Fqn view, out int family)
    {
        family = (KotlinTypeWitness.Flags(owner) & ~1) switch { 4 => 5, 8 => 6, 12 => 4, _ => -1 };
        view = null;
        if (family < 0 || owner.Args == null || AliasVarianceRepresentation.HasSourceVariance(owner, refs)) return false;
        var args = refs.NullableTypeFrames.GetValueOrDefault(owner.Name)?.OrdinaryArguments(owner.Args) ?? owner.Args;
        if (args.Length != 1 || HasProjection(args[0])) return false;
        view = new TypeNode.Fqn(family switch {
            4 => "System.Collections.Generic.ISet`1",
            5 => "System.Collections.Generic.ICollection`1",
            _ => "System.Collections.Generic.IList`1",
        }, args);
        // The guard, cache key and exact MemberRef must agree on the complete physical view.
        // In particular, nested opaque Kotlin values are not a different native construction witness here.
        view = ClrMemberResolution.PhysicalInstanceView(refs, (TypeNode.Fqn)projectValue(view));
        return IsInvariant(view, refs);
    }

    internal static JsonObject Wrap(JsonNode receiver, TypeNode.Fqn view, int family, TypeNode resultType,
        Func<JsonObject, JsonNode> direct, Func<JsonObject, JsonNode> fallback, TypeNode authoredSlot = null)
    {
        var name = CallEvalLowering.FreshBindingId();
        JsonObject Local() => new() { ["k"] = "local", ["name"] = name };
        JsonObject Is(TypeNode type) => new() {
            ["k"] = "isInst", ["type"] = TypeJson.Write(type), ["e"] = Local(),
        };
        JsonNode nativeTest = Is(view);
        if (family == 4)
            nativeTest = Binary("&&", Is(new TypeNode.Fqn("System.Collections.Generic.ICollection`1", view.Args)),
                Binary("||", nativeTest, Is(new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinMutableSetClassifier"))));
        if (authoredSlot != null)
            nativeTest = Binary("&&", new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = Is(authoredSlot) }, nativeTest);
        var eligible = new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.ExactMutableViewCacheKt"),
            ["method"] = "canUseExactMutableView", ["typeArgs"] = new JsonArray(),
            ["sig"] = new JsonArray(TypeJson.Write(Any), TypeJson.Write(new TypeNode.Nullable(Any)), TypeJson.Fqn("kotlin.Int")),
            ["ret"] = TypeJson.Write(Bool),
            ["args"] = new JsonArray(Local(), new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(view) },
                new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = family }),
        };
        // A custom interface cast may execute user code. Check immutable metadata eligibility first,
        // and preserve a null receiver's original argument evaluation before its eventual failure.
        var nonNull = new JsonObject { ["k"] = "binOp", ["op"] = "!=", ["lhs"] = Local(),
            ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(new TypeNode.Nullable(Any)), ["value"] = null } };
        var condition = Binary("&&", nonNull, Binary("&&", eligible, nativeTest));
        var statements = new JsonArray(new JsonObject {
            ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(Any), ["init"] = receiver.DeepClone(),
        });
        JsonNode result;
        if (resultType is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void" })
        {
            statements.Add(new JsonObject {
                ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                    ["cond"] = condition, ["body"] = new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = direct(Local()) }),
                }, new JsonObject {
                    ["else"] = true, ["body"] = new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = fallback(Local()) }),
                }),
            });
            result = new JsonObject { ["k"] = "staticField", ["ownerType"] = TypeJson.Fqn("kotlin.Unit"), ["name"] = "INSTANCE" };
        }
        else result = new JsonObject {
            ["k"] = "cond", ["type"] = TypeJson.Write(resultType), ["cond"] = condition,
            ["then"] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(resultType), ["e"] = direct(Local()) },
            ["else"] = fallback(Local()),
        };
        return new JsonObject { ["k"] = "valueBlock", ["stmts"] = statements, ["result"] = result };
    }

    internal static bool TryCount(JsonObject call, ReferenceMetadataIndex refs, out JsonObject rewritten)
    {
        rewritten = null;
        if (call["method"]?.GetValue<string>() != "projectedViewCountErased"
            || TypeJson.OwnerName(call["owner"]) != "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt"
            || call["args"] is not JsonArray { Count: 3 } arguments
            || arguments[1] is not JsonObject preferred || preferred["k"]?.GetValue<string>() != "classRef"
            || TypeJson.Read(preferred["type"]) is not TypeNode.Fqn view
            || arguments[2]?["value"] is not JsonValue value || !value.TryGetValue<int>(out var family)
            || family is not (4 or 5 or 6) || !IsInvariant(view, refs)
            || !refs.TryForeignStarMethod(view, "get_Count", null, 0, Array.Empty<TypeNode>(),
                out _, out var declaring, out _, out _, out _, out _, out _, out var method)) return false;
        var declaration = CloseDeclaring((TypeNode.Fqn)declaring, view.Args);
        rewritten = Wrap(arguments[0], view, family, TypeJson.Read(call["ret"]) ?? new TypeNode.Fqn("kotlin.Int"),
            receiver => ClrMemberResolution.ExactInstanceCall(refs, method, declaration, receiver, new JsonArray()),
            receiver => {
                var original = (JsonObject)call.DeepClone();
                original["args"][0] = receiver;
                return original;
            });
        return true;
    }

    internal static TypeNode.Fqn CloseDeclaring(TypeNode.Fqn declaring, TypeNode[] args) =>
        new(declaring.Name, declaring.Args == null ? args : declaring.Args.Select(type => Close(type, args)).ToArray());

    internal static bool TryListMutation(JsonObject call, ReferenceMetadataIndex refs, out JsonObject rewritten)
    {
        rewritten = null;
        if (TypeJson.OwnerName(call["owner"]) != "kotlin.collections.ClrCollectionDefaultsKt"
            || call["args"] is not JsonArray arguments
            || arguments.Count < 3 || arguments[1] is not JsonObject preferred
            || preferred["k"]?.GetValue<string>() != "classRef"
            || TypeJson.Read(preferred["type"]) is not TypeNode.Fqn view || !IsInvariant(view, refs)) return false;
        var operation = call["method"]?.GetValue<string>();
        var hasIndex = operation is "clrProjectedListSet" or "clrProjectedListRemoveAt" or "clrProjectedListAddAt";
        var hasElement = operation is "clrProjectedListSet" or "clrProjectedListAdd" or "clrProjectedListAddAt";
        if (!(hasIndex || hasElement) || arguments.Count != 2 + (hasIndex ? 1 : 0) + (hasElement ? 1 : 0)) return false;
        var indexType = new TypeNode.Fqn("System.Int32");
        var nativeName = operation switch {
            "clrProjectedListSet" => "set_Item", "clrProjectedListRemoveAt" => "RemoveAt",
            "clrProjectedListAddAt" => "Insert", _ => "Add",
        };
        var signature = (hasIndex ? new[] { indexType } : Array.Empty<TypeNode>())
            .Concat(hasElement ? view.Args : Array.Empty<TypeNode>()).ToArray();
        if (!refs.TryForeignStarMethod(view, nativeName, null, 0, signature,
                out _, out var declaring, out _, out _, out _, out _, out _, out var mutation)) return false;
        var mutationOwner = CloseDeclaring((TypeNode.Fqn)declaring, view.Args);
        var queryName = operation == "clrProjectedListAdd" ? "get_Count" : "get_Item";
        var needsQuery = operation != "clrProjectedListAddAt";
        System.Reflection.MethodInfo query = null;
        TypeNode queryOwner = null;
        if (needsQuery && !refs.TryForeignStarMethod(view, queryName, null, 0,
                hasIndex ? new[] { indexType } : Array.Empty<TypeNode>(),
                out _, out queryOwner, out _, out _, out _, out _, out _, out query)) return false;
        // Evaluate every supplied value before any native getter/mutation, and share it between both branches.
        var statements = new JsonArray();
        var captured = (JsonArray)arguments.DeepClone();
        var helperArguments = ((JsonArray)call["typeArgs"]).Select(TypeJson.Read).ToArray();
        TypeNode HelperSlot(TypeNode type) => type switch {
            TypeNode.Tv { Scope: "method" } variable => helperArguments[variable.I],
            TypeNode.Fqn { Args: { } nested } named => new TypeNode.Fqn(named.Name, nested.Select(HelperSlot).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(HelperSlot(n.Of)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(HelperSlot(o.Of)),
            TypeNode.Array a => new TypeNode.Array(HelperSlot(a.Elem), a.Rank, a.SzArray),
            _ => type,
        };
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i == 1) continue; // A closed classRef is the immutable compiler-owned preference.
            var name = CallEvalLowering.FreshBindingId();
            statements.Add(new JsonObject {
                ["k"] = "var", ["name"] = name,
                ["type"] = TypeJson.Write(HelperSlot(TypeJson.Read(call["sig"][i]))),
                ["init"] = arguments[i].DeepClone(),
            });
            captured[i] = new JsonObject { ["k"] = "local", ["name"] = name };
        }
        JsonArray NativeArguments() => new(captured.Skip(2).Select(arg => arg.DeepClone()).ToArray());
        JsonNode Query(JsonNode receiver) => ClrMemberResolution.ExactInstanceCall(refs, query,
            CloseDeclaring((TypeNode.Fqn)queryOwner, view.Args), receiver,
            hasIndex ? new JsonArray(captured[2].DeepClone()) : new JsonArray());
        TypeNode slot = operation switch {
            "clrProjectedListSet" => new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinMutableListSetSlot"),
            "clrProjectedListRemoveAt" => new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinMutableListRemoveAtSlot"),
            _ => null,
        };
        // A call without an explicit use-site result still has its selected declaration's result.
        // In particular, void Insert helpers cannot be placed in an object-valued conditional.
        if (!refs.TryResolveStaticMemberSignature(TypeJson.OwnerName(call["owner"]), operation,
                helperArguments.Length, true, ((JsonArray)call["sig"]).Select(TypeJson.Read).ToArray(), null,
                out _, out var helperDeclaration, out _)) return false;
        var helperResult = HelperSlot(ClrMemberResolution.DeclaredMethodReturn(refs, helperDeclaration));
        var resultType = helperResult is TypeNode.Fqn { Name: "void" or "System.Void" }
            ? helperResult : TypeJson.Read(call["ret"]) ?? helperResult;
        var dispatch = Wrap(captured[0], view, 6, resultType,
            receiver => {
                var native = ClrMemberResolution.ExactInstanceCall(refs, mutation, mutationOwner,
                    receiver.DeepClone(), NativeArguments());
                if (!needsQuery) return native;
                var old = CallEvalLowering.FreshBindingId();
                var isAppend = operation == "clrProjectedListAdd";
                return new JsonObject {
                    ["k"] = "valueBlock", ["stmts"] = new JsonArray(
                        new JsonObject { ["k"] = "var", ["name"] = old,
                            ["type"] = TypeJson.Write(isAppend ? indexType : new TypeNode.Nullable(Any)), ["init"] = Query(receiver.DeepClone()) },
                        new JsonObject { ["k"] = "exprStmt", ["expr"] = native }),
                    ["result"] = isAppend
                        ? new JsonObject { ["k"] = "binOp", ["op"] = "!=", ["lhs"] = Query(receiver.DeepClone()),
                            ["rhs"] = new JsonObject { ["k"] = "local", ["name"] = old } }
                        : new JsonObject { ["k"] = "local", ["name"] = old },
                };
            }, receiver => {
                var original = (JsonObject)call.DeepClone();
                original["args"] = captured.DeepClone();
                original["args"][0] = receiver;
                return original;
            }, slot);
        rewritten = new JsonObject { ["k"] = "valueBlock", ["stmts"] = statements, ["result"] = dispatch };
        return true;
    }

    static TypeNode Close(TypeNode type, TypeNode[] args) => type switch {
        TypeNode.Tv { Scope: "type" } variable => args[variable.I],
        TypeNode.Fqn { Args: { } nested } named => new TypeNode.Fqn(named.Name, nested.Select(arg => Close(arg, args)).ToArray()),
        _ => type,
    };

    static bool IsInvariant(TypeNode.Fqn view, ReferenceMetadataIndex refs) =>
        view.Args is { Length: 1 } args && !HasProjection(args[0])
        && refs.ResolveNetType(view.Name, 1) is { IsInterface: true } native
        && native.GetGenericArguments().All(parameter =>
            (parameter.GenericParameterAttributes & GenericParameterAttributes.VarianceMask) == GenericParameterAttributes.None);

    static bool HasProjection(TypeNode type) => type switch {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Nullable n => HasProjection(n.Of), TypeNode.Oblivious o => HasProjection(o.Of),
        TypeNode.Fqn { Args: { } args } => args.Any(HasProjection),
        TypeNode.Array a => HasProjection(a.Elem), TypeNode.ByRef b => HasProjection(b.Of),
        TypeNode.Ptr p => HasProjection(p.Of), TypeNode.Mod m => HasProjection(m.M) || HasProjection(m.Of),
        TypeNode.Fn f => HasProjection(f.Ret) || f.Params.Any(HasProjection)
            || f.Recv != null && HasProjection(f.Recv) || f.Ctx?.Any(HasProjection) == true,
        _ => false,
    };

    static JsonObject Binary(string op, JsonNode lhs, JsonNode rhs) => new() {
        ["k"] = "cond", ["type"] = TypeJson.Write(Bool), ["cond"] = lhs,
        ["then"] = op == "&&" ? rhs : new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Bool), ["value"] = true },
        ["else"] = op == "||" ? rhs : new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Bool), ["value"] = false },
    };

    internal static void SelfTest()
    {
        JsonObject Call(string method) => new() { ["k"] = "callStatic", ["method"] = method, ["args"] = new JsonArray() };
        var view = new TypeNode.Fqn("System.Collections.Generic.IList`1", new TypeNode[] { new TypeNode.Fqn("System.Int32") });
        var block = Wrap(Call("receiverOnce"), view, 6, new TypeNode.Fqn("kotlin.Unit"),
            _ => Call("native"), _ => Call("fallback"));
        var branch = block["stmts"][1];
        if (branch["branches"] is not JsonArray { Count: 2 } branches
            || branches[1]["else"]?.GetValue<bool>() != true
            || branches[1]["body"][0]["expr"]["method"]?.GetValue<string>() != "fallback")
            throw new InvalidOperationException("Exact mutable void dispatch lost its executable fallback branch");
        var condition = branches[0]["cond"];
        if (condition["cond"]?["k"]?.GetValue<string>() != "binOp"
            || condition["cond"]?["op"]?.GetValue<string>() != "!="
            || condition["then"]?["cond"]?["method"]?.GetValue<string>() != "canUseExactMutableView")
            throw new InvalidOperationException("Exact mutable dispatch probed an interface before null/metadata eligibility");
        var receivers = 0;
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (obj["method"]?.GetValue<string>() == "receiverOnce") receivers++;
                if (obj["k"]?.GetValue<string>() == "binOp" && obj["op"]?.GetValue<string>() is "&&" or "||")
                    throw new InvalidOperationException("Exact mutable dispatch used a non-CIR short-circuit operator");
                foreach (var child in obj) Walk(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Walk(child);
        }
        Walk(block);
        if (receivers != 1 || !HasProjection(new TypeNode.Array(new TypeNode.Star())))
            throw new InvalidOperationException("Exact mutable dispatch lost single evaluation or nested projection ownership");
        var closedResult = new TypeNode.Fqn("NestedValue", new TypeNode[] {
            new TypeNode.Tv("type", 0), new TypeNode.Tv("method", 1),
        });
        var closedCall = new JsonObject {
            ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(new TypeNode.Fqn(view.Name, new TypeNode[] { closedResult })),
            ["ret"] = TypeJson.Write(closedResult), ["sty"] = TypeJson.Write(closedResult),
        };
        ConstructedMemberReturnSubstitution.ApplyMaterialized(closedCall);
        if (TypeJson.Read(closedCall["ret"]) != closedResult || TypeJson.Read(closedCall["sty"]) != closedResult)
            throw new InvalidOperationException("Exact native result reapplied a caller-owned generic frame");
        Console.WriteLine("[exact mutable dispatch] self-test OK (short circuit, executable void fallback, single receiver, nested projection)");
    }
}
