using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materialize representation-induced value conversions in CIR.
//
// Kotlin relates its mutable and read-only collection surfaces, while their lowered CLR sibling interfaces are
// unrelated in the CLR type lattice. BirTypeLowering owns that physical projection; this final value-flow pass owns
// the casts the projection requires. It runs only after memberRef and every synthetic declaration are final, so both
// sides of an edge come from CIR facts: declaration slots, lexical storage, or an exact resolved external member.
// Owner-dependent Kotlin constraints likewise do not imply a physical CLR relation between two generic slots.
// A value crossing those slots needs an explicit boxed conversion, even when Kotlin proved the assignment legal.
// ilemit consequently emits ordinary `cast` nodes without recovering source constraints or collection vocabulary.
// A physical void invocation likewise needs an explicit Unit value when its result is consumed. Preserve the call
// as an expression statement and read the resolved singleton afterwards; discarded calls remain ordinary void calls.
static class PhysicalValueCoercion
{
    static bool NeedsConversion(TypeNode source, TypeNode target, Index index) =>
        source is TypeNode.Tv && target is TypeNode.Tv && !source.Equals(target)
        || NeedsBox(target, index.IsValue)
            && source is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
        // Erasure also crosses reference slots: an object value is not yet the
        // exact delegate, array, or interface consumed by a resolved CLR edge.
        || source is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
            && IsReferenceSlot(target, index.IsValue)
            && target is not TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
        || CollectionViewFaces.IsViewSeam(source, target)
        || index.NeedsNativeProjection?.Invoke(source, target) == true
        // A concrete value or generic stack slot is not a reference, even when its boxed value implements the
        // target interface. State the boxing edge before a conditional merge, store, argument or return.
        || !IsVoid(source) && !IsVoid(target)
            && NeedsBox(source, index.IsValue) && IsReferenceSlot(target, index.IsValue);

