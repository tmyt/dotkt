using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A Kotlin property reference is simultaneously a KProperty value and a FunctionN value. BIR keeps that meaning as
// an ordinary generated KProperty construction plus the transient `propertyFunction.iface` fact. A CLR delegate is
// not an interface and the generated KProperty class cannot physically implement it, so a construction or typed
// KProperty value that fills a function-typed SLOT needs a forwarding closure. That representation decision belongs
// here, never in kotc.
//
// The rule is structural over slots, not syntax sites. A slot is a field/local initializer or write, a return, a
// call/constructor/delegate argument, an array initializer/write (including vararg packs), or either arm of a value
// join. Transparent valueBlock/cond/cast/array containers propagate the same target to their values. The forwarding
// class captures the KProperty through its declared interface and invokes that interface, so no generated-name,
// physical-layout, or method-body inference is involved.
static class PropertyReferenceFunctionLowering
{
    const string Marker = "propertyFunction";
    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();

    sealed class Context
    {
        public TypeNode Ret;
        public readonly Dictionary<string, TypeNode> Locals = new(StringComparer.Ordinal);
    }

    static readonly Dictionary<(string Owner, string Name), TypeNode> _fields = new();
    static readonly List<JsonObject> _adapters = new();
    static readonly HashSet<string> _typeNames = new(StringComparer.Ordinal);
    static int _nextAdapter;
    static string _scope;

