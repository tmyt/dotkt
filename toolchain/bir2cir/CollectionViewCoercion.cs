using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materialize every resolved CLR collection-view conversion in CIR.
//
// Kotlin relates its mutable and read-only collection surfaces, while their lowered CLR sibling interfaces are
// unrelated in the CLR type lattice. BirTypeLowering owns that physical projection; this final value-flow pass owns
// the casts the projection requires. It runs only after memberRef and every synthetic declaration are final, so both
// sides of an edge come from CIR facts: declaration slots, lexical storage, or an exact resolved external member.
// ilemit consequently emits ordinary `cast` nodes and has no collection-family vocabulary.
static class CollectionViewCoercion
{
    sealed record MethodShape(string Owner, string Name, int Arity, TypeNode[] Parameters, TypeNode Return);

    sealed class Index
    {
        readonly Dictionary<string, List<MethodShape>> _methods = new(StringComparer.Ordinal);
        readonly Dictionary<string, TypeNode> _fields = new(StringComparer.Ordinal);

        internal static Index Build(IReadOnlyList<JsonNode> roots)
        {
            var result = new Index();
            foreach (var root in roots.OfType<JsonObject>())
            {
                var fileOwner = Str(root["fileClass"]);
                result.AddMembers(fileOwner, root);
                if (root["types"] is JsonArray types)
                    foreach (var type in types.OfType<JsonObject>()) result.AddType(type);
            }
            return result;
        }

        void AddType(JsonObject type)
        {
            var owner = Str(type["name"]);
            AddMembers(owner, type);
            if (type["types"] is JsonArray nested)
                foreach (var child in nested.OfType<JsonObject>()) AddType(child);
        }

        void AddMembers(string owner, JsonObject container)
        {
            if (owner == null) return;
            if (container["fields"] is JsonArray fields)
                foreach (var field in fields.OfType<JsonObject>())
                    if (Str(field["name"]) is string name && TypeJson.Read(field["type"]) is TypeNode type)
                        _fields[Key(owner, name)] = type;
            if (container["methods"] is not JsonArray methods) return;
            foreach (var method in methods.OfType<JsonObject>())
            {
                var name = Str(method["name"]);
                var ret = TypeJson.Read(method["ret"]);
                if (name == null || ret == null || method["params"] is not JsonArray parameters) continue;
                var ps = parameters.OfType<JsonObject>().Select(p => TypeJson.Read(p["type"])).ToArray();
                if (ps.Any(p => p == null)) continue;
                var shape = new MethodShape(owner, name, (method["typeParams"] as JsonArray)?.Count ?? 0, ps, ret);
                var key = Key(owner, name);
                if (!_methods.TryGetValue(key, out var bucket)) _methods[key] = bucket = new List<MethodShape>();
                bucket.Add(shape);
            }
        }

        internal TypeNode Field(string owner, string name)
            => owner != null && name != null && _fields.TryGetValue(Key(owner, name), out var type) ? type : null;

        internal MethodShape Method(JsonObject call)
        {
            var owner = CallOwner(call);
            var name = Str(call["method"]);
            if (owner == null || name == null || !_methods.TryGetValue(Key(owner.Name, name), out var candidates))
                return null;
            var methodArgs = ReadTypes(call["typeArgs"] as JsonArray) ?? Array.Empty<TypeNode>();
            var wanted = ReadTypes(call["sig"] as JsonArray);
            var matches = candidates.Where(c => c.Arity == methodArgs.Length
                && (wanted == null || (c.Parameters.Length == wanted.Length
                    && c.Parameters.Select((p, i) => Close(p, owner.Args, methodArgs) == wanted[i]).All(x => x)))).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        static string Key(string owner, string name) => owner + "\u0000" + name;
    }

    sealed class Scope
    {
        internal readonly Dictionary<string, TypeNode> Locals;
        internal readonly TypeNode Return;
        internal readonly TypeNode Owner;

        internal Scope(TypeNode owner = null, TypeNode ret = null,
            Dictionary<string, TypeNode> locals = null)
        {
            Owner = owner;
            Return = ret;
            Locals = locals ?? new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        }

