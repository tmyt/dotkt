using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CLR lexical-access projection. Kotlin declarations keep their source visibility; when ownership lowering places a
// frontend-valid caller in a different CLR TypeDef and direct CLR access is no longer legal, put a private extern
// [UnsafeAccessor] method on that physical caller and route the edge through it. No target declaration is widened.
//
// kotc carries the accessed declaration's owner/method generic frames in BIR. Re-declare owner parameters on a
// synthesized generic holder and method parameters on the accessor itself, preserving the exact form/index/constraints
// required by .NET 9+'s strict UnsafeAccessor signature matcher. Invalid/old BIR is outside the toolchain contract.
static class UnsafeAccessorLowering
{
    const string AttributeFqn = "System.Runtime.CompilerServices.UnsafeAccessorAttribute";
    const string KindFqn = "System.Runtime.CompilerServices.UnsafeAccessorKind";

    sealed class Host
    {
        public Host(string name, JsonObject declaration, JsonArray methods, string nestedIn, JsonObject root)
        {
            Name = name;
            Declaration = declaration;
            Methods = methods;
            NestedIn = nestedIn;
            Root = root;
        }

        public string Name { get; }
        public JsonObject Declaration { get; }
        public JsonArray Methods { get; }
        public string NestedIn { get; }
        public JsonObject Root { get; }
        public IReadOnlyList<JsonObject> LookupMethods { get; set; } = Array.Empty<JsonObject>();
        public IReadOnlyList<JsonObject> LookupConstructors { get; set; } = Array.Empty<JsonObject>();
        public IReadOnlyList<JsonObject> LookupFields { get; set; } = Array.Empty<JsonObject>();
    }
    sealed record AccessorDefinition(string EntryName, string HolderName, JsonArray Signature,
        int OwnerTypeParamCount, int MethodTypeParamCount);

