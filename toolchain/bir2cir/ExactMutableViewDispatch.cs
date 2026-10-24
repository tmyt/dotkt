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

    internal static bool TrySourceView(TypeNode.Fqn owner, ReferenceMetadataIndex refs,
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
        return IsInvariant(view, refs);
    }

    internal static JsonObject Wrap(JsonNode receiver, TypeNode.Fqn view, int family, TypeNode resultType,
        Func<JsonObject, JsonNode> direct, Func<JsonObject, JsonNode> fallback)
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
        var eligible = new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.ExactMutableViewCacheKt"),
            ["method"] = "canUseExactMutableView", ["typeArgs"] = new JsonArray(),
            ["sig"] = new JsonArray(TypeJson.Write(Any), TypeJson.Write(new TypeNode.Nullable(Any)), TypeJson.Fqn("kotlin.Int")),
            ["ret"] = TypeJson.Write(Bool),
            ["args"] = new JsonArray(Local(), new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(view) },
                new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = family }),
        };
        var condition = Binary("&&", nativeTest, eligible);
        var statements = new JsonArray(new JsonObject {
            ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(Any), ["init"] = receiver.DeepClone(),
        });
        JsonNode result;
        if (resultType is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void" })
        {
            statements.Add(new JsonObject {
                ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                    ["cond"] = condition, ["body"] = new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = direct(Local()) }),
                }), ["else"] = new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = fallback(Local()) }),
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
        _ => false,
    };

    static JsonObject Binary(string op, JsonNode lhs, JsonNode rhs) => new() {
        ["k"] = "binOp", ["op"] = op, ["lhs"] = lhs, ["rhs"] = rhs,
    };
}