        internal Scope Frame(JsonObject declaration, TypeNode owner, TypeNode ret)
        {
            var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            if (declaration["params"] is JsonArray parameters)
                foreach (var parameter in parameters.OfType<JsonObject>())
                    if (Str(parameter["name"]) is string name && TypeJson.Read(parameter["type"]) is TypeNode type)
                        locals[name] = type;
            return new Scope(owner, ret, locals);
        }

        internal Scope Copy() => new(Owner, Return, new Dictionary<string, TypeNode>(Locals, StringComparer.Ordinal));
    }

    public static void ApplyAll(IReadOnlyList<JsonNode> roots)
    {
        var index = Index.Build(roots);
        foreach (var root in roots.OfType<JsonObject>()) RewriteDocument(root, index);
    }

    static void RewriteDocument(JsonObject root, Index index)
    {
        var fileOwner = Str(root["fileClass"]);
        RewriteMembers(root, fileOwner == null ? null : new TypeNode.Fqn(fileOwner), index);
        if (root["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) RewriteType(type, index);
    }

    static void RewriteType(JsonObject type, Index index)
    {
        var owner = TypeJson.Read(type["selfType"])
            ?? (Str(type["name"]) is string name ? new TypeNode.Fqn(name) : null);
        RewriteMembers(type, owner, index);
        if (type["types"] is JsonArray nested)
            foreach (var child in nested.OfType<JsonObject>()) RewriteType(child, index);
    }

    static void RewriteMembers(JsonObject container, TypeNode owner, Index index)
    {
        if (container["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>())
                if (field["init"] is JsonNode init && TypeJson.Read(field["type"]) is TypeNode target)
                {
                    var fieldScope = new Scope(owner);
                    var rewritten = Coerce(Rewrite(init, fieldScope, index), target, fieldScope, index);
                    if (!ReferenceEquals(rewritten, init)) field["init"] = rewritten;
                }
        if (container["ctors"] is JsonArray constructors)
            foreach (var constructor in constructors.OfType<JsonObject>())
            {
                var scope = new Scope().Frame(constructor, owner, new TypeNode.Fqn("void"));
                // Constructor delegation is executable CIR too. Its evaluation plan establishes locals consumed by
                // the bare argument vector, so preserve the same order ilemit uses: preStmts, delegation, body.
                if (constructor["preStmts"] is JsonArray pre) RewriteArray(pre, scope, index);
                var arguments = constructor["thisArgs"] as JsonArray ?? constructor["baseArgs"] as JsonArray;
                if (arguments != null)
                {
                    RewriteArray(arguments, scope, index);
                    CoerceVector(arguments, ConstructorParameterTypes(constructor), scope, index);
                }
                if (constructor["body"] is JsonArray body) RewriteArray(body, scope, index);
            }
        if (container["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
            {
                var scope = new Scope().Frame(method, owner, TypeJson.Read(method["ret"]));
                if (method["body"] is JsonArray body) RewriteArray(body, scope, index);
            }
    }

    static JsonNode Rewrite(JsonNode node, Scope scope, Index index)
    {
        if (node is JsonArray array) { RewriteArray(array, scope, index); return array; }
        if (node is not JsonObject obj) return node;

        // A nested declaration owns a fresh parameter/local frame. Synthetic declarations normally live in the
        // module tables by this stage, but localFun remains a declaration object until its own lowering path consumes it.
        if (obj["k"] == null && obj["params"] is JsonArray && obj["body"] is JsonArray nestedBody)
        {
            var nestedScope = new Scope().Frame(obj, scope.Owner, TypeJson.Read(obj["ret"]));
            RewriteArray(nestedBody, nestedScope, index);
            return obj;
        }

        var loopScope = LoopScope(obj, scope);
        foreach (var child in obj.ToList())
        {
            if (child.Value == null) continue;
            var childScope = child.Key == "body" && loopScope != null ? loopScope : scope;
            var rewritten = Rewrite(child.Value, childScope, index);
            if (!ReferenceEquals(rewritten, child.Value)) obj[child.Key] = rewritten;
        }

        CoerceInputs(obj, scope, index);
        return CoerceDeclaredResult(obj, scope, index);
    }

    static void RewriteArray(JsonArray array, Scope scope, Index index)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonNode item)
            {
                var rewritten = Rewrite(item, scope, index);
                if (!ReferenceEquals(rewritten, item)) array[i] = rewritten;
            }
            if (array[i] is JsonObject statement && Str(statement["k"]) == "var"
                && Str(statement["name"]) is string name && TypeJson.Read(statement["type"]) is TypeNode type)
                scope.Locals[name] = type;
        }
    }

    static Scope LoopScope(JsonObject node, Scope scope)
    {
        var kind = Str(node["k"]);
        if (kind is not ("forRange" or "forArray" or "forEachInline" or "forIn")) return null;
        var name = Str(node["var"]);
        if (name == null) return null;
        var type = TypeJson.Read(node["elem"])
            ?? (kind == "forRange" ? new TypeNode.Fqn("System.Int32") : null);
        if (type == null) return null;
        var child = scope.Copy();
        child.Locals[name] = type;
        return child;
    }

    static void CoerceInputs(JsonObject node, Scope scope, Index index)
    {
        var kind = Str(node["k"]);
        switch (kind)
        {
            case "var":
                CoerceSlot(node, "init", TypeJson.Read(node["type"]), scope, index);
                break;
            case "setLocal":
                if (Str(node["name"]) is string local && scope.Locals.TryGetValue(local, out var localType))
                    CoerceSlot(node, "value", localType, scope, index);
                break;
            case "setField": case "setFieldExpr": case "staticFieldSet":
                CoerceSlot(node, "value", FieldTarget(node, scope, index), scope, index);
                break;
            case "return": case "returnExpr":
                CoerceSlot(node, "value", scope.Return, scope, index);
                break;
            case "callStatic": case "callInstance": case "constrainedCall":
            case "clrStatic": case "clrInstance": case "clrGenericStatic": case "clrGenericInstance":
            case "new": case "newClr":
                CoerceArguments(node, ParameterTypes(node), scope, index);
                break;
            case "delegateInvoke":
                if (TypeJson.Read(node["funcType"]) is TypeNode.Fn fn)
                    CoerceArguments(node, fn.DelegateParams, scope, index);
                break;
            case "clrPropSet":
                CoerceSlot(node, "value", FieldTarget(node, scope, index), scope, index);
                break;
            case "arraySet":
                CoerceSlot(node, "value", TypeJson.Read(node["elem"]), scope, index);
                break;
            case "stackSet": case "byrefStore":
                CoerceSlot(node, "value", TypeJson.Read(node["elem"]), scope, index);
                break;
            case "newArray":
                CoerceArray(node["elems"] as JsonArray, TypeJson.Read(node["elem"]), scope, index);
                break;
            case "newList": case "newSet":
                CoerceArray(node["elems"] as JsonArray, TypeJson.Read(node["elem"]), scope, index);
                break;
            case "newMap":
                if (node["entries"] is JsonArray entries)
                    foreach (var entry in entries.OfType<JsonObject>())
                    {
                        CoerceSlot(entry, "key", TypeJson.Read(node["keyType"]), scope, index);
                        CoerceSlot(entry, "value", TypeJson.Read(node["valType"]), scope, index);
                    }
                break;
            case "cond":
                var result = TypeJson.Read(node["type"]);
                CoerceSlot(node, "then", result, scope, index);
                CoerceSlot(node, "else", result, scope, index);
                break;
        }
    }

    static void CoerceArguments(JsonObject node, TypeNode[] targets, Scope scope, Index index)
    {
        if (node["args"] is JsonArray args) CoerceVector(args, targets, scope, index);
    }

    static void CoerceVector(JsonArray args, TypeNode[] targets, Scope scope, Index index)
    {
        if (targets == null || args == null || targets.Length != args.Count) return;
        for (var i = 0; i < args.Count; i++)
            if (args[i] is JsonNode arg)
            {
                var coerced = Coerce(arg, targets[i], scope, index);
                if (!ReferenceEquals(coerced, arg)) args[i] = coerced;
            }
    }

    static void CoerceArray(JsonArray values, TypeNode target, Scope scope, Index index)
    {
        if (values == null || target == null) return;
        for (var i = 0; i < values.Count; i++)
            if (values[i] is JsonNode value)
            {
                var coerced = Coerce(value, target, scope, index);
                if (!ReferenceEquals(coerced, value)) values[i] = coerced;
            }
    }

    static void CoerceSlot(JsonObject owner, string key, TypeNode target, Scope scope, Index index)
    {
        if (target != null && owner[key] is JsonNode value)
        {
            var coerced = Coerce(value, target, scope, index);
            if (!ReferenceEquals(coerced, value)) owner[key] = coerced;
        }
    }

    static JsonNode Coerce(JsonNode value, TypeNode target, Scope scope, Index index)
    {
        // A stamp-less conditional still has an internal verifier merge before its enclosing store/argument/return.
        // An outer cast is too late: normalize the sibling branch itself and state the merge type once an edge proves
        // it. Typed conditionals take the same path earlier through CoerceInputs.
        if (target != null && value is JsonObject conditional && Str(conditional["k"]) == "cond"
            && conditional["type"] == null)
        {
            var thenSeam = CollectionViewFaces.IsViewSeam(ExprType(conditional["then"], scope, index), target);
            var elseSeam = CollectionViewFaces.IsViewSeam(ExprType(conditional["else"], scope, index), target);
            if (thenSeam || elseSeam)
            {
                CoerceSlot(conditional, "then", target, scope, index);
                CoerceSlot(conditional, "else", target, scope, index);
                conditional["type"] = TypeJson.Write(target);
                return conditional;
            }
        }
        var got = ExprType(value, scope, index);
        if (!CollectionViewFaces.IsViewSeam(got, target)) return value;
        return new JsonObject
        {
            ["k"] = "cast",
            ["type"] = TypeJson.Write(target),
            ["e"] = value.DeepClone(),
        };
    }

    static JsonNode CoerceDeclaredResult(JsonObject expression, Scope scope, Index index)
    {
        var declared = TypeJson.Read(expression["ret"]) ?? TypeJson.Read(expression["dynRet"]);
        if (declared == null) return expression;
        var actual = PhysicalResult(expression, scope, index);
        if (!CollectionViewFaces.IsViewSeam(actual, declared)) return expression;
        var physical = expression.DeepClone().AsObject();
        // The inner expression leaves the exact member/declaration result on the CLR stack. Once the caller-facing
        // view moves to the explicit outer cast, every surviving inner result stamp must describe that physical value;
        // otherwise ilemit quite reasonably sees an identity cast and emits no instruction.
        foreach (var key in new[] { "sty", "ret", "dynRet" })
            if (physical[key] != null) physical[key] = TypeJson.Write(actual);
        return new JsonObject
        {
            ["k"] = "cast",
            ["type"] = TypeJson.Write(declared),
            ["e"] = physical,
        };
    }

    static TypeNode ExprType(JsonNode node, Scope scope, Index index)
    {
        if (node is not JsonObject obj) return null;
        var kind = Str(obj["k"]);
        if (kind == "local" && Str(obj["name"]) is string name && scope.Locals.TryGetValue(name, out var local))
            return local;
        if (kind == "this") return scope.Owner;
        if (PhysicalResult(obj, scope, index) is TypeNode physical) return physical;
        return NodeType.Of(obj, child => ExprType(child, scope, index),
            name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var elem) ? elem : null);
    }

    static TypeNode PhysicalResult(JsonObject expression, Scope scope, Index index)
    {
        var kind = Str(expression["k"]);
        if (kind == "cast") return TypeJson.Read(expression["type"]);
        if (ResolvedMember(expression) is JsonObject member
            && kind is "callStatic" or "callInstance" or "constrainedCall"
                or "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance"
                or "clrPropGet" or "field" or "staticField" or "clrStaticField" or "lateinitGet")
            return Close(TypeJson.Read(member["returnType"]), OwnerArgs(member), MethodArgs(expression));
        if (kind is "callStatic" or "callInstance" && index.Method(expression) is MethodShape method)
        {
            var owner = CallOwner(expression);
            return Close(method.Return, owner?.Args, MethodArgs(expression));
        }
        if (kind is "field" or "staticField" or "lateinitGet")
        {
            var owner = FieldOwner(expression, scope, index);
            return Close(index.Field(owner?.Name, Str(expression["name"])), owner?.Args, MethodArgs(expression));
        }
        return null;
    }

    static TypeNode FieldTarget(JsonObject node, Scope scope, Index index)
    {
        if (ResolvedMember(node) is JsonObject member)
        {
            var raw = Str(member["kind"]) == "field"
                ? TypeJson.Read(member["returnType"])
                : ReadTypes(member["parameterTypes"] as JsonArray)?.LastOrDefault();
            return Close(raw, OwnerArgs(member), MethodArgs(node));
        }
        var owner = FieldOwner(node, scope, index);
        var localRaw = TypeJson.Read(node["memberType"]) ?? index.Field(owner?.Name, Str(node["name"]));
        return Close(localRaw, owner?.Args, MethodArgs(node));
    }

    static TypeNode.Fqn FieldOwner(JsonObject node, Scope scope, Index index)
        => TypeJson.Read(node["ownerType"] ?? node["type"]) as TypeNode.Fqn
            ?? ExprType(node["recv"], scope, index) as TypeNode.Fqn;

    static JsonObject ResolvedMember(JsonObject node)
        => node["memberRef"] as JsonObject ?? node["fieldRef"] as JsonObject;

    static TypeNode[] ConstructorParameterTypes(JsonObject constructor)
    {
        if (constructor["baseCtorRef"] is JsonObject member)
        {
            var parameters = ReadTypes(member["parameterTypes"] as JsonArray);
            return parameters?.Select(p => Close(p, OwnerArgs(member), Array.Empty<TypeNode>())).ToArray();
        }
        return ReadTypes(constructor["delegationSig"] as JsonArray);
    }

    static TypeNode[] ParameterTypes(JsonObject node)
    {
        TypeNode[] parameters = null;
        TypeNode[] ownerArgs = null;
        if (node["memberRef"] is JsonObject member)
        {
            parameters = ReadTypes(member["parameterTypes"] as JsonArray);
            ownerArgs = OwnerArgs(member);
        }
        parameters ??= ReadTypes(node["sig"] as JsonArray) ?? ReadTypes(node["argTypes"] as JsonArray);
        if (parameters == null) return null;
        var methodArgs = MethodArgs(node);
        return parameters.Select(p => Close(p, ownerArgs ?? CallOwner(node)?.Args, methodArgs)).ToArray();
    }

    static TypeNode.Fqn CallOwner(JsonObject call)
        => TypeJson.Read(call["ownerType"]) as TypeNode.Fqn
            ?? TypeJson.Read(call["owner"]) as TypeNode.Fqn
            ?? TypeJson.Read(call["type"]) as TypeNode.Fqn
            ?? TypeJson.Read(call["calleeOwner"]) as TypeNode.Fqn;

    static TypeNode[] OwnerArgs(JsonObject member)
        => (TypeJson.Read(member["declaringType"]) as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>();

    static TypeNode[] MethodArgs(JsonObject node)
        => ReadTypes(node["typeArgs"] as JsonArray) ?? Array.Empty<TypeNode>();

    static TypeNode Close(TypeNode type, TypeNode[] ownerArgs, TypeNode[] methodArgs) => type switch
    {
        null => null,
        TypeNode.Tv { Scope: "type" } tv when ownerArgs != null && tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when methodArgs != null && tv.I >= 0 && tv.I < methodArgs.Length => methodArgs[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => Close(a, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Close(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Close(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(Close(a.Elem, ownerArgs, methodArgs), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(Close(b.Of, ownerArgs, methodArgs)),
        TypeNode.Ptr p => new TypeNode.Ptr(Close(p.Of, ownerArgs, methodArgs)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, Close(m.M, ownerArgs, methodArgs), Close(m.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Close(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(p => Close(p, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : Close(fn.Recv, ownerArgs, methodArgs), fn.Clr,
            fn.Ctx?.Select(p => Close(p, ownerArgs, methodArgs)).ToArray()),
        _ => type,
    };

    static TypeNode[] ReadTypes(JsonArray array)
    {
        if (array == null) return null;
        var result = new TypeNode[array.Count];
        for (var i = 0; i < array.Count; i++)
            if ((result[i] = TypeJson.Read(array[i])) == null) return null;
        return result;
    }

    static string Str(JsonNode node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