    static int _counter;
    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();
    static bool Bool(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    public static void ApplyAll(IReadOnlyList<JsonNode> roots)
    {
        var callers = new List<Host>();
        foreach (var root in roots.OfType<JsonObject>())
        {
            if (Str(root["fileClass"]) is string fileClass)
                callers.Add(new Host(fileClass, root, EnsureArray(root, "methods"), null, root));
            if (root["types"] is not JsonArray types) continue;
            foreach (var type in types.OfType<JsonObject>())
                if (Str(type["name"]) is string name)
                    callers.Add(new Host(name, type, EnsureArray(type, "methods"), Str(type["nestedIn"]), root));
        }
        var hosts = callers.GroupBy(host => host.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var occurrences = group.ToArray();
                var methods = occurrences.SelectMany(host => host.Methods.OfType<JsonObject>()).ToArray();
                var constructors = occurrences.SelectMany(host =>
                    (host.Declaration["ctors"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    .ToArray();
                var fields = occurrences.SelectMany(host =>
                    (host.Declaration["fields"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    .ToArray();
                foreach (var occurrence in occurrences)
                {
                    occurrence.LookupMethods = methods;
                    occurrence.LookupConstructors = constructors;
                    occurrence.LookupFields = fields;
                }
                return occurrences[0];
            }, StringComparer.Ordinal);
        if (hosts.Count == 0) return;

        var accessors = new Dictionary<string, AccessorDefinition>(StringComparer.Ordinal);
        foreach (var caller in callers)
        {
            // Snapshot source/synthesized executable members: accessors appended during this walk have no body and
            // must not become input to their own synthesis.
            foreach (var method in caller.Methods.OfType<JsonObject>().ToArray())
                if (method["body"] is JsonNode body) Rewrite(body, caller, hosts, accessors);
            if (caller.Declaration["ctors"] is JsonArray ctors)
                foreach (var ctor in ctors.OfType<JsonObject>())
                {
                    if (ctor["preStmts"] is JsonNode pre) Rewrite(pre, caller, hosts, accessors);
                    if (ctor["delegation"] is JsonNode delegation) Rewrite(delegation, caller, hosts, accessors);
                    // Constructor delegation arguments execute in the caller TypeDef just like its body. kotc keeps
                    // them in dedicated baseArgs/thisArgs vectors, so private sibling/file-class edges there need the
                    // same UnsafeAccessor projection (SafeContinuation(delegate, UNDECIDED_BOX) is the concrete case).
                    if (ctor["baseArgs"] is JsonNode baseArgs) Rewrite(baseArgs, caller, hosts, accessors);
                    if (ctor["thisArgs"] is JsonNode thisArgs) Rewrite(thisArgs, caller, hosts, accessors);
                    if (ctor["body"] is JsonNode body) Rewrite(body, caller, hosts, accessors);
                }
            if (caller.Declaration["fields"] is JsonArray fields)
                foreach (var field in fields.OfType<JsonObject>())
                    if (field["init"] is JsonNode init) Rewrite(init, caller, hosts, accessors);
        }
    }

    public static void DropFacts(IReadOnlyList<JsonNode> roots)
    {
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                obj.Remove("memberVisibility");
                obj.Remove("memberType");
                obj.Remove("memberOwnerTypeParams");
                obj.Remove("memberMethodTypeParams");
                obj.Remove("memberReturnType");
                // Every `new` carries the frontend-selected constructor declaration signature.  Same-unit
                // constructor binding consumes it after physical lowering; it is not an UnsafeAccessor-only fact.
                if (Str(obj["k"]) != "new") obj.Remove("memberSignature");
                foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList()) Walk(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) if (child != null) Walk(child);
        }
        foreach (var root in roots) Walk(root);
    }

    static JsonArray EnsureArray(JsonObject owner, string key)
    {
        if (owner[key] is JsonArray array) return array;
        owner[key] = array = new JsonArray();
        return array;
    }

    static void Rewrite(JsonNode node, Host caller, IReadOnlyDictionary<string, Host> hosts,
        Dictionary<string, AccessorDefinition> accessors)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.ToList()) if (child != null) Rewrite(child, caller, hosts, accessors);
            return;
        }
        if (node is not JsonObject obj) return;

        foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
            Rewrite(child, caller, hosts, accessors);

        var kind = Str(obj["k"]);
        if (kind is "callInstance" or "callStatic") RewriteMethod(obj, kind, caller, hosts, accessors);
        else if (kind == "newBoundDelegate") RewriteBoundDelegate(obj, caller, hosts, accessors);
        else if (kind == "new") RewriteConstructor(obj, caller, hosts, accessors);
        else if (kind is "field" or "setField" or "setFieldExpr" or "lateinitGet"
            or "staticField" or "staticFieldSet")
            RewriteField(obj, kind, caller, hosts, accessors);
        else
        {
            obj.Remove("memberVisibility");
            obj.Remove("memberType");
            obj.Remove("memberOwnerTypeParams");
            obj.Remove("memberMethodTypeParams");
            obj.Remove("memberReturnType");
            obj.Remove("memberSignature");
        }
    }

    static void RewriteMethod(JsonObject access, string kind, Host caller,
        IReadOnlyDictionary<string, Host> hosts, Dictionary<string, AccessorDefinition> accessors)
    {
        var superCall = Bool(access["super"]);
        var frontendVisibility = Str(access["memberVisibility"]);
        var ownerTypeParams = access["memberOwnerTypeParams"] as JsonArray;
        var methodTypeParams = access["memberMethodTypeParams"] as JsonArray;
        var memberSignature = access["memberSignature"] as JsonArray;
        var memberReturnType = access["memberReturnType"]?.DeepClone();
        access.Remove("memberVisibility");
        access.Remove("memberOwnerTypeParams");
        access.Remove("memberMethodTypeParams");
        access.Remove("memberSignature");
        access.Remove("memberReturnType");
        // UnsafeAccessorKind.Method always performs virtual dispatch. A frontend-authorized `super.X()` is instead
        // a lexical non-virtual edge. It may stay direct only when the call consumes THIS method's `this`; verified IL
        // rejects a non-virtual base call on a captured outer receiver (`ThisMismatch`). A lifted carrier therefore
        // calls a private instance forwarder on that outer derived owner, whose own body performs the base call on
        // its real `this`. This preserves dispatch without widening the base member.
        if (superCall)
        {
            if (Str(access["recv"]?["k"]) == "this") return;
            RewriteNestedSuperMethod(access, caller, hosts, accessors, methodTypeParams, memberSignature,
                memberReturnType);
            return;
        }
        // Top-level Kotlin calls deliberately keep `owner:null` for semantic substitutions and carry their exact
        // file-facade dispatch identity separately. That identity is also the UnsafeAccessor target owner.
        var ownerNode = access["ownerType"] ?? access["owner"] ?? access["calleeOwner"];
        if (TypeJson.Read(ownerNode) is not TypeNode.Fqn ownerType
            || Str(access["method"]) is not string targetName)
            return;
        KotlinPropertyAccessors.TryCallIdentity(access, out var propertyName, out var propertyAccessor);

        hosts.TryGetValue(ownerType.Name, out var targetHost);
        if (targetHost != null && DirectPrivateAccessIsValid(caller.Name, targetHost.Name, hosts)) return;

        var signature = SignatureOf(access);
        var methodArity = access["typeArgs"] is JsonArray typeArgs ? typeArgs.Count : 0;
        JsonObject target = null;
        if (targetHost != null)
        {
            var candidates = targetHost.LookupMethods
                .Where(method => !KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
                    && (propertyAccessor != null
                        ? KotlinPropertyAccessors.TryIdentity(method, out var candidateProperty,
                            out var candidateAccessor)
                            && candidateProperty == propertyName && candidateAccessor == propertyAccessor
                        : Str(method["name"]) == targetName
                            && !KotlinPropertyAccessors.TryIdentity(method, out _, out _))
                    && Bool(method["static"]) == (kind == "callStatic")
                    && (method["params"] is JsonArray parameters ? parameters.Count : 0) == signature.Count
                    && (method["typeParams"] is JsonArray ownParams ? ownParams.Count : 0) == methodArity)
                .ToArray();
            target = SelectMethod(candidates, signature, ownerType.Args);
            if (target == null || Str(target["vis"]) is not ("private" or "protected")) return;
            // UnsafeAccessorAttribute names the actual target MethodDef. The call still carries its Kotlin property
            // identity at this point, while the declaration has already passed through the physical allocator.
            targetName = Str(target["name"])
                ?? throw new InvalidOperationException("private Kotlin accessor has no physical method name");
        }
        else if (frontendVisibility is not ("private" or "protected"))
            return;

        var targetStatic = kind == "callStatic";
        // `ret` is the declaration spelling and can be absent or stale after default-argument expansion;
        // `dynRet`/`sty` carry the instantiated expression result.  A local declaration remains authoritative.
        JsonNode declaredAccessorReturn;
        if (target?["ret"] is JsonNode declaredReturn && TypeJson.Read(declaredReturn) is TypeNode declaredType)
            declaredAccessorReturn = TypeJson.Write(declaredType);
        else if (memberReturnType != null)
            declaredAccessorReturn = memberReturnType;
        else
            declaredAccessorReturn = access["dynRet"]?.DeepClone()
                ?? access["sty"]?.DeepClone()
                ?? access["ret"]?.DeepClone()
                ?? TypeJson.Fqn("kotlin.Unit");
        JsonNode callReturnType = access["dynRet"]?.DeepClone()
            ?? access["sty"]?.DeepClone()
            ?? access["ret"]?.DeepClone();
        if (callReturnType == null && TypeJson.Read(declaredAccessorReturn) is TypeNode openReturn)
        {
            if (ownerType.Args is { Length: > 0 }) openReturn = SubstituteOwnerSlots(openReturn, ownerType.Args);
            callReturnType = TypeJson.Write(openReturn);
        }
        callReturnType ??= declaredAccessorReturn.DeepClone();
        var targetDeclarationId = Str(target?[DeclarationIdentityBinding.Key] ?? access[DeclarationIdentityBinding.Key]);
        var key = $"{caller.Name}|method|{ownerType.Name}|{targetName}|{targetStatic}|{methodArity}|" +
                  string.Join(";", signature.Select(TypeKey)) + "|" + TypeKey(declaredAccessorReturn) +
                  "|declaration:" + targetDeclarationId;
        var definition = EnsureAccessor(caller, accessors, key, targetName, targetStatic ? 2 : 1,
            ownerNode, PhysicalOwnerTypeParams(targetHost, ownerTypeParams),
            PhysicalMethodTypeParams(target, methodTypeParams),
            declaredAccessorReturn, signature, includeTarget: true,
            targetDeclarationId: targetDeclarationId);

        var args = new JsonArray();
        if (targetStatic)
            args.Add(new JsonObject { ["k"] = "default", ["type"] = ownerNode.DeepClone() });
        else
            args.Add(access["recv"]?.DeepClone());
        if (access["args"] is JsonArray originalArgs)
            foreach (var arg in originalArgs) args.Add(arg?.DeepClone());

        var callOwner = AccessorCallOwner(caller, definition, ownerType.Args ?? Array.Empty<TypeNode>());
        var replacement = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = callOwner.DeepClone(),
            ["ownerType"] = callOwner,
            ["method"] = definition.EntryName,
            ["sig"] = definition.Signature.DeepClone(),
            ["args"] = args,
            ["ret"] = callReturnType.DeepClone(),
        };
        if (access["typeArgs"] is JsonArray originalTypeArgs)
            replacement["typeArgs"] = originalTypeArgs.DeepClone();
        // The replacement is a call to the synthesized accessor, so its expression stamp must describe
        // that accessor's return rather than preserve a possibly stale stamp from the rewritten edge.
        replacement["sty"] = callReturnType.DeepClone();
        Replace(access, replacement);
    }