    public static IReadOnlyDictionary<(string Owner, string Name), TypeNode> CollectFieldSlots(
        IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<(string Owner, string Name), TypeNode>();
        void IndexFields(JsonObject type)
        {
            if (Str(type["name"]) is not string owner) return;
            if (type["fields"] is JsonArray fields)
                foreach (var field in fields.OfType<JsonObject>())
                    if (Str(field["name"]) is string name && TypeJson.Read(field["type"]) is TypeNode slot)
                        result[(owner, name)] = slot;
            if (type["types"] is JsonArray nested)
                foreach (var child in nested.OfType<JsonObject>()) IndexFields(child);
        }
        foreach (var file in roots.OfType<JsonObject>())
        {
            if (Str(file["fileClass"]) is string fileClass && file["fields"] is JsonArray fields)
                foreach (var field in fields.OfType<JsonObject>())
                    if (Str(field["name"]) is string name && TypeJson.Read(field["type"]) is TypeNode slot)
                        result[(fileClass, name)] = slot;
            if (file["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) IndexFields(type);
        }
        return result;
    }

    public static void Apply(JsonNode root,
        IReadOnlyDictionary<(string Owner, string Name), TypeNode> moduleFields)
    {
        if (root is not JsonObject file) return;
        _fields.Clear();
        foreach (var field in moduleFields) _fields[field.Key] = field.Value;
        _adapters.Clear();
        _typeNames.Clear();
        _nextAdapter = 0;
        _scope = string.Concat((Str(file["fileClass"]) ?? "File")
            .Select(c => char.IsLetterOrDigit(c) ? c : '_'));

        if (file["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) IndexType(type);
        if (file["fields"] is JsonArray fileFields)
        {
            var fileClass = Str(file["fileClass"]);
            foreach (var field in fileFields.OfType<JsonObject>())
            {
                if (fileClass != null && Str(field["name"]) is string name
                    && TypeJson.Read(field["type"]) is TypeNode slot)
                    _fields[(fileClass, name)] = slot;
                ProcessField(field, null);
            }
        }
        if (file["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>()) ProcessMethod(method);
        if (file["types"] is JsonArray declarations)
            foreach (var type in declarations.OfType<JsonObject>().ToList()) ProcessType(type);

        StripMarkers(file);
        if (_adapters.Count == 0) return;
        var outTypes = file["types"] as JsonArray;
        if (outTypes == null) { outTypes = new JsonArray(); file["types"] = outTypes; }
        foreach (var adapter in _adapters) outTypes.Add(adapter);
    }

    static void IndexType(JsonObject type)
    {
        if (Str(type["name"]) is not string owner) return;
        _typeNames.Add(owner);
        if (type["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>())
                if (Str(field["name"]) is string name && TypeJson.Read(field["type"]) is TypeNode slot)
                    _fields[(owner, name)] = slot;
        if (type["types"] is JsonArray nested)
            foreach (var child in nested.OfType<JsonObject>()) IndexType(child);
    }

    static void ProcessType(JsonObject type)
    {
        if (type["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>()) ProcessField(field, null);
        if (type["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>()) ProcessMethod(ctor, constructor: true);
        if (type["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>()) ProcessMethod(method);
        if (type["types"] is JsonArray nested)
            foreach (var child in nested.OfType<JsonObject>()) ProcessType(child);
    }

    static void ProcessField(JsonObject field, Context context)
    {
        if (TypeJson.Read(field["type"]) is not TypeNode slot) return;
        if (field["init"] is JsonObject init) RewriteValue(init, slot, context ?? new Context());
    }

    static void ProcessMethod(JsonObject method, bool constructor = false)
    {
        var context = new Context { Ret = constructor ? null : TypeJson.Read(method["ret"]) };
        if (method["params"] is JsonArray parameters)
            foreach (var parameter in parameters.OfType<JsonObject>())
                if (Str(parameter["name"]) is string name && TypeJson.Read(parameter["type"]) is TypeNode type)
                    context.Locals[name] = type;
        if (method["body"] is JsonArray body)
        {
            CollectLocals(body, context.Locals);
            ProcessNode(body, context);
        }
        if (method["baseArgs"] is JsonArray baseArgs)
            RewriteArguments(method, baseArgs, method["delegationSig"] as JsonArray, context);
        if (method["thisArgs"] is JsonArray thisArgs)
            RewriteArguments(method, thisArgs, method["delegationSig"] as JsonArray, context);
    }

    static void CollectLocals(JsonNode node, Dictionary<string, TypeNode> into)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) is "localFun" or "typeDef") return;
            if (Str(obj["k"]) == "var" && Str(obj["name"]) is string name
                && TypeJson.Read(obj["type"]) is TypeNode type)
                into[name] = type;
            foreach (var pair in obj)
                if (pair.Value != null && pair.Key != "synthClass") CollectLocals(pair.Value, into);
        }
        else if (node is JsonArray array)
            foreach (var item in array) if (item != null) CollectLocals(item, into);
    }

    static void ProcessNode(JsonNode node, Context context)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array.ToList()) if (item != null) ProcessNode(item, context);
            return;
        }
        if (node is not JsonObject obj) return;

        var kind = Str(obj["k"]);
        if (kind == "callInstance" && RewriteFunctionSlotInvoke(obj, context)) kind = "delegateInvoke";
        switch (kind)
        {
            case "var":
                if (TypeJson.Read(obj["type"]) is TypeNode localType && obj["init"] is JsonObject init)
                    RewriteValue(init, localType, context);
                break;
            case "setLocal":
                if (Str(obj["name"]) is string local && context.Locals.TryGetValue(local, out var target)
                    && obj["value"] is JsonObject localValue)
                    RewriteValue(localValue, target, context);
                break;
            case "setField": case "setFieldExpr":
                if (FieldType(obj) is TypeNode fieldType && obj["value"] is JsonObject fieldValue)
                    RewriteValue(fieldValue, fieldType, context);
                break;
            case "staticFieldSet": case "setStaticField": case "setStaticFieldExpr": case "clrStaticFieldSet":
                if (FieldType(obj) is TypeNode staticType
                    && (obj["value"] ?? obj["e"]) is JsonObject staticValue)
                    RewriteValue(staticValue, staticType, context);
                break;
            case "return": case "returnExpr":
                if (context.Ret != null && obj["value"] is JsonObject returned)
                    RewriteValue(returned, context.Ret, context);
                break;
            case "arraySet":
                if (TypeJson.Read(obj["elem"]) is TypeNode arrayElement
                    && obj["value"] is JsonObject arrayValue)
                    RewriteValue(arrayValue, arrayElement, context);
                break;
            case "newArray": case "newArrayInit": case "newArraySized":
                if (TypeJson.Read(obj["elem"]) is TypeNode elem && obj["elems"] is JsonArray elems)
                    foreach (var item in elems.OfType<JsonObject>()) RewriteValue(item, elem, context);
                if (kind == "newArrayInit" && TypeJson.Read(obj["funcType"]) is TypeNode initializerType
                    && obj["init"] is JsonObject initializer)
                    RewriteValue(initializer, initializerType, context);
                break;
            case "clrPropSet":
                if (LastParameterType(obj) is TypeNode propertyType
                    && (obj["value"] ?? obj["e"]) is JsonObject propertyValue)
                    RewriteValue(propertyValue, propertyType, context);
                break;
            case "localFun":
                ProcessMethod(obj);
                return;
            case "typeDef":
                if (obj["type"] is JsonObject localTypeDef) ProcessType(localTypeDef);
                return;
        }

        var argumentsTargeted = false;
        if (obj["args"] is JsonArray args)
        {
            var parameters = ParameterVector(obj);
            argumentsTargeted = parameters?.Count == args.Count;
            RewriteArguments(obj, args, parameters, context);
        }
        if (obj["synthClass"] is JsonObject synth) ProcessSyntheticClass(synth);

        // Targeted children were already visited with their declared slot. Re-visiting is harmless after adaptation,
        // but skipping them keeps the walk linear and prevents a marker from being stripped before an outer slot sees it.
        foreach (var pair in obj.ToList())
        {
            if (pair.Value == null || pair.Key is Marker or "synthClass") continue;
            var targetedChild = kind switch
            {
                "var" => pair.Key == "init",
                "setLocal" or "setField" or "setFieldExpr" or "staticFieldSet" or "setStaticField"
                    or "setStaticFieldExpr" or "clrStaticFieldSet" or "arraySet"
                    or "return" or "returnExpr" or "clrPropSet" => pair.Key is "value" or "e",
                "newArray" or "newArraySized" => pair.Key == "elems",
                "newArrayInit" => pair.Key is "elems" or "init",
                _ => false,
            };
            if (targetedChild || pair.Key == "args" && argumentsTargeted) continue;
            ProcessNode(pair.Value, context);
        }
    }

    static void ProcessSyntheticClass(JsonObject synth)
    {
        var context = new Context { Ret = TypeJson.Read(synth["ret"]) };
        if (synth["params"] is JsonArray parameters)
            foreach (var parameter in parameters.OfType<JsonObject>())
                if (Str(parameter["name"]) is string name && TypeJson.Read(parameter["type"]) is TypeNode type)
                    context.Locals[name] = type;
        if (synth["body"] is JsonArray body)
        {
            CollectLocals(body, context.Locals);
            ProcessNode(body, context);
        }
    }

    // FIR data flow may narrow a mutable function local to the concrete KProperty reference most recently assigned
    // to it, and consequently spell `f(x)` as KProperty1.invoke even though the value is stored physically in the
    // declared function slot. The slot declaration remains authoritative: dispatch through its delegate. This is the
    // invocation half of adapting the preceding setLocal and prevents a Func value from being cast back to KProperty.
    static bool RewriteFunctionSlotInvoke(JsonObject call, Context context)
    {
        if (Str(call["method"]) != "invoke" || call["recv"] is not JsonObject receiver)
            return false;
        var localReceiver = Str(receiver["k"]) == "local" ? receiver
            : Str(receiver["k"]) == "cast" && receiver["e"] is JsonObject inner && Str(inner["k"]) == "local"
                ? inner : null;
        if (localReceiver == null
            || Str(localReceiver["name"]) is not string name
            || !context.Locals.TryGetValue(name, out var declared)
            || FunctionTarget(declared) is not TypeNode.Fn function
            || TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn owner
            || !owner.Name.StartsWith("kotlin.reflect.KProperty", StringComparison.Ordinal)
                && !owner.Name.StartsWith("kotlin.reflect.KMutableProperty", StringComparison.Ordinal))
            return false;
        if (call["args"] is not JsonArray args || args.Count != function.DelegateParams.Length) return false;

        var keep = call.Where(pair => pair.Key is "sty" or "pos").ToList();
        var recv = localReceiver.DeepClone();
        var arguments = args.DeepClone();
        foreach (var key in call.Select(pair => pair.Key).ToList()) call.Remove(key);
        foreach (var pair in keep) call[pair.Key] = pair.Value?.DeepClone();
        call["k"] = "delegateInvoke";
        call["funcType"] = TypeJson.Write(function);
        call["recv"] = recv;
        call["args"] = arguments;
        return true;
    }

    static JsonArray ParameterVector(JsonObject call)
    {
        if (Str(call["k"]) == "delegateInvoke"
            && TypeJson.Read(call["funcType"]) is TypeNode.Fn function)
            return new JsonArray(function.DelegateParams.Select(TypeJson.Write).ToArray());
        return call["resolvedMemberParams"] as JsonArray ?? call["shapeTypes"] as JsonArray
            ?? call["argTypes"] as JsonArray ?? call["sig"] as JsonArray;
    }

    static TypeNode LastParameterType(JsonObject node)
    {
        var vector = ParameterVector(node);
        return vector is { Count: > 0 } ? CloseParameter(node, TypeJson.Read(vector[^1])) : null;
    }

    static void RewriteArguments(JsonObject call, JsonArray args, JsonArray parameters, Context context)
    {
        if (parameters == null || parameters.Count != args.Count) return;
        for (var i = 0; i < args.Count; i++)
            if (args[i] is JsonObject argument && TypeJson.Read(parameters[i]) is TypeNode parameter)
                RewriteValue(argument, CloseParameter(call, parameter), context);
    }

    static TypeNode CloseParameter(JsonObject call, TypeNode type)
    {
        if (type == null) return null;
        var owner = TypeJson.Read(call["type"] ?? call["ownerType"] ?? call["owner"] ?? call["calleeOwner"])
            as TypeNode.Fqn;
        var ownerArgs = owner?.Args ?? Array.Empty<TypeNode>();
        var methodArgs = (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray()
            ?? Array.Empty<TypeNode>();
        return Substitute(type, ownerArgs, methodArgs);
    }

    static TypeNode Substitute(TypeNode type, TypeNode[] ownerArgs, TypeNode[] methodArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodArgs.Length => methodArgs[tv.I],
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(t => Substitute(t, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Substitute(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Substitute(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(Substitute(a.Elem, ownerArgs, methodArgs), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(Substitute(b.Of, ownerArgs, methodArgs)),
        TypeNode.Ptr p => new TypeNode.Ptr(Substitute(p.Of, ownerArgs, methodArgs)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, Substitute(m.M, ownerArgs, methodArgs),
            Substitute(m.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Substitute(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(t => Substitute(t, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : Substitute(fn.Recv, ownerArgs, methodArgs), fn.Clr,
            fn.Ctx?.Select(t => Substitute(t, ownerArgs, methodArgs)).ToArray()),
        _ => type,
    };

    static TypeNode FieldType(JsonObject write)
    {
        if (TypeJson.Read(write["ownerType"] ?? write["type"]) is not TypeNode.Fqn owner
            || Str(write["name"]) is not string name)
            return TypeJson.Read(write["memberType"]);
        if (!_fields.TryGetValue((owner.Name, name), out var slot)) return TypeJson.Read(write["memberType"]);
        return owner.Args is { Length: > 0 }
            ? Substitute(slot, owner.Args, Array.Empty<TypeNode>())
            : slot;
    }

    static void RewriteValue(JsonObject value, TypeNode target, Context context)
    {
        if (target == null) { ProcessNode(value, context); return; }
        var function = FunctionTarget(target);
        if (function is { Suspend: false } && PropertyInterface(value, context) is TypeNode.Fqn iface)
        {
            Adapt(value, iface, function);
            ProcessNode(value, context);
            return;
        }

        switch (Str(value["k"]))
        {
            case "valueBlock":
                if (value["stmts"] is JsonNode statements) ProcessNode(statements, context);
                if (value["result"] is JsonObject result) RewriteValue(result, target, context);
                return;
            case "cond":
                if (value["cond"] is JsonNode condition) ProcessNode(condition, context);
                if (value["then"] is JsonObject thenValue) RewriteValue(thenValue, target, context);
                if (value["else"] is JsonObject elseValue) RewriteValue(elseValue, target, context);
                return;
            case "cast":
                if (TypeJson.Read(value["type"]) is TypeNode castTarget
                    && value["e"] is JsonObject castValue)
                    RewriteValue(castValue, castTarget, context);
                else ProcessNode(value, context);
                return;
            case "newArray": case "newArrayInit": case "newArraySized":
                if (ArrayTarget(target) is TypeNode elem && value["elems"] is JsonArray elements)
                    foreach (var item in elements.OfType<JsonObject>()) RewriteValue(item, elem, context);
                ProcessNode(value, context);
                return;
        }
        ProcessNode(value, context);
    }

    static TypeNode.Fqn PropertyInterface(JsonObject value, Context context)
    {
        if (value[Marker] is JsonObject marker
            && TypeJson.Read(marker["iface"]) is TypeNode.Fqn carried)
            return carried;
        TypeNode type = StaticType.Surface(value, BirScope.FromVars(context.Locals));
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        return type is TypeNode.Fqn iface
            && (iface.Name.StartsWith("kotlin.reflect.KProperty", StringComparison.Ordinal)
                || iface.Name.StartsWith("kotlin.reflect.KMutableProperty", StringComparison.Ordinal))
            ? iface : null;
    }

    static TypeNode.Fn FunctionTarget(TypeNode type)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        return type as TypeNode.Fn;
    }

    static TypeNode ArrayTarget(TypeNode type)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        return (type as TypeNode.Array)?.Elem;
    }

    static void Adapt(JsonObject construction, TypeNode.Fqn authoredIface, TypeNode.Fn target)
    {
        if (authoredIface.Args is not { Length: > 0 } ifaceArgs)
            throw new InvalidOperationException("bir2cir: propertyFunction interface has no value argument");
        var arity = authoredIface.Name switch
        {
            "kotlin.reflect.KProperty0" or "kotlin.reflect.KMutableProperty0" => 0,
            "kotlin.reflect.KProperty1" or "kotlin.reflect.KMutableProperty1" => 1,
            "kotlin.reflect.KProperty2" or "kotlin.reflect.KMutableProperty2" => 2,
            _ => throw new InvalidOperationException(
                $"bir2cir: propertyFunction names non-KProperty interface '{authoredIface.Name}'"),
        };
        if (ifaceArgs.Length != arity + 1 || target.DelegateParams.Length != arity)
            throw new InvalidOperationException(
                $"bir2cir: KProperty{arity} cannot fill function slot with {target.DelegateParams.Length} parameter(s)");

        var canonicalName = $"kotlin.reflect.KProperty{arity}";
        var actuals = new List<TypeNode>();
        var allowsRefStruct = new List<bool>();
        TypeNode Dense(TypeNode actual, bool propertyArgument)
        {
            if (!MentionsTypeVariable(actual)) return actual;
            var index = actuals.FindIndex(t => t.Equals(actual));
            if (index < 0)
            {
                index = actuals.Count;
                actuals.Add(actual);
                allowsRefStruct.Add(!propertyArgument);
            }
            else if (propertyArgument) allowsRefStruct[index] = false;
            return new TypeNode.Tv("type", index);
        }

        var denseIfaceArgs = ifaceArgs.Select(t => Dense(t, propertyArgument: true)).ToArray();
        var denseParams = target.DelegateParams.Select(t => Dense(t, propertyArgument: false)).ToArray();
        var denseRet = Dense(target.Ret, propertyArgument: false);
        var denseIface = new TypeNode.Fqn(canonicalName, denseIfaceArgs);

        string name;
        do name = $"dotkt${_scope}$PropertyFunctionAdapter{_nextAdapter++}";
        while (_typeNames.Contains(name));
        _typeNames.Add(name);

        var self = new TypeNode.Fqn(name);
        var field = new JsonObject { ["name"] = "p", ["type"] = TypeJson.Write(denseIface) };
        var callArgs = new JsonArray(denseParams.Select((parameter, i) =>
        {
            JsonNode argument = new JsonObject { ["k"] = "local", ["name"] = "p" + i };
            if (!parameter.Equals(denseIfaceArgs[i]))
                argument = new JsonObject
                {
                    ["k"] = "cast",
                    ["type"] = TypeJson.Write(denseIfaceArgs[i]),
                    ["e"] = argument,
                };
            return argument;
        }).ToArray());
        var call = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(denseIface),
            ["virtual"] = true,
            ["recv"] = new JsonObject
            {
                ["k"] = "field",
                ["ownerType"] = TypeJson.Write(self),
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["name"] = "p",
            },
            ["method"] = "invoke",
            ["sig"] = new JsonArray(Enumerable.Range(0, arity)
                .Select(i => TypeJson.Write(new TypeNode.Tv("type", i))).ToArray()),
            ["args"] = callArgs,
        };
        JsonNode result = call;
        var naturalRet = denseIfaceArgs[^1];
        if (!naturalRet.Equals(denseRet) && denseRet is TypeNode.Tv)
            result = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(denseRet), ["e"] = call };
        var body = new JsonArray
        {
            target.Ret is TypeNode.Fqn { Name: "kotlin.Unit" }
                ? new JsonObject { ["k"] = "exprStmt", ["expr"] = call }
                : new JsonObject { ["k"] = "return", ["value"] = result },
        };

        var ctor = new JsonObject
        {
            ["params"] = new JsonArray { field.DeepClone() },
            ["baseArgs"] = null,
            ["body"] = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "setField",
                    ["ownerType"] = TypeJson.Write(self),
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["name"] = "p",
                    ["value"] = new JsonObject { ["k"] = "local", ["name"] = "p" },
                },
            },
        };
        var invoke = new JsonObject
        {
            ["name"] = "invoke",
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = false,
            ["params"] = new JsonArray(denseParams.Select((type, i) => (JsonNode)new JsonObject
            {
                ["name"] = "p" + i,
                ["type"] = TypeJson.Write(type),
            }).ToArray()),
            ["ret"] = TypeJson.Write(denseRet),
            ["body"] = body,
        };
        var adapter = new JsonObject
        {
            ["name"] = name,
            ["kind"] = "class",
            ["generated"] = true,
            ["base"] = null,
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray { field },
            ["ctors"] = new JsonArray { ctor },
            ["methods"] = new JsonArray { invoke },
        };
        if (actuals.Count > 0)
            adapter["typeParams"] = new JsonArray(actuals.Select((_, i) =>
            {
                var parameter = new JsonObject { ["name"] = "T" + i };
                if (allowsRefStruct[i]) parameter["specialConstraints"] = new JsonArray { "allowsRefStruct" };
                return (JsonNode)parameter;
            }).ToArray());
        _adapters.Add(adapter);

        var captured = construction.DeepClone() as JsonObject;
        captured.Remove(Marker);
        foreach (var key in construction.Select(pair => pair.Key).ToList()) construction.Remove(key);
        construction["k"] = "newClosure";
        construction["closureType"] = TypeJson.Write(actuals.Count == 0
            ? self : new TypeNode.Fqn(name, actuals.ToArray()));
        construction["captures"] = new JsonArray { captured };
        construction["method"] = "invoke";
        construction["funcType"] = TypeJson.Write(target);
        if (actuals.Count > 0)
            construction["typeArgs"] = new JsonArray(actuals.Select(TypeJson.Write).ToArray());
    }

    static bool MentionsTypeVariable(TypeNode type) => type switch
    {
        TypeNode.Tv => true,
        TypeNode.Fqn { Args: { } args } => args.Any(MentionsTypeVariable),
        TypeNode.Projection p => MentionsTypeVariable(p.Of),
        TypeNode.Nullable n => MentionsTypeVariable(n.Of),
        TypeNode.Oblivious o => MentionsTypeVariable(o.Of),
        TypeNode.Array a => MentionsTypeVariable(a.Elem),
        TypeNode.ByRef b => MentionsTypeVariable(b.Of),
        TypeNode.Ptr p => MentionsTypeVariable(p.Of),
        TypeNode.Mod m => MentionsTypeVariable(m.M) || MentionsTypeVariable(m.Of),
        TypeNode.Fn fn => MentionsTypeVariable(fn.Ret) || fn.DelegateParams.Any(MentionsTypeVariable),
        _ => false,
    };

    static void StripMarkers(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove(Marker);
            foreach (var pair in obj.ToList()) if (pair.Value != null) StripMarkers(pair.Value);
        }
        else if (node is JsonArray array)
            foreach (var item in array.ToList()) if (item != null) StripMarkers(item);
    }
}
