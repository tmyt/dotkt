using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Shared physical TypeNode policy and the early DOM owners' result/storage helpers.
// Final value-flow rewriting is owned by TypedPhysicalValueCoercion.
static partial class PhysicalValueCoercion
{
    internal static bool NeedsConversion(TypeNode source, TypeNode target, ValueTypeOracle isValue,
        Func<TypeNode.Fqn, bool> isExistentialOwner, Func<TypeNode.Fqn, bool> isNativeReference,
        Func<TypeNode.Fqn, TypeNode.Fqn, bool> hasDeclaredCarrier)
    {
        source = SignatureValueTypes.Of(source);
        target = SignatureValueTypes.Of(target);
        ValidateNativePointerConversion(source, target);
        return source is TypeNode.Tv && target is TypeNode.Tv && !source.Equals(target)
        || target is TypeNode.Tv && source is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
        || source is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
            && !IsVoid(target) && NeedsBox(target, isValue)
        || source is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
            && IsReferenceSlot(target, isValue)
            && target is not TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
        || source is TypeNode.Fqn carrier && target is TypeNode.Fqn native
            && isExistentialOwner(carrier)
            && (isNativeReference(native) || hasDeclaredCarrier(native, carrier))
        || CollectionViewFaces.IsViewSeam(source, target)
        || source is TypeNode.Fqn { Name: "System.Array" } && target is TypeNode.Array
        // A concrete value or generic stack slot is not a reference, even when its boxed value implements the
        // target interface. State the boxing edge before a conditional merge, store, argument or return.
        || !IsVoid(source) && !IsVoid(target)
            && NeedsBox(source, isValue) && IsReferenceSlot(target, isValue);
    }

    internal static void ValidateNativePointerConversion(TypeNode source, TypeNode target)
    {
        if (source == null || target == null || IsVoid(target)) return;
        if ((source is TypeNode.FnPtr || target is TypeNode.FnPtr)
            && BirTypeLowering.CanonicalPhysicalSlotType(source) != BirTypeLowering.CanonicalPhysicalSlotType(target))
            throw new InvalidOperationException("bir2cir: a native function-pointer value requires its exact signature; boxing and checked casts are not supported"
                + $" (source={TypeNode.ToJson(source)}, target={TypeNode.ToJson(target)})");
    }