    static void RewriteNestedSuperMethod(JsonObject access, Host caller,
        IReadOnlyDictionary<string, Host> hosts, Dictionary<string, AccessorDefinition> accessors,
        JsonArray methodTypeParams, JsonArray memberSignature, JsonNode memberReturnType)
    {
        if (access["recv"] is not JsonObject receiver
            || NodeType.Of(receiver) is not TypeNode.Fqn receiverType
            || TypeJson.Read(access["ownerType"]) is not TypeNode.Fqn targetOwner
            || Str(access["method"]) is not string targetName)
            throw new InvalidOperationException("Nested Kotlin super call is missing its receiver/target facts");

        var forwarderHost = EnclosingHost(caller, receiverType.Name, hosts)
            ?? throw new InvalidOperationException(
                $"Nested Kotlin super receiver '{receiverType.Name}' is not an enclosing physical owner of '{caller.Name}'");
        // A synthesized carrier's physical type frame includes every captured owner slot. Its copied call-site
        // owner token can consequently contain the carrier frame rather than the base declaration's arity. Recover
        // the exact constructed super owner from the derived declaration graph; this is a CLR representation choice,
        // while memberSignature below remains the frontend's declaration-frame fact.
        var resolvedOwner = ResolveConstructedAncestor(forwarderHost, targetOwner.Name, hosts) ?? targetOwner;
        var ownerArgs = resolvedOwner.Args ?? Array.Empty<TypeNode>();
        var declarationSignature = memberSignature ?? SignatureOf(access);
        var signature = new JsonArray(declarationSignature.Select(type =>
        {
            var parsed = TypeJson.Read(type)
                ?? throw new InvalidOperationException($"Kotlin super call '{targetName}' has an untyped parameter");
            return TypeJson.Write(SubstituteOwnerSlots(parsed, ownerArgs));
        }).ToArray());
        var declarationReturn = TypeJson.Read(memberReturnType)
            ?? TypeJson.Read(access["dynRet"])
            ?? TypeJson.Read(access["ret"])
            ?? TypeJson.Read(access["sty"])
            ?? new TypeNode.Fqn("kotlin.Unit");
        var declaredReturn = SubstituteOwnerSlots(declarationReturn, ownerArgs);
        var declaredReturnJson = TypeJson.Write(declaredReturn);
        var forwarderTypeParams = SubstituteOwnerSlotsInDescriptors(methodTypeParams, ownerArgs);
        var methodArity = access["typeArgs"] is JsonArray typeArgs ? typeArgs.Count : 0;
        if ((forwarderTypeParams?.Count ?? 0) != methodArity)
            throw new InvalidOperationException(
                $"Kotlin super call '{targetOwner.Name}.{targetName}' has {methodArity} method arguments but " +
                $"{forwarderTypeParams?.Count ?? 0} declaration parameters");

        var key = $"{forwarderHost.Name}|super|{targetOwner.Name}|{targetName}|{methodArity}|" +
                  string.Join(";", signature.Select(TypeKey)) + "|" + TypeKey(declaredReturnJson);
        if (!accessors.TryGetValue(key, out var definition))
        {
            var name = SuperForwarderName(targetName);
            var parameters = new JsonArray(signature.Select((type, index) =>
                (JsonNode)Param("arg" + index, type?.DeepClone())).ToArray());
            var targetCall = (JsonObject)access.DeepClone();
            targetCall["recv"] = new JsonObject { ["k"] = "this" };
            targetCall["ownerType"] = TypeJson.Write(resolvedOwner);
            targetCall["sig"] = declarationSignature.DeepClone();
            targetCall["args"] = new JsonArray(parameters.OfType<JsonObject>().Select(parameter =>
                (JsonNode)new JsonObject { ["k"] = "local", ["name"] = parameter["name"]?.DeepClone() }).ToArray());
            targetCall["ret"] = declaredReturnJson.DeepClone();
            targetCall["dynRet"] = declaredReturnJson.DeepClone();
            targetCall["sty"] = declaredReturnJson.DeepClone();
            if (methodArity > 0)
                targetCall["typeArgs"] = new JsonArray(Enumerable.Range(0, methodArity)
                    .Select(index => (JsonNode)TypeJson.Write(new TypeNode.Tv("method", index))).ToArray());
            else
                targetCall.Remove("typeArgs");
            DropMemberFacts(targetCall);

            var returnsUnit = declaredReturn is TypeNode.Fqn { Name: "kotlin.Unit" or "void" };
            var body = returnsUnit
                ? new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = targetCall },
                    new JsonObject { ["k"] = "return" })
                : new JsonArray(new JsonObject { ["k"] = "return", ["value"] = targetCall });
            var forwarder = new JsonObject
            {
                ["name"] = name,
                ["generated"] = true,
                ["static"] = false,
                ["override"] = false,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "private",
                ["params"] = parameters,
                ["ret"] = declaredReturnJson.DeepClone(),
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
            if (forwarderTypeParams != null) forwarder["typeParams"] = forwarderTypeParams;
            forwarderHost.Methods.Add(forwarder);
            definition = new AccessorDefinition(name, null, (JsonArray)signature.DeepClone(), 0, methodArity);
            accessors[key] = definition;
        }