    static bool NeedsBox(TypeNode type, ValueTypeOracle isValue) => type switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable nullable => NeedsBox(nullable.Of, isValue),
        TypeNode.Oblivious oblivious => NeedsBox(oblivious.Of, isValue),
        TypeNode.Fqn named => isValue(named),
        _ => false,
    };

    static bool IsReferenceSlot(TypeNode type, ValueTypeOracle isValue) => type switch
    {
        TypeNode.Array => true,
        TypeNode.Nullable nullable => IsReferenceSlot(nullable.Of, isValue),
        TypeNode.Oblivious oblivious => IsReferenceSlot(oblivious.Of, isValue),
        TypeNode.Fqn named => !IsVoid(named) && !isValue(named),
        _ => false,
    };
    sealed record MethodShape(string Owner, string Name, int Arity, TypeNode[] Parameters, TypeNode Return);

    sealed class Index
    {
        readonly Dictionary<string, List<MethodShape>> _methods = new(StringComparer.Ordinal);
        readonly Dictionary<string, TypeNode> _fields = new(StringComparer.Ordinal);
        readonly Dictionary<string, TypeNode[]> _captureConstructors = new(StringComparer.Ordinal);
        readonly Func<JsonObject> _unitValue;
        internal ValueTypeOracle IsValue;
        internal JsonObject Document;
        internal bool ReferenceBuild;
        internal Func<TypeNode, TypeNode, bool> NeedsDeclaredProjection;
        internal Func<TypeNode, TypeNode, bool> NeedsNativeProjection;
        internal Func<JsonNode, TypeNode, TypeNode, JsonNode> AdaptRepresentation;

        Index(Func<JsonObject> unitValue) => _unitValue = unitValue;
        internal JsonObject UnitValue() => _unitValue();

        internal static Index Build(IReadOnlyList<JsonNode> roots, Func<JsonObject> unitValue)
        {
            var result = new Index(unitValue);
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
            if (owner != null && type["ctors"] is JsonArray { Count: 1 } constructors
                && constructors[0]?["params"] is JsonArray parameters)
                _captureConstructors[owner] = parameters.Select(p => TypeJson.Read(p?["type"])).ToArray();
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

        internal TypeNode[] CaptureConstructor(string owner)
            => owner != null && _captureConstructors.TryGetValue(owner, out var parameters) ? parameters : null;

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
        internal readonly Dictionary<string, TypeNode> TemporaryFields;
        internal readonly Dictionary<string, TypeNode> TemporaryLocals;

        internal Scope(TypeNode owner = null, TypeNode ret = null,
            Dictionary<string, TypeNode> locals = null, Dictionary<string, TypeNode> temporaryFields = null,
            Dictionary<string, TypeNode> temporaryLocals = null)
        {
            Owner = owner;
            Return = ret;
            Locals = locals ?? new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            TemporaryFields = temporaryFields ?? new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            TemporaryLocals = temporaryLocals ?? new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        }

        internal Scope Frame(JsonObject declaration, TypeNode owner, TypeNode ret)
        {
            var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            if (declaration["params"] is JsonArray parameters)
                foreach (var parameter in parameters.OfType<JsonObject>())
                    if (Str(parameter["name"]) is string name && TypeJson.Read(parameter["type"]) is TypeNode type)
                        locals[name] = type;
            var temporaryFields = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            var temporaryLocals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            while (CollectTemporaryValues(declaration["preStmts"], temporaryFields, temporaryLocals)
                | CollectTemporaryValues(declaration["body"], temporaryFields, temporaryLocals)) { }
            return new Scope(owner, ret, locals, temporaryFields, temporaryLocals);
        }

        internal Scope Copy() => new(Owner, Return, new Dictionary<string, TypeNode>(Locals, StringComparer.Ordinal), TemporaryFields, TemporaryLocals);
    }

    public static void ApplyAll(IReadOnlyList<JsonNode> roots, Func<JsonObject> unitValue,
        ValueTypeOracle isValue, bool referenceBuild = false,
        Func<TypeNode, TypeNode, bool> needsDeclaredProjection = null,
        Func<TypeNode, TypeNode, bool> needsNativeProjection = null,
        Func<JsonNode, TypeNode, TypeNode, JsonNode> adaptRepresentation = null)
    {
        var index = Index.Build(roots, unitValue);
        index.IsValue = isValue;
        index.ReferenceBuild = referenceBuild;
        index.NeedsDeclaredProjection = needsDeclaredProjection;
        index.NeedsNativeProjection = needsNativeProjection;
        index.AdaptRepresentation = adaptRepresentation;
        foreach (var root in roots.OfType<JsonObject>()) RewriteDocument(root, index);
    }

    static void RewriteDocument(JsonObject root, Index index)
    {
        index.Document = root;
        var fileOwner = Str(root["fileClass"]);
        RewriteMembers(root, fileOwner == null ? null : new TypeNode.Fqn(fileOwner), index);
        if (root["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>().ToArray()) RewriteType(type, index);
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
                    var rewritten = Rewrite(init, fieldScope, index);
                    if (!index.ReferenceBuild) rewritten = Coerce(rewritten, target, fieldScope, index);
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
                    if (!index.ReferenceBuild)
                    {
                        var targets = ConstructorParameterTypes(constructor);
                        CoerceVector(arguments, targets, scope, index);
                        if (constructor["baseCtorRef"] is JsonObject && targets?.Length == arguments.Count)
                            for (var i = 0; i < arguments.Count; i++)
                                if (arguments[i] is JsonNode argument)
                                {
                                    var converted = CoerceDeclaredValue(argument, targets[i], scope, index);
                                    if (!ReferenceEquals(converted, argument)) arguments[i] = converted;
                                }
                    }
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

    static JsonNode Rewrite(JsonNode node, Scope scope, Index index, bool resultUsed = true)
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
            if (child.Key == "catches" && Str(obj["k"]) is "try" or "tryExpr"
                && child.Value is JsonArray catches)
            {
                foreach (var clause in catches.OfType<JsonObject>())
                {
                    var catchScope = scope.Copy();
                    if (Str(clause["var"]) is string caught && TypeJson.Read(clause["excType"]) is TypeNode exceptionType)
                        catchScope.Locals[caught] = exceptionType;
                    Rewrite(clause, catchScope, index);
                }
                continue;
            }
            var childScope = child.Key == "body" && loopScope != null ? loopScope : scope;
            var rewritten = Rewrite(child.Value, childScope, index,
                ChildUsesValue(obj, child.Key, scope, resultUsed));
            if (!ReferenceEquals(rewritten, child.Value)) obj[child.Key] = rewritten;
        }

        // Collection allocation nodes already state their exact reference-type constructor. A compiler-owned
        // single-evaluation binding can retain that type even when its Kotlin surface is existential. Do not narrow
        // user storage or declaration signatures: those can contain other instantiations, including value elements.
        if (!index.ReferenceBuild && obj.Remove(CallEvalLowering.ValueTemporaryKey) && Str(obj["k"]) == "var"
            && TypeJson.Read(obj["type"]) is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" })
        {
            // The child walk may already have wrapped a spilled-field read in its explicit conversion. Use the
            // binding's precomputed provenance rather than trying to rediscover it through rewritten expressions.
            var constructed = Str(obj["name"]) is string temporary
                && scope.TemporaryLocals.TryGetValue(temporary, out var known) ? known
                : ConstructedReferenceType(obj["init"], scope.TemporaryFields, scope.TemporaryLocals);
            if (constructed is TypeNode.Fqn) obj["type"] = TypeJson.Write(constructed);
        }
        CoerceInputs(obj, scope, index);
        // Reference declarations retain Kotlin signature vocabulary. Only consume the
        // executable identity operations left in constructor delegation after body squash.
        if (index.ReferenceBuild) return obj;
        if (Str(obj["k"]) is "return" or "returnExpr" && IsVoid(scope.Return)
            && obj["value"] is JsonNode discarded)
        {
            // A specialized Unit result can remain a real value even though this declaration returns void.
            // Evaluate it once for effects (and exceptions), then return with an empty stack, including in try.
            var exit = (JsonObject)obj.DeepClone();
            exit.Remove("value");
            var effect = new JsonObject { ["k"] = "exprStmt", ["expr"] = discarded.DeepClone() };
            return Str(obj["k"]) == "return"
                ? new JsonObject { ["k"] = "block", ["body"] = new JsonArray(effect, exit) }
                : new JsonObject
                {
                    ["k"] = "valueBlock", ["type"] = TypeJson.Fqn("void"),
                    ["stmts"] = new JsonArray(effect), ["result"] = exit,
                };
        }
        var result = CoerceDeclaredResult(obj, scope, index);
        if (Str(obj["k"]) == "field" && TemporaryFieldKey(obj) is string fieldKey
            && scope.TemporaryFields.TryGetValue(fieldKey, out var retained)
            && ExprType(obj, scope, index) is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" })
            return new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(retained), ["e"] = obj.DeepClone() };
        if (resultUsed && Str(obj["k"]) == "const" && IsVoid(ExprType(result, scope, index)))
            return index.UnitValue();
        if (resultUsed && CanProduceVoidValue(Str(obj["k"])) && IsVoid(ExprType(result, scope, index)))
            return new JsonObject
            {
                ["k"] = "valueBlock",
                ["type"] = TypeJson.Write(new TypeNode.Fqn("kotlin.Unit")),
                ["stmts"] = new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = result.DeepClone() }),
                ["result"] = index.UnitValue(),
            };
        return result;
    }

    static TypeNode ConstructedReferenceType(JsonNode value, IReadOnlyDictionary<string, TypeNode> fields,
        IReadOnlyDictionary<string, TypeNode> locals)
    {
        if (value is not JsonObject expression) return null;
        if (Str(expression["k"]) == "valueBlock") return ConstructedReferenceType(expression["result"], fields, locals);
        if (Str(expression["k"]) == "local" && Str(expression["name"]) is string name
            && locals.TryGetValue(name, out var local)) return local;
        if (Str(expression["k"]) == "field" && TemporaryFieldKey(expression) is string key
            && fields.TryGetValue(key, out var retained)) return retained;
        return Str(expression["k"]) is "newList" or "newSet" or "newMap"
            && expression["ctorRef"] is JsonObject constructor
            ? TypeJson.Read(constructor["declaringType"]) : null;
    }

    // Cold lowering may spill the same single-evaluation binding. Keep the field's layout unchanged and state an
    // exact read conversion from its known allocation. The fact is method-frame-local, not inferred from a name.
    static string TemporaryFieldKey(JsonObject node) =>
        node["recv"] is JsonObject receiver && Str(receiver["k"]) == "this"
        && TypeJson.Read(node["ownerType"]) is TypeNode owner && Str(node["name"]) is string name
            ? owner.ToString() + "\u0000" + name : null;

    static bool CollectTemporaryValues(JsonNode node, Dictionary<string, TypeNode> fields,
        Dictionary<string, TypeNode> locals)
    {
        var changed = false;
        if (node is JsonArray array)
        {
            foreach (var child in array) changed |= CollectTemporaryValues(child, fields, locals);
            return changed;
        }
        if (node is not JsonObject obj || obj["params"] is JsonArray && obj["body"] is JsonArray) return false;
        if (Str(obj["k"]) == "setField" && obj[CallEvalLowering.ValueTemporaryKey]?.GetValue<bool>() == true
            && TemporaryFieldKey(obj) is string key && !fields.ContainsKey(key)
            && ConstructedReferenceType(obj["value"], fields, locals) is TypeNode type)
        {
            fields[key] = type;
            changed = true;
        }
        if (Str(obj["k"]) == "var" && obj[CallEvalLowering.ValueTemporaryKey]?.GetValue<bool>() == true
            && Str(obj["name"]) is string name && !locals.ContainsKey(name)
            && ConstructedReferenceType(obj["init"], fields, locals) is TypeNode localType)
        {
            locals[name] = localType;
            changed = true;
        }
        foreach (var child in obj) changed |= CollectTemporaryValues(child.Value, fields, locals);
        return changed;
    }

    static bool IsVoid(TypeNode type) => type is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };

    static bool CanProduceVoidValue(string kind) => kind is
        "callStatic" or "callInstance" or "constrainedCall" or "clrStatic" or "clrInstance"
        or "clrGenericStatic" or "clrGenericInstance" or "delegateInvoke" or "cond" or "valueBlock";

    static bool ChildUsesValue(JsonObject parent, string key, Scope scope, bool resultUsed) =>
        (Str(parent["k"]), key) switch
        {
            ("exprStmt", "expr") => false,
            ("return" or "returnExpr", "value") => !IsVoid(scope.Return),
            ("valueBlock", "result") or ("cond", "then" or "else") =>
                resultUsed && !IsVoid(TypeJson.Read(parent["type"])),
            _ => true,
        };

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
        if (index.ReferenceBuild && !(kind == "binOp" && Str(node["op"]) == "===")) return;
        switch (kind)
        {
            case "binOp" when Str(node["op"]) == "===":
                var identityLeft = ExprType(node["lhs"], scope, index);
                var identityRight = ExprType(node["rhs"], scope, index);
                if (identityLeft == null || identityRight == null)
                    throw new InvalidOperationException("bir2cir: identity comparison lacks a physical operand type");
                // Only equal physical slots retain the documented homogeneous comparison.
                // Distinct slots compare references: box generic/value operands explicitly,
                // without calling Equals or narrowing one operand to the other's type.
                if (!identityLeft.Equals(identityRight))
                    foreach (var operand in new[] { "lhs", "rhs" })
                        node[operand] = new JsonObject
                        {
                            ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Object"),
                            ["e"] = node[operand]!.DeepClone(),
                        };
                node["op"] = "==";
                break;
            case "var":
                CoerceSlot(node, "init", TypeJson.Read(node["type"]), scope, index);
                break;
            case "setLocal":
                if (Str(node["name"]) is string local && scope.Locals.TryGetValue(local, out var localType))
                    CoerceSlot(node, "value", localType, scope, index);
                break;
            case "setField": case "setFieldExpr": case "staticFieldSet":
                CoerceMemberSlot(node, "value", FieldTarget(node, scope, index), scope, index);
                break;
            case "return": case "returnExpr":
                CoerceSlot(node, "value", scope.Return, scope, index);
                break;
            case "callStatic": case "callInstance": case "constrainedCall":
            case "clrStatic": case "clrInstance": case "clrGenericStatic": case "clrGenericInstance":
                CoerceArguments(node, ParameterTypes(node), scope, index);
                break;
            case "new": case "newClr":
                CoerceConstructorArguments(node, scope, index);
                break;
            case "newClosure": case "newSam":
                var captureOwner = TypeJson.Read(node[kind == "newClosure" ? "closureType" : "samType"]) as TypeNode.Fqn;
                var captureParameters = index.CaptureConstructor(captureOwner?.Name);
                if (node["captures"] is not JsonArray captures || captureParameters == null
                    || captureParameters.Any(p => p == null) || captures.Count != captureParameters.Length)
                    throw new InvalidOperationException("bir2cir: captured construction has no exact constructor contract");
                var captureArguments = ReadTypes(node["typeArgs"] as JsonArray) ?? Array.Empty<TypeNode>();
                CoerceVector(captures, captureParameters.Select(p => Close(p, captureArguments, Array.Empty<TypeNode>())).ToArray(), scope, index);
                break;
            case "delegateInvoke":
                CoerceDelegateArguments(node, scope, index);
                break;
            case "clrPropSet":
                CoerceMemberSlot(node, "value", FieldTarget(node, scope, index), scope, index);
                break;
            case "arraySet":
                CoerceSlot(node, "value", TypeJson.Read(node["elem"]), scope, index);
                goto case "arrayGet";
            case "arrayGet": case "forArray":
                if (TypeJson.Read(node["elem"]) is TypeNode arrayElement)
                    CoerceSlot(node, "array", new TypeNode.Array(arrayElement), scope, index);
                break;
            case "byrefLoad": case "byrefStore":
                // A managed reference's physical referent is owned by its pointer
                // declaration, not by the erased Kotlin value flowing through it.
                var pointerType = node["ptr"] is JsonNode pointer
                    ? ExprType(pointer, scope, index)
                    : Str(node["local"]) is string pointerLocal
                        && scope.Locals.TryGetValue(pointerLocal, out var namedPointer) ? namedPointer : null;
                if (pointerType is TypeNode.ByRef reference)
                    node["elem"] = TypeJson.Write(reference.Of);
                if (kind == "byrefStore")
                {
                    CoerceSlot(node, "value", TypeJson.Read(node["elem"]), scope, index);
                    // The pointee is an exact storage declaration even without a
                    // memberRef on this store. Recover its closed CLR type from a
                    // Kotlin carrier on the value edge, never by changing the pointer.
                    if (node["value"] is JsonNode stored)
                    {
                        var converted = CoerceDeclaredValue(stored, TypeJson.Read(node["elem"]), scope, index);
                        converted = CoerceReferenceStore(converted, TypeJson.Read(node["elem"]), scope, index);
                        if (!ReferenceEquals(converted, stored)) node["value"] = converted;
                    }
                }
                break;
            case "stackSet":
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
        // The receiver is an input slot too. An array element or generic result may expose the storage
        // collection face while the selected member belongs to its read-only face. Use the exact resolved
        // owner, just as argument coercion uses the exact selected parameter vector.
        if (node["recv"] != null && ResolvedMember(node) is JsonObject selected)
        {
            // Address-taking dispatch consumes the recorded receiver construction, not a boxed instance of the
            // declaring interface. Preserve that physical slot for constrained calls and CLR struct dispatch.
            var receiverTarget = Str(node["k"]) switch
            {
                "constrainedCall" => TypeJson.Read(node["recvType"]),
                "clrInstance" or "clrPropGet" or "clrPropSet" => TypeJson.Read(node["type"]),
                _ => TypeJson.Read(selected["declaringType"]),
            };
            CoerceSlot(node, "recv", receiverTarget, scope, index);
        }
    }

    static void CoerceDelegateArguments(JsonObject node, Scope scope, Index index)
    {
        // Invoke's resolved declaration owns the constructed function delegate's physical slots.
        // Unlike a Kotlin function type, that declaration states exactly what the emitted call consumes.
        if (node["invokeRef"] is not JsonObject invoke || node["args"] is not JsonArray args)
            throw new InvalidOperationException("bir2cir: delegate invocation has no resolved argument contract");
        // A function-bounded generic value still occupies a generic stack slot. Its selected
        // Invoke consumes a delegate reference, so materialize that boxing edge in CIR too.
        CoerceSlot(node, "recv", TypeJson.Read(invoke["declaringType"]), scope, index);
        var targets = ReadTypes(invoke["parameterTypes"] as JsonArray)
            ?? throw new InvalidOperationException("bir2cir: delegate invocation has no parameter types");
        if (targets.Length != args.Count)
            throw new InvalidOperationException("bir2cir: delegate invocation argument count differs from its resolved contract");
        targets = targets.Select(t => Close(t, OwnerArgs(invoke), Array.Empty<TypeNode>())).ToArray();
        CoerceVector(args, targets, scope, index);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is not JsonNode value || targets[i] is TypeNode.ByRef
                || targets[i].Equals(ExprType(value, scope, index))) continue;
            // State the argument conversion explicitly: a value-type argument to an object/interface slot
            // requires boxing, even though the Kotlin source needs no explicit cast.
            args[i] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(targets[i]), ["e"] = value.DeepClone() };
        }
    }

    static void CoerceArguments(JsonObject node, TypeNode[] targets, Scope scope, Index index)
    {
        if (node["args"] is not JsonArray args) return;
        CoerceVector(args, targets, scope, index);
        // A selected CLR declaration owns its exact parameter slots. Kotlin value slots use
        // classifier carriers; do not apply this narrowing to ordinary inferred/local signatures.
        if (node["memberRef"] is not JsonObject || targets?.Length != args.Count) return;
        for (var i = 0; i < args.Count; i++)
            if (args[i] is JsonNode argument)
            {
                var converted = CoerceDeclaredValue(argument, targets[i], scope, index);
                if (!ReferenceEquals(converted, argument)) args[i] = converted;
            }
    }

    static JsonNode CoerceDeclaredValue(JsonNode value, TypeNode target, Scope scope, Index index)
        => index.NeedsDeclaredProjection?.Invoke(ExprType(value, scope, index), target) == true
            ? new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(target), ["e"] = value.DeepClone() }
            : value;

    // Managed-reference stores consume the actual referent, including array
    // constructions and native alias targets. A source declaration modifier is
    // not a stack type: use its physical value face when comparing the edge.
    // This is a checked value conversion, never a pointer conversion or copy.
    static JsonNode CoerceReferenceStore(JsonNode value, TypeNode target, Scope scope, Index index)
    {
        var sourceSlot = ReferenceValueSlot(ExprType(value, scope, index));
        var targetSlot = ReferenceValueSlot(target);
        if (sourceSlot == targetSlot || !IsReferenceSlot(sourceSlot, index.IsValue)
            || !IsReferenceSlot(targetSlot, index.IsValue)) return value;
        return new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(target), ["e"] = value.DeepClone() };
    }

    static TypeNode ReferenceValueSlot(TypeNode type) => type switch
    {
        TypeNode.Mod modifier => ReferenceValueSlot(modifier.Of),
        TypeNode.Nullable nullable => ReferenceValueSlot(nullable.Of),
        TypeNode.Oblivious oblivious => ReferenceValueSlot(oblivious.Of),
        _ => type,
    };

    static void CoerceMemberSlot(JsonObject node, string key, TypeNode target, Scope scope, Index index)
    {
        CoerceSlot(node, key, target, scope, index);
        if (ResolvedMember(node) == null || node[key] is not JsonNode value) return;
        var converted = CoerceDeclaredValue(value, target, scope, index);
        if (!ReferenceEquals(converted, value)) node[key] = converted;
    }

    static void CoerceConstructorArguments(JsonObject node, Scope scope, Index index)
    {
        // A local new's argTypes already describe the caller's closed construction. Only a resolved constructor
        // memberRef still carries declaration-relative parameters that need substitution through its owner.
        var targets = node["memberRef"] is JsonObject
            ? ParameterTypes(node)
            : ReadTypes(node["argTypes"] as JsonArray);
        CoerceArguments(node, targets, scope, index);
        // An erased value may cross into a closed constructor parameter, but that target belongs to the
        // selected physical declaration, not to an independently closed source projection. In particular,
        // Holder<MutableList<*>> constructs Holder<object>: its argument must not be cast to IList<object>.
        if (node["memberRef"] is not JsonObject member || Str(member["kind"]) != "ctor"
            || node["args"] is not JsonArray args || targets?.Length != args.Count) return;
        for (var i = 0; i < args.Count; i++)
            if (args[i] is JsonNode argument
                && ExprType(argument, scope, index) is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
                && targets[i] is TypeNode.Fqn or TypeNode.Array or TypeNode.Fn or TypeNode.Tv
                && targets[i] is not TypeNode.Fqn { Args: null, Name: "object" or "System.Object" })
                args[i] = new JsonObject
                {
                    ["k"] = "cast", ["type"] = TypeJson.Write(targets[i]), ["e"] = argument.DeepClone(),
                };
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
            var thenSeam = NeedsConversion(ExprType(conditional["then"], scope, index), target, index);
            var elseSeam = NeedsConversion(ExprType(conditional["else"], scope, index), target, index);
            if (thenSeam || elseSeam)
            {
                CoerceSlot(conditional, "then", target, scope, index);
                CoerceSlot(conditional, "else", target, scope, index);
                conditional["type"] = TypeJson.Write(target);
                return conditional;
            }
        }
        var got = ExprType(value, scope, index);
        if (index.AdaptRepresentation?.Invoke(value, got, target) is JsonNode representation)
            return representation;
        if (value is JsonObject expression
            && ClrMemberResolution.AdaptUnitDelegateValue(index.Document, expression, got, target) is JsonObject adapter)
            return adapter;
        if (!NeedsConversion(got, target, index)) return value;
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
        // A closed generic return can be a real value even when the Kotlin Unit return stamp folded to void.
        // Keep the exact closed physical result on the call instead of asking ilemit to recover its generic frame.
        if (IsVoid(declared) && actual != null && !IsVoid(actual))
        {
            foreach (var key in new[] { "ret", "dynRet" })
                if (expression[key] != null) expression[key] = TypeJson.Write(actual);
            return expression;
        }
        // An object-returning Kotlin call can already carry a caller-facing generic projection in ret.
        // Make that projection explicit before its consumer inspects the physical expression result;
        // otherwise identity comparison would box its other operand while the call still unboxes to T.
        var genericObjectProjection = declared is TypeNode.Tv
            && actual is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
            && Str(expression["k"]) is "callStatic" or "callInstance" or "constrainedCall";
        // An erased Kotlin result stamp cannot describe the stack result of an
        // exact CLR member. Keep the native value/generic result on the inner
        // call so the explicit conversion really emits boxing rather than an
        // identity object-to-object cast.
        var boxedResult = NeedsBox(actual, index.IsValue) && IsReferenceSlot(declared, index.IsValue);
        if (!genericObjectProjection && !boxedResult && !CollectionViewFaces.IsViewSeam(actual, declared)
            && index.NeedsNativeProjection?.Invoke(actual, declared) != true)
        {
            // A resolved native expression states the stack result, not the
            // member's open declaration frame. Even when no view conversion
            // is necessary, close that result over the selected owner/method
            // arguments. Consuming edges already own their caller-side target.
            if (actual != null && Str(expression["k"]) is "clrStatic" or "clrInstance"
                or "clrGenericStatic" or "clrGenericInstance" or "clrPropGet" or "clrStaticField")
                foreach (var key in new[] { "sty", "ret", "dynRet" })
                    if (expression[key] != null) expression[key] = TypeJson.Write(actual);
            return expression;
        }
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

    internal static void SelfTest()
    {
        foreach (var declared in new TypeNode[] {
            new TypeNode.Tv("type", 0),
            new TypeNode.ByRef(new TypeNode.Tv("type", 0)),
            new TypeNode.Nullable(new TypeNode.Tv("type", 0)),
            new TypeNode.Mod(false, new TypeNode.Fqn("System.Runtime.CompilerServices.IsVolatile"),
                new TypeNode.Tv("type", 0)),
            new TypeNode.Array(new TypeNode.Tv("type", 0)),
            new TypeNode.Fqn("NativeBox", new TypeNode[] {
                new TypeNode.Tv("type", 0), new TypeNode.Tv("method", 0) }),
        })
        {
            var ownerArguments = new TypeNode[] { new TypeNode.Tv("type", 1), new TypeNode.Tv("type", 0) };
            var methodArguments = new TypeNode[] { new TypeNode.Tv("method", 1) };
            var expected = ValueSlotType(Close(declared, ownerArguments, methodArguments));
            if (expected is TypeNode.ByRef reference) expected = ValueSlotType(reference.Of);
            var call = new JsonObject {
                ["k"] = "clrInstance", ["method"] = "Read", ["recv"] = new JsonObject { ["k"] = "this" },
                ["type"] = TypeJson.Write(new TypeNode.Fqn("NativeOwner", ownerArguments)),
                ["typeArgs"] = new JsonArray(methodArguments.Select(TypeJson.Write).ToArray()),
                ["args"] = new JsonArray(), ["ret"] = TypeJson.Write(declared),
                ["sty"] = TypeJson.Write(declared), ["dynRet"] = TypeJson.Write(declared),
                ["memberRef"] = new JsonObject {
                    ["kind"] = "method", ["name"] = "Read",
                    ["declaringType"] = TypeJson.Write(new TypeNode.Fqn("NativeOwner", ownerArguments)),
                    ["returnType"] = TypeJson.Write(declared), ["parameterTypes"] = new JsonArray(),
                },
            };
            var root = new JsonObject { ["fileClass"] = "Consumer", ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "Use", ["params"] = new JsonArray(), ["ret"] = TypeJson.Fqn("void"),
                    ["body"] = new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = call }) }) };
            for (var iteration = 0; iteration < 2; iteration++)
            {
                ApplyAll(new JsonNode[] { root }, () => throw new InvalidOperationException("Unexpected Unit"), _ => false);
                if (TypeJson.Read(call["ret"]) != expected || TypeJson.Read(call["sty"]) != expected
                    || TypeJson.Read(call["dynRet"]) != expected
                    || TypeJson.Read(call["memberRef"]["returnType"]) != declared)
                    throw new InvalidOperationException("Native result lost its constructed caller frame or rewrote its open declaration");
            }
        }
        foreach (var kind in new[] { "newClosure", "newSam" })
        {
            var capture = new JsonObject { ["k"] = kind,
                [kind == "newClosure" ? "closureType" : "samType"] = TypeJson.Fqn("Capture"),
                ["typeArgs"] = new JsonArray(TypeJson.Fqn("System.Object")),
                ["captures"] = new JsonArray(new JsonObject { ["k"] = "const",
                    ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = 7 }) };
            var document = JsonNode.Parse("""
                {"fileClass":"CaptureConsumer","methods":[{"name":"Use","params":[],
                  "ret":{"t":"fqn","name":"void"},"body":[]}],
                 "types":[{"name":"Capture","typeParams":["T"],"ctors":[{"params":[
                  {"name":"value","type":{"t":"tv","scope":"type","i":0}}],"body":[]}]}]}
                """)!.AsObject();
            document["methods"][0]["body"].AsArray().Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = capture });
            for (var iteration = 0; iteration < 2; iteration++)
            {
                ApplyAll(new JsonNode[] { document }, () => throw new InvalidOperationException("Unexpected Unit"),
                    t => t.Name == "System.Int32");
                if (Str(capture["captures"][0]["k"]) != "cast"
                    || TypeJson.Read(capture["captures"][0]["type"]) != new TypeNode.Fqn("System.Object")
                    || Str(capture["captures"][0]["e"]["k"]) != "const")
                    throw new InvalidOperationException("Captured value did not enter its constructed physical slot exactly once");
            }
        }
        var referent = new TypeNode.Tv("method", 0);
        var referenceRead = new JsonObject { ["k"] = "var", ["name"] = "saved", ["type"] = TypeJson.Fqn("object"),
            ["init"] = new JsonObject { ["k"] = "byrefLoad", ["elem"] = TypeJson.Fqn("object"),
                ["ptr"] = new JsonObject { ["k"] = "local", ["name"] = "pointer" } } };
        var referenceWrite = new JsonObject { ["k"] = "byrefStore", ["elem"] = TypeJson.Fqn("object"),
            ["ptr"] = new JsonObject { ["k"] = "local", ["name"] = "pointer" },
            ["value"] = new JsonObject { ["k"] = "local", ["name"] = "saved" } };
        var namedReferenceRead = new JsonObject { ["k"] = "var", ["name"] = "namedSaved", ["type"] = TypeJson.Fqn("object"),
            ["init"] = new JsonObject { ["k"] = "byrefLoad", ["elem"] = TypeJson.Fqn("object"), ["local"] = "pointer" } };
        var namedReferenceWrite = new JsonObject { ["k"] = "byrefStore", ["elem"] = TypeJson.Fqn("object"),
            ["local"] = "pointer", ["value"] = new JsonObject { ["k"] = "local", ["name"] = "namedSaved" } };
        var referenceMethod = new JsonObject { ["name"] = "Use", ["typeParams"] = new JsonArray("T"),
            ["params"] = new JsonArray(new JsonObject { ["name"] = "pointer",
                ["type"] = TypeJson.Write(new TypeNode.ByRef(referent)) }), ["ret"] = TypeJson.Fqn("void"),
            ["body"] = new JsonArray(referenceRead, referenceWrite, namedReferenceRead, namedReferenceWrite) };
        var referenceDocument = new JsonObject { ["fileClass"] = "Consumer", ["methods"] = new JsonArray(referenceMethod) };
        for (var iteration = 0; iteration < 2; iteration++)
        {
            ApplyAll(new JsonNode[] { referenceDocument }, () => throw new InvalidOperationException("Unexpected Unit"), _ => false);
            if (Str(referenceRead["init"]["k"]) != "cast"
                || TypeJson.Read(referenceRead["init"]["e"]["elem"]) != referent
                || TypeJson.Read(referenceWrite["elem"]) != referent
                || Str(referenceWrite["value"]["k"]) != "cast"
                || TypeJson.Read(referenceWrite["value"]["type"]) != referent
                || Str(referenceWrite["value"]["e"]["k"]) != "local"
                || Str(namedReferenceRead["init"]["k"]) != "cast"
                || TypeJson.Read(namedReferenceRead["init"]["e"]["elem"]) != referent
                || TypeJson.Read(namedReferenceWrite["elem"]) != referent
                || Str(namedReferenceWrite["value"]["k"]) != "cast"
                || TypeJson.Read(namedReferenceWrite["value"]["type"]) != referent)
                throw new InvalidOperationException("Managed-reference value flow lost its physical referent or conversion");
        }
        var closedReferent = new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Fqn("System.String") });
        var carrierReferent = new TypeNode.Fqn("Box$star");
        var carrierStore = new JsonObject { ["k"] = "byrefStore", ["elem"] = TypeJson.Write(carrierReferent),
            ["ptr"] = new JsonObject { ["k"] = "local", ["name"] = "pointer" },
            ["value"] = new JsonObject { ["k"] = "local", ["name"] = "value" } };
        var carrierDocument = new JsonObject { ["fileClass"] = "Consumer", ["methods"] = new JsonArray(
            new JsonObject { ["name"] = "Write", ["ret"] = TypeJson.Fqn("void"),
                ["params"] = new JsonArray(
                    new JsonObject { ["name"] = "pointer", ["type"] = TypeJson.Write(new TypeNode.ByRef(closedReferent)) },
                    new JsonObject { ["name"] = "value", ["type"] = TypeJson.Write(carrierReferent) }),
                ["body"] = new JsonArray(carrierStore) }) };
        for (var iteration = 0; iteration < 2; iteration++)
        {
            ApplyAll(new JsonNode[] { carrierDocument }, () => throw new InvalidOperationException("Unexpected Unit"),
                _ => false, needsDeclaredProjection: (source, target) =>
                    source == carrierReferent && target == closedReferent);
            if (TypeJson.Read(carrierStore["elem"]) != closedReferent
                || Str(carrierStore["value"]["k"]) != "cast"
                || TypeJson.Read(carrierStore["value"]["type"]) != closedReferent
                || Str(carrierStore["value"]["e"]["k"]) != "local"
                || Str(carrierStore["ptr"]["k"]) != "local")
                throw new InvalidOperationException("Managed-reference carrier store lost exact pointee, checked conversion or pointer identity");
        }
        foreach (var (source, target) in new (TypeNode, TypeNode)[] {
            (new TypeNode.Mod(false, new TypeNode.Fqn("Alias"), new TypeNode.Fqn("System.Object")),
                new TypeNode.Fqn("System.Collections.Generic.IReadOnlyList", new TypeNode[] { new TypeNode.Fqn("System.String") })),
            (new TypeNode.Array(carrierReferent), new TypeNode.Array(closedReferent)) })
        {
            var store = new JsonObject { ["k"] = "byrefStore", ["local"] = "pointer",
                ["elem"] = TypeJson.Write(source), ["value"] = new JsonObject { ["k"] = "local", ["name"] = "value" } };
            var document = new JsonObject { ["fileClass"] = "Consumer", ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "Write", ["ret"] = TypeJson.Fqn("void"),
                    ["params"] = new JsonArray(
                        new JsonObject { ["name"] = "pointer", ["type"] = TypeJson.Write(new TypeNode.ByRef(target)) },
                        new JsonObject { ["name"] = "value", ["type"] = TypeJson.Write(source) }),
                    ["body"] = new JsonArray(store) }) };
            for (var iteration = 0; iteration < 2; iteration++)
            {
                ApplyAll(new JsonNode[] { document }, () => throw new InvalidOperationException("Unexpected Unit"), _ => false);
                if (TypeJson.Read(store["elem"]) != target || Str(store["value"]["k"]) != "cast"
                    || TypeJson.Read(store["value"]["type"]) != target || Str(store["value"]["e"]["k"]) != "local"
                    || Str(store["local"]) != "pointer" || store["ptr"] != null)
                    throw new InvalidOperationException("Managed-reference alias/array store lacks a single checked value conversion");
            }
        }
        var delegateType = new TypeNode.Fqn("System.Func", new TypeNode[] {
            new TypeNode.Fqn("System.Object"), new TypeNode.Fqn("System.Object") });
        var erasedReference = new JsonObject { ["k"] = "var", ["name"] = "callback",
            ["type"] = TypeJson.Write(delegateType),
            ["init"] = new JsonObject { ["k"] = "local", ["name"] = "erased" } };
        var referenceRoot = new JsonObject { ["fileClass"] = "Consumer", ["methods"] = new JsonArray(
            new JsonObject { ["name"] = "Use", ["params"] = new JsonArray(new JsonObject {
                ["name"] = "erased", ["type"] = TypeJson.Fqn("System.Object") }),
                ["ret"] = TypeJson.Fqn("void"), ["body"] = new JsonArray(erasedReference) }) };
        for (var iteration = 0; iteration < 2; iteration++)
        {
            ApplyAll(new JsonNode[] { referenceRoot }, () => throw new InvalidOperationException("Unexpected Unit"), _ => false);
            if (Str(erasedReference["init"]["k"]) != "cast"
                || TypeJson.Read(erasedReference["init"]["type"]) != delegateType
                || Str(erasedReference["init"]["e"]["k"]) != "local")
                throw new InvalidOperationException("Erased reference did not enter its exact physical slot once");
        }
        foreach (var argument in new TypeNode[] { new TypeNode.Tv("method", 0), new TypeNode.Fqn("System.Int32") })
        {
            var call = new JsonObject {
                ["k"] = "clrStatic", ["type"] = TypeJson.Fqn("Native"), ["method"] = "Read",
                ["typeArgs"] = new JsonArray(TypeJson.Write(argument)), ["args"] = new JsonArray(),
                ["ret"] = TypeJson.Fqn("object"), ["sty"] = TypeJson.Fqn("object"),
                ["memberRef"] = new JsonObject {
                    ["kind"] = "method", ["declaringType"] = TypeJson.Fqn("Native"),
                    ["name"] = "Read", ["genericArity"] = 1, ["parameterTypes"] = new JsonArray(),
                    ["returnType"] = TypeJson.Write(new TypeNode.Tv("method", 0)),
                },
            };
            var root = new JsonObject { ["fileClass"] = "Consumer", ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "Use", ["params"] = new JsonArray(),
                    ["typeParams"] = new JsonArray("T"), ["ret"] = TypeJson.Fqn("object"),
                    ["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = call }) }) };
            for (var iteration = 0; iteration < 2; iteration++)
            {
                ApplyAll(new JsonNode[] { root }, () => throw new InvalidOperationException("Unexpected Unit"),
                    type => type.Name == "System.Int32");
                var value = root["methods"][0]["body"][0]["value"];
                if (Str(value["k"]) != "cast" || Str(value["e"]["k"]) != "clrStatic"
                    || TypeJson.Read(value["e"]["ret"]) != argument
                    || TypeJson.Read(value["e"]["sty"]) != argument
                    || TypeJson.Read(value["e"]["memberRef"]["returnType"]) != new TypeNode.Tv("method", 0))
                    throw new InvalidOperationException("Physical CLR result boxing lost the exact native result or was not idempotent");
            }
        }
        foreach (var kind in new[] { "newArray", "newList", "newSet" })
        {
            var boxed = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("object"),
                ["e"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = 42 } };
            var construction = new JsonObject { ["k"] = kind, ["elem"] = TypeJson.Fqn("System.Int32"),
                ["elems"] = new JsonArray(boxed) };
            var root = new JsonObject { ["fileClass"] = "Consumer", ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "Use", ["params"] = new JsonArray(), ["ret"] = TypeJson.Fqn("void"),
                    ["body"] = new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = construction }) }) };
            for (var iteration = 0; iteration < 2; iteration++)
            {
                ApplyAll(new JsonNode[] { root }, () => throw new InvalidOperationException("Unexpected Unit"),
                    type => type.Name == "System.Int32");
                var element = construction["elems"][0];
                if (Str(element["k"]) != "cast" || TypeJson.Read(element["type"]) != new TypeNode.Fqn("System.Int32")
                    || Str(element["e"]["k"]) != "cast" || Str(element["e"]["e"]["k"]) != "const")
                    throw new InvalidOperationException("Erased element did not enter its physical value slot exactly once");
            }
        }
        Console.WriteLine("[physical value coercion] self-test OK (native generic/value result boxing)");
    }

    static TypeNode ExprType(JsonNode node, Scope scope, Index index)
    {
        if (node is not JsonObject obj) return null;
        var kind = Str(obj["k"]);
        if (kind == "local" && Str(obj["name"]) is string name && scope.Locals.TryGetValue(name, out var local))
            return local;
        if (kind == "this") return scope.Owner;
        if (PhysicalResult(obj, scope, index) is TypeNode physical) return physical;
        var inferred = NodeType.Of(obj, child => ExprType(child, scope, index),
            name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var elem) ? elem : null);
        return index.ReferenceBuild ? inferred : BirTypeLowering.CanonicalExpressionResult(inferred);
    }

    static TypeNode PhysicalResult(JsonObject expression, Scope scope, Index index)
    {
        var kind = Str(expression["k"]);
        if (kind == "cast") return TypeJson.Read(expression["type"]);
        if (ResolvedMember(expression) is JsonObject member
            && kind is "callStatic" or "callInstance" or "constrainedCall"
                or "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance"
                or "clrPropGet" or "field" or "staticField" or "clrStaticField" or "lateinitGet")
        {
            var result = ValueSlotType(Close(TypeJson.Read(member["returnType"]), OwnerArgs(member), MethodArgs(expression)));
            // An ordinary native ref-return invocation consumes a value copy.
            // Address-taking is represented separately by byrefOf, which keeps
            // the selected member's unchanged pointer signature.
            return kind is "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance" or "clrPropGet"
                && result is TypeNode.ByRef reference ? ValueSlotType(reference.Of) : result;
        }
        if (kind is "callStatic" or "callInstance" or "constrainedCall"
            && TypeJson.Read(expression["calleeRet"]) is TypeNode selectedReturn)
            return Close(selectedReturn, CallOwner(expression)?.Args, MethodArgs(expression));
        if (kind is "callStatic" or "callInstance" or "constrainedCall" && index.Method(expression) is MethodShape method)
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
            return parameters?.Select(p => ValueSlotType(Close(p, OwnerArgs(member), Array.Empty<TypeNode>()))).ToArray();
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
        return parameters.Select(p => ValueSlotType(Close(p, ownerArgs ?? CallOwner(node)?.Args, methodArgs))).ToArray();
    }

    // Custom modifiers distinguish declarations, but do not change the value on
    // the evaluation stack. Keep the memberRef untouched and coerce its value slot.
    static TypeNode ValueSlotType(TypeNode type) => type is TypeNode.Mod modifier
        ? ValueSlotType(modifier.Of) : type;

    static TypeNode.Fqn CallOwner(JsonObject call)
        => TypeJson.Read(call["ownerType"]) as TypeNode.Fqn
            ?? TypeJson.Read(call["iface"]) as TypeNode.Fqn
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