    internal static bool NeedsBox(TypeNode type, ValueTypeOracle isValue) => type switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable nullable => NeedsBox(nullable.Of, isValue),
        TypeNode.Oblivious oblivious => NeedsBox(oblivious.Of, isValue),
        TypeNode.Fqn named => isValue(named),
        _ => false,
    };

    internal static bool IsReferenceSlot(TypeNode type, ValueTypeOracle isValue) => type switch
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
                    && (c.Parameters.SequenceEqual(wanted)
                        || c.Parameters.Select((p, i) => Close(p, owner.Args, methodArgs) == wanted[i]).All(x => x))))).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        static string Key(string owner, string name) => owner + "\u0000" + name;

    }

    internal static Func<JsonNode, BirScope, TypeNode> ResultTypeOracle(IReadOnlyList<JsonNode> roots)
    {
        var index = Index.Build(roots);
        var fields = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        return (node, scope) =>
        {
            if (node is JsonObject construction && Str(construction["k"]) == "newSam"
                && TypeJson.Read(construction["samType"]) is TypeNode.Fqn implementation)
                return construction["typeArgs"] is JsonArray args && args.Count > 0
                    ? new TypeNode.Fqn(implementation.Name, args.Select(TypeJson.Read).ToArray()) : implementation;
            return ConstructedReferenceType(node, fields, scope.VarTypes)
                ?? ExprType(node, scope.VarTypes, index);
        };
    }

    // The early scalar-dispatch boundary can allocate storage only for an
    // actual incoming parameter and a bare generic native referent. Full ABI
    // allocation retains its existing eligibility for all addressed locals.
    internal static bool NativeLocalEligible(bool genericParametersOnly, bool parameter, TypeNode referent)
        => !genericParametersOnly || parameter && SignatureValueTypes.Of(referent) is TypeNode.Tv;

    internal static void AllocateNativeLocals(IEnumerable<JsonNode> body,
        IEnumerable<JsonObject> parameterDeclarations, JsonArray prologue,
        GenericArrayStorageLowering.FreshNames arrayNames = null, bool genericParametersOnly = false)
    {
        var required = new Dictionary<JsonObject, TypeNode>();
        var parameters = parameterDeclarations.ToArray();
        var uses = new Dictionary<JsonObject, List<JsonObject>>();
        var names = parameters.Select(parameter => Str(parameter["name"])).ToHashSet(StringComparer.Ordinal);
        void Visit(JsonNode node, Dictionary<string, JsonObject> declarations)
        {
            if (node is JsonArray array)
            {
                foreach (var child in array) Visit(child, declarations);
                return;
            }
            if (node is not JsonObject obj) return;
            // A nested declaration owns different storage, even if names repeat.
            if (obj["k"] == null && obj["params"] is JsonArray) return;
            if (Str(obj["name"]) is string usedName)
            {
                names.Add(usedName);
                if (Str(obj["k"]) is "local" or "setLocal"
                    && declarations.TryGetValue(usedName, out var usedDeclaration))
                {
                    if (!uses.TryGetValue(usedDeclaration, out var references))
                        uses[usedDeclaration] = references = new List<JsonObject>();
                    references.Add(obj);
                }
            }
            if (Str(obj["k"]) is ("block" or "valueBlock" or "cond" or "try" or "tryExpr"
                    or "forRange" or "forArray" or "forIn"))
                declarations = new Dictionary<string, JsonObject>(declarations, StringComparer.Ordinal);
            if (Str(obj["k"]) is ("callStatic" or "callInstance" or "constrainedCall"
                    or "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance" or "new" or "newClr")
                && obj["args"] is JsonArray arguments
                && ParameterTypes(obj) is { } callParameters && callParameters.Length == arguments.Count)
                for (var i = 0; i < callParameters.Length; i++)
                {
                    if (SignatureValueTypes.Of(callParameters[i]) is not TypeNode.ByRef reference
                        || arguments[i] is not JsonObject argument) continue;
                    var location = Str(argument["k"]) == "byrefOf" ? argument["inner"] as JsonObject : argument;
                    if (location == null || Str(location["k"]) != "local"
                        || Str(location["name"]) is not string local
                        || !declarations.TryGetValue(local, out var declaration)) continue;
                    // The early dispatch boundary knows a bare generic parameter's frame,
                    // but constructed local locations still await full ABI materialization.
                    if (!NativeLocalEligible(genericParametersOnly, parameters.Contains(declaration), reference.Of)) continue;
                    // A managed-reference local already stores the pointer, not
                    // the addressed value. Never replace it with value storage.
                    if (SignatureValueTypes.Of(TypeJson.Read(declaration["type"])) is TypeNode.ByRef)
                        continue;
                    var element = SignatureValueTypes.Of(reference.Of);
                    if (required.TryGetValue(declaration, out var previous) && previous != element)
                        throw new InvalidOperationException(
                            $"bir2cir: local '{local}' is required by distinct CLR managed-reference element types");
                    required[declaration] = element;
                }
            foreach (var child in obj) Visit(child.Value, declarations);
            if (Str(obj["k"]) == "var" && Str(obj["name"]) is string declared)
                declarations[declared] = obj;
        }
        var declarations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
            if (Str(parameter["name"]) is string name) declarations[name] = parameter;
        foreach (var statement in body.ToArray()) Visit(statement, declarations);
        var prefix = new List<JsonNode>();
        foreach (var (declaration, type) in required)
        {
            if (!parameters.Contains(declaration)) { declaration["type"] = TypeJson.Write(type); continue; }
            if (SignatureValueTypes.Of(TypeJson.Read(declaration["type"])) == type) continue;
            // Incoming values are by value. Keep the published Kotlin value slot, but allocate its addressable
            // local once at entry with the exact native element. All uses share this location for the whole body;
            // this is not a per-call temporary or a pointer conversion and has no copy-out step.
            var ordinal = 0;
            string name;
            do { name = "$nativeArgument$" + ordinal++; } while (!names.Add(name));
            foreach (var reference in uses.GetValueOrDefault(declaration) ?? new List<JsonObject>())
            {
                reference["name"] = name;
                if (Str(reference["k"]) == "local") reference["sty"] = TypeJson.Write(type);
            }
            prefix.Add(new JsonObject {
                ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(type),
                ["init"] = new JsonObject { ["k"] = "local", ["name"] = Str(declaration["name"]) },
            });
        }
        for (var i = prefix.Count - 1; i >= 0; i--)
        {
            // Native-argument stems are disjoint from array temporaries today. Still register this explicit
            // late declaration boundary rather than making the allocator depend on that implementation detail.
            arrayNames?.Reserve(prefix[i]);
            prologue.Insert(0, prefix[i]);
        }
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
        return Str(expression["k"]) == "newList"
            && expression["ctorRef"] is JsonObject constructor
            ? TypeJson.Read(constructor["declaringType"]) : null;
    }

    // Cold lowering may spill the same single-evaluation binding. Keep the field's layout unchanged and state an
    // exact read conversion from its known allocation. The fact is method-frame-local, not inferred from a name.
    static string TemporaryFieldKey(JsonObject node) =>
        node["recv"] is JsonObject receiver && Str(receiver["k"]) == "this"
        && TypeJson.Read(node["ownerType"]) is TypeNode owner && Str(node["name"]) is string name
            ? owner.ToString() + "\u0000" + name : null;

    static bool IsVoid(TypeNode type) => type is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };

    internal static int NumericRank(TypeNode type) => type is TypeNode.Fqn { Args: null } named ? named.Name switch
    {
        "System.Double" => 5,
        "System.Single" => 4,
        "System.Int64" or "System.UInt64" => 3,
        "System.Int32" or "System.UInt32" => 2,
        "System.Int16" or "System.UInt16" or "System.Char" => 1,
        "System.Byte" or "System.SByte" => 0,
        _ => -1,
    } : -1;

    static TypeNode ExprType(JsonNode node, IReadOnlyDictionary<string, TypeNode> locals, Index index)
    {
        if (node is not JsonObject obj) return null;
        var kind = Str(obj["k"]);
        if (kind == "local" && Str(obj["name"]) is string name && locals.TryGetValue(name, out var local))
            return local;
        if (kind == "this") return null;
        // Plain local CIR calls already carry their exact declaration contract. A Kotlin function modifier on
        // a parameter is metadata, not a reason to re-match the call against another declaration.
        if (kind is "callStatic" or "callInstance" or "constrainedCall"
            && TypeJson.Read(obj["calleeRet"]) is TypeNode declared)
            return SignatureValueTypes.Of(Close(declared, CallOwner(obj)?.Args, MethodArgs(obj)));
        if (PhysicalResult(obj, locals, index) is TypeNode physical)
        {
            // Ordinary CLR calls copy a ref return. byrefOf is the separate operation retaining its pointer;
            // arithmetic and value spills both consume the loaded element, not the method's T& signature.
            if (kind is "clrStatic" or "clrInstance" && physical is TypeNode.ByRef reference)
                physical = reference.Of;
            return SignatureValueTypes.Of(physical);
        }
        var inferred = NodeType.Of(obj, child => ExprType(child, locals, index),
            name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var elem) ? elem : null);
        return SignatureValueTypes.Of(BirTypeLowering.CanonicalExpressionResult(inferred));
    }

    static TypeNode PhysicalResult(JsonObject expression, IReadOnlyDictionary<string, TypeNode> locals, Index index)
    {
        var kind = Str(expression["k"]);
        if (kind == "cast") return TypeJson.Read(expression["type"]);
        if (ResolvedMember(expression) is JsonObject member
            && kind is "callStatic" or "callInstance" or "constrainedCall"
                or "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance"
                or "clrPropGet" or "field" or "staticField" or "clrStaticField" or "lateinitGet")
            return Close(TypeJson.Read(member["returnType"]), OwnerArgs(member), MethodArgs(expression));
        if (kind is "callStatic" or "callInstance" or "constrainedCall" && index.Method(expression) is MethodShape method)
        {
            var owner = CallOwner(expression);
            return Close(method.Return, owner?.Args, MethodArgs(expression));
        }
        if (kind is "field" or "staticField" or "lateinitGet")
        {
            var owner = FieldOwner(expression, locals, index);
            return Close(index.Field(owner?.Name, Str(expression["name"])), owner?.Args, MethodArgs(expression));
        }
        return null;
    }

    static TypeNode.Fqn FieldOwner(JsonObject node, IReadOnlyDictionary<string, TypeNode> locals, Index index)
        => TypeJson.Read(node["ownerType"] ?? node["type"]) as TypeNode.Fqn
            ?? ExprType(node["recv"], locals, index) as TypeNode.Fqn;

    static JsonObject ResolvedMember(JsonObject node)
        => node["memberRef"] as JsonObject ?? node["fieldRef"] as JsonObject;

    static TypeNode[] ParameterTypes(JsonObject node)
    {
        TypeNode[] parameters = null;
        TypeNode[] ownerArgs = null;
        if (node["memberRef"] is JsonObject member)
        {
            parameters = ReadTypes(member["parameterTypes"] as JsonArray);
            ownerArgs = OwnerArgs(member);
        }
        // NetInteropBinding carries the selected declaration here until the final MemberRef is materialized.
        parameters ??= ReadTypes(node["resolvedMemberParams"] as JsonArray)
            ?? ReadTypes(node["sig"] as JsonArray) ?? ReadTypes(node["shapeTypes"] as JsonArray)
            ?? ReadTypes(node["argTypes"] as JsonArray);
        if (parameters == null) return null;
        var methodArgs = MethodArgs(node);
        return parameters.Select(p => Close(p, ownerArgs ?? CallOwner(node)?.Args, methodArgs)).ToArray();
    }

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

    internal static TypeNode Close(TypeNode type, TypeNode[] ownerArgs, TypeNode[] methodArgs) => type switch
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
        TypeNode.FnPtr pointer => pointer with {
            Ret = Close(pointer.Ret, ownerArgs, methodArgs),
            Params = pointer.Params.Select(parameter => Close(parameter, ownerArgs, methodArgs)).ToArray(),
        },
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