        var callResult = access["sty"]?.DeepClone()
            ?? access["dynRet"]?.DeepClone()
            ?? access["ret"]?.DeepClone()
            ?? declaredReturnJson.DeepClone();
        var replacement = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(receiverType),
            ["virtual"] = false,
            ["recv"] = receiver.DeepClone(),
            ["method"] = definition.EntryName,
            ["sig"] = definition.Signature.DeepClone(),
            ["args"] = access["args"]?.DeepClone() ?? new JsonArray(),
            ["ret"] = callResult.DeepClone(),
            ["sty"] = callResult.DeepClone(),
        };
        if (access["typeArgs"] is JsonArray originalTypeArgs)
            replacement["typeArgs"] = originalTypeArgs.DeepClone();
        Replace(access, replacement);
    }

    static JsonObject SelectMethod(JsonObject[] candidates, JsonArray signature, TypeNode[] ownerArgs)
    {
        if (candidates.Length == 0) return null;
        var exact = candidates.Where(candidate =>
        {
            var parameters = candidate["params"] as JsonArray ?? new JsonArray();
            for (var index = 0; index < parameters.Count; index++)
            {
                var declared = TypeJson.Read(parameters[index]?["type"]);
                var instantiated = ownerArgs == null ? declared : SubstituteOwnerSlots(declared, ownerArgs);
                if (TypeKey(TypeJson.Write(instantiated)) != TypeKey(signature[index])) return false;
            }
            return true;
        }).ToArray();
        if (exact.Length == 1) return exact[0];
        return candidates.Length == 1 ? candidates[0] : null;
    }

    static void RewriteBoundDelegate(JsonObject access, Host caller,
        IReadOnlyDictionary<string, Host> hosts, Dictionary<string, AccessorDefinition> accessors)
    {
        var frontendVisibility = Str(access["memberVisibility"]);
        var ownerTypeParams = access["memberOwnerTypeParams"] as JsonArray;
        var methodTypeParams = access["memberMethodTypeParams"] as JsonArray;
        var memberReturnType = access["memberReturnType"]?.DeepClone();
        access.Remove("memberVisibility");
        access.Remove("memberOwnerTypeParams");
        access.Remove("memberMethodTypeParams");
        access.Remove("memberReturnType");
        access.Remove("memberSignature");
        if (TypeJson.Read(access["ownerType"]) is not TypeNode.Fqn ownerType
            || Str(access["method"]) is not string targetName)
            return;

        hosts.TryGetValue(ownerType.Name, out var targetHost);
        if (targetHost != null && DirectPrivateAccessIsValid(caller.Name, targetHost.Name, hosts)) return;

        var signature = SignatureOf(access);
        var methodArity = access["typeArgs"] is JsonArray typeArgs ? typeArgs.Count : 0;
        JsonObject target = null;
        if (targetHost != null)
        {
            var candidates = targetHost.LookupMethods
                .Where(method => Str(method["name"]) == targetName && !Bool(method["static"])
                    && (method["params"] is JsonArray parameters ? parameters.Count : 0) == signature.Count
                    && (method["typeParams"] is JsonArray ownParams ? ownParams.Count : 0) == methodArity)
                .ToArray();
            target = SelectMethod(candidates, signature, ownerType.Args);
            if (target == null || Str(target["vis"]) is not ("private" or "protected")) return;
        }
        else if (frontendVisibility is not ("private" or "protected"))
            return;

        var returnTypeNode = TypeJson.Read(target?["ret"]) ?? TypeJson.Read(memberReturnType);
        if (returnTypeNode == null)
            returnTypeNode = TypeJson.Read(access["funcType"]) is TypeNode.Fn fn
                ? fn.Ret : new TypeNode.Fqn("kotlin.Unit", null);
        var declaredReturnType = TypeJson.Write(returnTypeNode);
        var key = $"{caller.Name}|bound|{ownerType.Name}|{targetName}|{methodArity}|" +
                  string.Join(";", signature.Select(TypeKey)) + "|" + TypeKey(declaredReturnType);
        var definition = EnsureAccessor(caller, accessors, key, targetName, 1, access["ownerType"],
            PhysicalOwnerTypeParams(targetHost, ownerTypeParams),
            PhysicalMethodTypeParams(target, methodTypeParams), declaredReturnType,
            signature, includeTarget: true,
            targetDeclarationId: Str(target?[DeclarationIdentityBinding.Key] ?? access[DeclarationIdentityBinding.Key]));
        var callOwner = AccessorCallOwner(caller, definition, ownerType.Args ?? Array.Empty<TypeNode>());

        var replacement = new JsonObject
        {
            ["k"] = "newBoundDelegate",
            ["ownerType"] = callOwner.DeepClone(),
            ["calleeOwner"] = callOwner,
            ["method"] = definition.EntryName,
            ["sig"] = definition.Signature.DeepClone(),
            ["virtual"] = false,
            ["recv"] = access["recv"]?.DeepClone(),
            ["funcType"] = access["funcType"]?.DeepClone(),
        };
        if (access["typeArgs"] is JsonArray originalTypeArgs)
            replacement["typeArgs"] = originalTypeArgs.DeepClone();
        if (access["sty"] is JsonNode sty) replacement["sty"] = sty.DeepClone();
        Replace(access, replacement);
    }

    static TypeNode SubstituteOwnerSlots(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv tv when tv.Scope == "type" && tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name,
            f.Args?.Select(arg => SubstituteOwnerSlots(arg, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteOwnerSlots(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteOwnerSlots(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstituteOwnerSlots(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteOwnerSlots(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstituteOwnerSlots(fn.Ret, args),
            fn.Params.Select(param => SubstituteOwnerSlots(param, args)).ToArray(),
            fn.Recv == null ? null : SubstituteOwnerSlots(fn.Recv, args), fn.Clr,
            fn.Ctx?.Select(param => SubstituteOwnerSlots(param, args)).ToArray()),
        _ => type,
    };

    // The facts are semantic declaration parameters. Ownership/companion projection may give a LOCAL physical
    // TypeDef a different generic frame; UnsafeAccessor matching is a CLR concern, so the physical declaration wins.
    // External targets have no local declaration and therefore retain the exact frame carried in BIR.
    static JsonArray PhysicalOwnerTypeParams(Host target, JsonArray semantic)
    {
        if (target == null) return semantic;
        var captured = target.Declaration["capturedTypeParams"] as JsonArray;
        var declared = target.Declaration["typeParams"] as JsonArray;
        if (captured == null || captured.Count == 0) return declared ?? new JsonArray();
        var physical = new JsonArray();
        foreach (var parameter in captured) physical.Add(parameter?.DeepClone());
        if (declared != null)
            foreach (var parameter in declared) physical.Add(parameter?.DeepClone());
        return physical;
    }

    // As with an owner's frame above, a local target declaration has already passed through the representation
    // lowerings that establish its emitted CLR contract. The BIR fact on the call is the earlier semantic frame and
    // may therefore still spell a rewritten constraint (CharSequence is the concrete case). Match UnsafeAccessor to
    // the physical target declaration; retain the carried frame only for an external target with no local declaration.
    static JsonArray PhysicalMethodTypeParams(JsonObject target, JsonArray semantic)
        => target?["typeParams"] as JsonArray ?? semantic;

    static JsonArray RenamedTypeParams(JsonArray source, string prefix)
    {
        if (source == null) return null;
        var result = new JsonArray();
        for (var index = 0; index < source.Count; index++)
        {
            if (source[index] is not JsonObject descriptor) result.Add(prefix + index);
            else
            {
                var copy = (JsonObject)descriptor.DeepClone();
                copy["name"] = prefix + index;
                result.Add(copy);
            }
        }
        return result;
    }

    static JsonNode OpenOwner(JsonNode ownerNode, int ownerCount)
    {
        if (TypeJson.Read(ownerNode) is not TypeNode.Fqn owner) return ownerNode?.DeepClone();
        return TypeJson.Write(new TypeNode.Fqn(owner.Name, ownerCount == 0 ? null : Enumerable.Range(0, ownerCount)
            .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray()));
    }

    static JsonNode ConstructedHolder(string name, IReadOnlyList<TypeNode> args) =>
        TypeJson.Write(new TypeNode.Fqn(name, args.Count == 0 ? null : args.ToArray()));

    static AccessorDefinition EnsureAccessor(Host caller,
        Dictionary<string, AccessorDefinition> accessors, string key, string targetName, int kind,
        JsonNode ownerNode, JsonArray ownerTypeParams, JsonArray methodTypeParams, JsonNode returnType,
        JsonArray signature, bool includeTarget, string targetDeclarationId = null)
    {
        if (accessors.TryGetValue(key, out var existing)) return existing;
        if (TypeJson.Read(ownerNode) is not TypeNode.Fqn owner)
            throw new InvalidOperationException("UnsafeAccessor target has no owner type");
        var ownerArgs = owner.Args ?? Array.Empty<TypeNode>();
        var ownerCount = ownerTypeParams?.Count ?? 0;
        if (ownerCount != ownerArgs.Length)
            throw new InvalidOperationException(
                $"UnsafeAccessor owner generic frame does not match its construction: {owner.Name} has " +
                $"{ownerCount} declaration parameters and {ownerArgs.Length} constructed arguments ({key})");

        var declarationParams = new JsonArray();
        if (includeTarget) declarationParams.Add(Param("target", OpenOwner(ownerNode, ownerCount)));
        for (var index = 0; index < signature.Count; index++)
            declarationParams.Add(Param("arg" + index, signature[index]?.DeepClone()));
        var methodParams = RenamedTypeParams(methodTypeParams, "__method");
        var accessorName = AccessorName(targetName);

        if (ownerCount == 0)
        {
            var accessor = AccessorDeclaration(accessorName, returnType, declarationParams, kind, targetName);
            if (targetDeclarationId != null) accessor["unsafeTargetDeclarationId"] = targetDeclarationId;
            if (methodParams != null) accessor["typeParams"] = methodParams.DeepClone();
            caller.Methods.Add(accessor);
            var direct = new AccessorDefinition(accessorName, null,
                new JsonArray(declarationParams.OfType<JsonObject>()
                    .Select(parameter => parameter["type"]?.DeepClone()).ToArray()), 0,
                methodParams?.Count ?? 0);
            accessors[key] = direct;
            return direct;
        }

        // Owner generic parameters must remain TYPE parameters with their original positions. Putting them on the
        // accessor method is rejected by .NET 9+'s strict UnsafeAccessor signature matcher. A compiler-reserved
        // top-level holder supplies that exact type frame; its internal wrapper is the only same-assembly entry point,
        // while the attributed method itself remains private static extern.
        var holderName = "dotkt$unsafe$holder$" + System.Threading.Interlocked.Increment(ref _counter);
        var holderTypeParams = RenamedTypeParams(ownerTypeParams, "__owner");
        var externMethod = AccessorDeclaration(accessorName, returnType, declarationParams, kind, targetName);
        if (targetDeclarationId != null) externMethod["unsafeTargetDeclarationId"] = targetDeclarationId;
        if (methodParams != null) externMethod["typeParams"] = methodParams.DeepClone();
        var entryName = accessorName + "$invoke";
        var wrapper = WrapperDeclaration(entryName, accessorName, holderName, holderTypeParams.Count,
            methodParams?.Count ?? 0, returnType, declarationParams, methodParams);
        EnsureArray(caller.Root, "types").Add(new JsonObject
        {
            ["name"] = holderName,
            ["kind"] = "class",
            ["generated"] = true,
            ["vis"] = "internal",
            ["typeParams"] = holderTypeParams,
            ["base"] = null,
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["ctors"] = new JsonArray(),
            ["methods"] = new JsonArray(externMethod, wrapper),
            ["properties"] = new JsonArray(),
            ["attrs"] = new JsonArray(),
        });
        var definition = new AccessorDefinition(entryName, holderName,
            new JsonArray(declarationParams.OfType<JsonObject>()
                .Select(parameter => parameter["type"]?.DeepClone()).ToArray()), ownerCount,
            methodParams?.Count ?? 0);
        accessors[key] = definition;
        return definition;
    }

    static JsonObject WrapperDeclaration(string name, string accessorName, string holderName, int ownerCount,
        int methodCount, JsonNode returnType, JsonArray parameters, JsonArray methodTypeParams)
    {
        var holderSelf = TypeJson.Write(new TypeNode.Fqn(holderName, Enumerable.Range(0, ownerCount)
            .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray()));
        var args = new JsonArray(parameters.OfType<JsonObject>().Select(parameter => (JsonNode)new JsonObject
        {
            ["k"] = "local",
            ["name"] = parameter["name"]?.DeepClone(),
        }).ToArray());
        var call = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = holderSelf.DeepClone(),
            ["ownerType"] = holderSelf,
            ["method"] = accessorName,
            ["sig"] = new JsonArray(parameters.OfType<JsonObject>()
                .Select(parameter => parameter["type"]?.DeepClone()).ToArray()),
            ["args"] = args,
            ["ret"] = returnType.DeepClone(),
        };
        if (methodCount > 0)
            call["typeArgs"] = new JsonArray(Enumerable.Range(0, methodCount)
                .Select(index => (JsonNode)TypeJson.Write(new TypeNode.Tv("method", index))).ToArray());
        var returnsUnit = TypeJson.Read(returnType) is TypeNode.Fqn { Name: "kotlin.Unit" or "void" };
        var body = returnsUnit
            ? new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = call }, new JsonObject { ["k"] = "return" })
            : new JsonArray(new JsonObject { ["k"] = "return", ["value"] = call });
        var wrapper = new JsonObject
        {
            ["name"] = name,
            ["generated"] = true,
            ["static"] = true,
            ["override"] = false,
            ["virtual"] = false,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "internal",
            ["params"] = parameters.DeepClone(),
            ["ret"] = returnType.DeepClone(),
            ["body"] = body,
            ["attrs"] = new JsonArray(),
        };
        if (methodTypeParams != null) wrapper["typeParams"] = methodTypeParams.DeepClone();
        return wrapper;
    }

    static JsonNode AccessorCallOwner(Host caller, AccessorDefinition definition, TypeNode[] ownerArgs) =>
        definition.HolderName == null ? SelfType(caller) : ConstructedHolder(definition.HolderName, ownerArgs);

    static JsonArray SignatureOf(JsonObject access)
    {
        if (access["sig"] is JsonArray signature) return signature;
        if (access["argTypes"] is JsonArray argTypes) return argTypes;
        return new JsonArray();
    }

    static void RewriteConstructor(JsonObject access, Host caller, IReadOnlyDictionary<string, Host> hosts,
        Dictionary<string, AccessorDefinition> accessors)
    {
        var frontendVisibility = Str(access["memberVisibility"]);
        var ownerTypeParams = access["memberOwnerTypeParams"] as JsonArray;
        var memberSignature = access["memberSignature"] as JsonArray;
        access.Remove("memberVisibility");
        access.Remove("memberOwnerTypeParams");
        access.Remove("memberMethodTypeParams");
        if (TypeJson.Read(access["type"]) is not TypeNode.Fqn ownerType) return;
        hosts.TryGetValue(ownerType.Name, out var targetHost);
        if (targetHost != null && DirectPrivateAccessIsValid(caller.Name, targetHost.Name, hosts)) return;
        var signature = memberSignature ?? access["argTypes"] as JsonArray ?? new JsonArray();
        JsonObject target = null;
        if (targetHost != null)
        {
            var candidates = targetHost.LookupConstructors
                .Where(ctor => Str(ctor["vis"]) is "private" or "protected"
                    && (ctor["params"] is JsonArray parameters ? parameters.Count : 0) == signature.Count)
                .ToArray();
            target = SelectMethod(candidates, signature, ownerType.Args);
            if (target == null) return;
        }
        else if (frontendVisibility is not ("private" or "protected"))
            return;

        var physicalOwnerTypeParams = PhysicalOwnerTypeParams(targetHost, ownerTypeParams);
        var key = $"{caller.Name}|ctor|{ownerType.Name}|" + string.Join(";", signature.Select(TypeKey));
        var definition = EnsureAccessor(caller, accessors, key, null, 0, access["type"], physicalOwnerTypeParams,
            null, OpenOwner(access["type"], physicalOwnerTypeParams?.Count ?? 0), signature, includeTarget: false);
        var callOwner = AccessorCallOwner(caller, definition, ownerType.Args ?? Array.Empty<TypeNode>());
        var replacement = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = callOwner.DeepClone(),
            ["ownerType"] = callOwner,
            ["method"] = definition.EntryName,
            ["sig"] = definition.Signature.DeepClone(),
            ["args"] = access["args"]?.DeepClone() ?? new JsonArray(),
            ["ret"] = access["type"]?.DeepClone(),
        };
        if (access["sty"] is JsonNode sty) replacement["sty"] = sty.DeepClone();
        Replace(access, replacement);
    }

    static void RewriteField(JsonObject access, string kind, Host caller,
        IReadOnlyDictionary<string, Host> hosts, Dictionary<string, AccessorDefinition> accessors)
    {
        var frontendVisibility = Str(access["memberVisibility"]);
        var frontendFieldType = access["memberType"]?.DeepClone();
        var ownerTypeParams = access["memberOwnerTypeParams"] as JsonArray;
        access.Remove("memberVisibility");
        access.Remove("memberType");
        access.Remove("memberOwnerTypeParams");
        access.Remove("memberMethodTypeParams");
        var ownerNode = access["ownerType"];
        if (TypeJson.Read(ownerNode) is not TypeNode.Fqn ownerType || Str(access["name"]) is not string targetName)
            return;
        // kotc names the field's Kotlin DECLARATION owner; whether that owner is constructed at this use site is a
        // physical fact this layer owns. A field access can therefore carry the bare generic declaration (`H`) while
        // its receiver's static type carries the construction (`H<Int>`) — a delegated property's `$delegate` read is
        // the shape that does. Close the owner frame from the receiver before the accessor is minted, so the holder
        // is constructed exactly as its declaration parameters require.
        if (ownerType.Args is not { Length: > 0 } && access["recv"] is JsonNode fieldReceiver
            && NodeType.Of(fieldReceiver) is TypeNode.Fqn constructedReceiver
            && constructedReceiver.Name == ownerType.Name && constructedReceiver.Args is { Length: > 0 })
        {
            ownerType = constructedReceiver;
            ownerNode = TypeJson.Write(ownerType);
        }

        hosts.TryGetValue(ownerType.Name, out var targetHost);
        if (targetHost != null && DirectPrivateAccessIsValid(caller.Name, targetHost.Name, hosts)) return;

        JsonObject target = null;
        if (targetHost != null)
        {
            target = targetHost.LookupFields.SingleOrDefault(field => Str(field["name"]) == targetName);
            if (target == null || Str(target["vis"]) is not ("private" or "protected")) return;
        }
        else if (frontendVisibility is not ("private" or "protected"))
            return;

        var fieldTypeNode = target?["type"]?.DeepClone() ?? frontendFieldType
            ?? access["ret"]?.DeepClone() ?? access["sty"]?.DeepClone();
        if (TypeJson.Read(fieldTypeNode) is not TypeNode fieldType) return;
        var declaredFieldTypeJson = TypeJson.Write(fieldType);
        var actualFieldType = ownerType.Args is { Length: > 0 }
            ? SubstituteOwnerSlots(fieldType, ownerType.Args) : fieldType;
        var fieldTypeJson = TypeJson.Write(actualFieldType);
        var declaredByRefType = TypeJson.Write(new TypeNode.ByRef(fieldType));
        var callByRefType = TypeJson.Write(new TypeNode.ByRef(actualFieldType));
        var targetStatic = kind is "staticField" or "staticFieldSet"
            || (target != null && Bool(target["static"]));

        var key = $"{caller.Name}|field|{ownerType.Name}|{targetName}|{targetStatic}|{TypeKey(declaredFieldTypeJson)}";
        var definition = EnsureAccessor(caller, accessors, key, targetName, targetStatic ? 4 : 3, ownerNode,
            PhysicalOwnerTypeParams(targetHost, ownerTypeParams), null, declaredByRefType,
            new JsonArray(), includeTarget: true);
        var callOwner = AccessorCallOwner(caller, definition, ownerType.Args ?? Array.Empty<TypeNode>());

        // A read and a write each need their OWN pointer call: a JsonNode has one parent, so handing the same
        // instance to both a byrefLoad and a byrefStore throws rather than emitting the second one.
        JsonObject PointerCall() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = callOwner.DeepClone(),
            ["ownerType"] = callOwner.DeepClone(),
            ["method"] = definition.EntryName,
            ["sig"] = definition.Signature.DeepClone(),
            ["args"] = new JsonArray(targetStatic
                ? new JsonObject { ["k"] = "default", ["type"] = ownerNode.DeepClone() }
                : access["recv"]?.DeepClone()),
            ["ret"] = callByRefType.DeepClone(),
        };
        var read = new JsonObject
        {
            ["k"] = "byrefLoad",
            ["ptr"] = PointerCall(),
            ["elem"] = fieldTypeJson.DeepClone(),
            ["sty"] = fieldTypeJson.DeepClone(),
        };
        if (Bool(access["volatile"]) || Bool(target?["volatile"])) read["volatile"] = true;

        if (kind == "lateinitGet")
        {
            var replacement = new JsonObject
            {
                ["k"] = "lateinitGet",
                ["name"] = targetName,
                ["value"] = read,
                ["sty"] = fieldTypeJson.DeepClone(),
            };
            if (access["lateinitSourceName"] is JsonNode sourceName)
                replacement["lateinitSourceName"] = sourceName.DeepClone();
            Replace(access, replacement);
            return;
        }
        if (kind is "field" or "staticField")
        {
            Replace(access, read);
            return;
        }

        var store = new JsonObject
        {
            ["k"] = "byrefStore",
            ["ptr"] = PointerCall(),
            ["elem"] = fieldTypeJson.DeepClone(),
            ["value"] = access["value"]?.DeepClone(),
        };
        if (Bool(access["volatile"]) || Bool(target?["volatile"])) store["volatile"] = true;
        Replace(access, kind == "setField"
            ? new JsonObject { ["k"] = "exprStmt", ["expr"] = store }
            : store);
    }

    static JsonObject AccessorDeclaration(string name, JsonNode ret, JsonArray parameters, int kind, string targetName)
    {
        var attribute = new JsonObject
        {
            ["attr"] = TypeJson.Fqn(AttributeFqn),
            ["attrExternal"] = true,
            ["attrAssembly"] = "System.Runtime",
            ["argTypes"] = new JsonArray(TypeJson.Fqn(KindFqn)),
            ["args"] = new JsonArray(new JsonObject
            {
                ["value"] = kind,
                ["type"] = TypeJson.Fqn(KindFqn),
            }),
        };
        if (targetName != null)
            attribute["namedArgs"] = new JsonArray(new JsonObject
            {
                ["kind"] = "property",
                ["name"] = "Name",
                ["type"] = TypeJson.Fqn("System.String"),
                ["value"] = new JsonObject
                {
                    ["value"] = targetName,
                    ["type"] = TypeJson.Fqn("System.String"),
                },
            });
        return new JsonObject
        {
            ["name"] = name,
            ["generated"] = true,
            ["static"] = true,
            ["extern"] = true,
            ["override"] = false,
            ["virtual"] = false,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "private",
            ["params"] = parameters,
            ["ret"] = ret?.DeepClone() ?? TypeJson.Fqn("kotlin.Unit"),
            ["attrs"] = new JsonArray(attribute),
        };
    }

    static JsonObject Param(string name, JsonNode type) => new()
    {
        ["name"] = name,
        ["type"] = type ?? TypeJson.Fqn("kotlin.Any"),
    };

    static JsonNode SelfType(Host host)
    {
        var captured = host.Declaration["capturedTypeParams"] is JsonArray capturedParameters
            ? capturedParameters.Count : 0;
        var declared = host.Declaration["typeParams"] is JsonArray declaredParameters
            ? declaredParameters.Count : 0;
        var count = captured + declared;
        return TypeJson.Write(new TypeNode.Fqn(host.Name, count == 0 ? null : Enumerable.Range(0, count)
            .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray()));
    }

    static Host EnclosingHost(Host caller, string name, IReadOnlyDictionary<string, Host> hosts)
    {
        var current = caller;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(current.Name))
        {
            if (current.Name == name) return current;
            current = current.NestedIn != null && hosts.TryGetValue(current.NestedIn, out var parent) ? parent : null;
        }
        return null;
    }

    static TypeNode.Fqn ResolveConstructedAncestor(Host start, string target,
        IReadOnlyDictionary<string, Host> hosts)
    {
        if (TypeJson.Read(SelfType(start)) is not TypeNode.Fqn self) return null;
        var queue = new Queue<TypeNode.Fqn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(self);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(TypeJson.Write(current).ToJsonString())) continue;
            if (current.Name == target) return current;
            if (!hosts.TryGetValue(current.Name, out var host)) continue;
            var args = current.Args ?? Array.Empty<TypeNode>();
            void Enqueue(JsonNode edge)
            {
                if (TypeJson.Read(edge) is TypeNode.Fqn ancestor)
                    queue.Enqueue((TypeNode.Fqn)SubstituteOwnerSlots(ancestor, args));
            }
            Enqueue(host.Declaration["base"]);
            if (host.Declaration["interfaces"] is JsonArray interfaces)
                foreach (var edge in interfaces) Enqueue(edge);
        }
        return null;
    }

    static JsonArray SubstituteOwnerSlotsInDescriptors(JsonArray descriptors, TypeNode[] ownerArgs)
    {
        if (descriptors == null) return null;
        var result = (JsonArray)descriptors.DeepClone();
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null) continue;
                    if (TypeJson.IsType(value))
                        obj[key] = TypeJson.Write(SubstituteOwnerSlots(TypeJson.Read(value), ownerArgs));
                    else
                        Walk(value);
                }
            }
            else if (node is JsonArray array)
                for (var index = 0; index < array.Count; index++)
                {
                    var value = array[index];
                    if (value == null) continue;
                    if (TypeJson.IsType(value))
                        array[index] = TypeJson.Write(SubstituteOwnerSlots(TypeJson.Read(value), ownerArgs));
                    else
                        Walk(value);
                }
        }
        Walk(result);
        return result;
    }

    static void DropMemberFacts(JsonObject obj)
    {
        obj.Remove("memberVisibility");
        obj.Remove("memberType");
        obj.Remove("memberOwnerTypeParams");
        obj.Remove("memberMethodTypeParams");
        obj.Remove("memberReturnType");
        obj.Remove("memberSignature");
    }

    static bool DirectPrivateAccessIsValid(string caller, string target, IReadOnlyDictionary<string, Host> hosts)
    {
        if (caller == target) return true;
        var current = caller;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(current) && hosts.TryGetValue(current, out var host))
        {
            if (host.NestedIn == target) return true;
            current = host.NestedIn;
        }
        return false;
    }

    static string AccessorName(string target)
    {
        var safe = new string((target ?? "member").Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"dotkt$unsafe${System.Threading.Interlocked.Increment(ref _counter)}${safe}";
    }

    static string SuperForwarderName(string target)
    {
        var safe = new string((target ?? "member").Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"dotkt$super${System.Threading.Interlocked.Increment(ref _counter)}${safe}";
    }

    static string TypeKey(JsonNode node) => node?.ToJsonString() ?? "null";

    static void Replace(JsonObject target, JsonObject replacement)
    {
        target.Clear();
        foreach (var pair in replacement.ToList())
        {
            replacement.Remove(pair.Key);
            target[pair.Key] = pair.Value;
        }
    }
}
