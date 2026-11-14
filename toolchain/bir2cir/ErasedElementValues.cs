using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Open Kotlin arrays and iterators preserve their actual elements, not the caller's concrete T.
// Their elements are erased values until an actual consuming slot asks for T.
// Project only body-local value storage here; signatures, allocation witnesses, reified
// operations and native locations retain their independently owned representations.
static partial class ErasedElementValues
{
    sealed record Slot(JsonObject Declaration, string Name, TypeNode Type);
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");
    const string SourceVariableKey = "_erasedValueVariable";

    internal static TypeNode.Tv RetainedVariable(JsonObject value) =>
        TypeJson.Read(value[SourceVariableKey]) as TypeNode.Tv;

    internal static void ClearRetainedVariables(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove(SourceVariableKey);
            foreach (var child in obj.Select(pair => pair.Value)) ClearRetainedVariables(child);
        }
        else if (node is JsonArray array)
            foreach (var child in array) ClearRetainedVariables(child);
    }

    internal static void ApplyType(JsonObject type, ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, JsonObject> definitions,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy)
    {
        bool IsInterface(TypeNode value) => value is TypeNode.Fqn named
            && (definitions.TryGetValue(named.Name, out var declaration) ? Text(declaration["kind"]) == "interface"
                : references.IsInterfaceType(named));
        foreach (var method in (type["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Concat((type["ctors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()))
            Project(method, type, IsInterface, frames, isValue, policy);
    }

    internal static void ApplySuspendBody(JsonObject lambda, ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, JsonObject> definitions,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy)
    {
        bool IsInterface(TypeNode value) => value is TypeNode.Fqn named
            && (definitions.TryGetValue(named.Name, out var declaration) ? Text(declaration["kind"]) == "interface"
                : references.IsInterfaceType(named));
        Project(lambda, lambda, IsInterface, frames, isValue, policy);
    }

    static TypeNode.Tv Variable(TypeNode type) => type switch {
        TypeNode.Tv variable => variable,
        TypeNode.Projection projection => Variable(projection.Of),
        _ => null,
    };

    // Late existential capture alignment consumes erased field/parameter origins;
    // module array/iterator reads have already been projected on the typed graph.
    static void Project(JsonObject method, JsonObject owner, Func<TypeNode, bool> isInterface,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy)
    {
        if (method["k"] == null && method["body"] is JsonArray nativeBody)
        {
            var constructor = method["ret"] == null;
            var prologue = constructor ? method["preStmts"] as JsonArray ?? new JsonArray() : nativeBody;
            PhysicalValueCoercion.AllocateNativeLocals((method["preStmts"] as JsonArray ?? new JsonArray())
                .Concat(method["thisArgs"] as JsonArray ?? method["baseArgs"] as JsonArray ?? new JsonArray())
                .Concat(nativeBody), (method["params"] as JsonArray ?? new JsonArray()).OfType<JsonObject>(), prologue,
                genericParametersOnly: true);
            if (constructor && method["preStmts"] == null && prologue.Count != 0) method["preStmts"] = prologue;
        }
        var executable = (method["preStmts"] as JsonArray ?? new JsonArray())
            .Concat(method["thisArgs"] as JsonArray ?? method["baseArgs"] as JsonArray ?? new JsonArray())
            .Concat(method["body"] as JsonArray ?? new JsonArray()).ToArray();
        if (executable.Length == 0) return;
        var reads = new Dictionary<JsonObject, TypeNode.Tv>();
        var captures = new List<JsonObject>();
        var uses = new Dictionary<JsonObject, Slot>();
        var addressed = new HashSet<Slot>();
        var slots = new List<Slot>();
        var valueResults = new HashSet<JsonObject>();
        var bindings = new Dictionary<string, Slot>(StringComparer.Ordinal);
        var parameterOrigins = new Dictionary<Slot, TypeNode.Tv>();
        var mapping = new NullableRepresentationTypes(owner == null ? null : KotlinSupertypesRecord.ReadNullableFrame(owner),
            NullableRepresentationTypes.DeclarationMethodFrame(method), frames, isValue, policy: policy);
        TypeNode.Tv SourceVariable(JsonObject declaration) => Text(declaration["kotlinType"]) is string source
            && TypeNode.Parse(source) is TypeNode.Tv variable ? Variable(mapping.Slot(variable)) : null;
        var erasedFields = (owner?["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(field => IsObject(TypeJson.Read(field["type"])) && SourceVariable(field) != null)
            .ToDictionary(field => Text(field["name"]), SourceVariable, StringComparer.Ordinal);
        foreach (var parameter in (method["params"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Concat(Text(method["k"]) == "newSuspendLambda"
                ? (method["captures"] as JsonArray ?? new JsonArray()).OfType<JsonObject>() : Enumerable.Empty<JsonObject>()))
            if (Text(parameter["name"]) is string name)
            {
                var slot = new Slot(parameter, name, TypeJson.Read(parameter["type"]));
                bindings[name] = slot;
                if (slot.Type is not TypeNode.Tv && SourceVariable(parameter) is { } variable)
                    parameterOrigins[slot] = variable;
            }

        void Bind(JsonNode node, Dictionary<string, Slot> scope, bool location = false)
        {
            if (node is JsonArray array)
            {
                var block = new Dictionary<string, Slot>(scope, StringComparer.Ordinal);
                foreach (var child in array) Bind(child, block);
                return;
            }
            if (node is not JsonObject obj) return;
            if (obj["k"] == null && obj["params"] is JsonArray && obj["body"] is JsonArray) return;
            var kind = Text(obj["k"]);
            if (kind is "newClosure" or "newSam" or "newSuspendLambda") captures.Add(obj);
            if (kind is "local" or "setLocal" && Text(obj["name"]) is string used
                && scope.TryGetValue(used, out var slot))
            {
                uses[obj] = slot;
                if (location) addressed.Add(slot);
            }
            if (!location && kind == "field" && Text(obj["recv"]?["k"]) == "this"
                && TypeJson.OwnerName(obj["ownerType"]) == Text(owner?["name"])
                && Text(obj["name"]) is string fieldName && erasedFields.TryGetValue(fieldName, out var fieldVariable))
            {
                reads[obj] = fieldVariable;
            }
            if (!location && kind is "cond" or "valueBlock") valueResults.Add(obj);
            if (kind == "valueBlock")
            {
                // The result reads the same lexical bindings established by
                // both ordered statement lists, without exporting them outside.
                var inner = new Dictionary<string, Slot>(scope, StringComparer.Ordinal);
                foreach (var statement in (obj["stmts"] as JsonArray ?? new JsonArray())
                    .Concat(obj["body"] as JsonArray ?? new JsonArray())) Bind(statement, inner);
                Bind(obj["result"], inner, location);
                return;
            }
            if (kind == "forArray" && Text(obj["var"]) is string loopName)
            {
                Bind(obj["array"], scope);
                var loop = new Slot(obj, loopName, TypeJson.Read(obj["elem"]));
                var inner = new Dictionary<string, Slot>(scope, StringComparer.Ordinal) { [loopName] = loop };
                Bind(obj["body"], inner);
                return;
            }
            foreach (var (key, child) in obj)
            {
                if (key is "attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey) continue;
                // A materialized lambda body owns its generic frame. Its capture
                // expressions still execute in the enclosing declaration.
                if (key == "body" && kind is "newClosure" or "newSam" or "newSuspendLambda") continue;
                if (key == "args" && child is JsonArray arguments)
                {
                    var parameters = obj["memberRef"]?["parameterTypes"] as JsonArray
                        ?? obj["resolvedMemberParams"] as JsonArray ?? obj["sig"] as JsonArray
                        ?? obj["shapeTypes"] as JsonArray ?? obj["argTypes"] as JsonArray;
                    for (var i = 0; i < arguments.Count; i++)
                        Bind(arguments[i], scope, parameters != null && i < parameters.Count
                            && SignatureValueTypes.Of(TypeJson.Read(parameters[i])) is TypeNode.ByRef);
                }
                else Bind(child, scope, kind == "byrefOf" && key == "inner"
                    || location && kind == "valueBlock" && key == "result");
            }
            if (kind == "var" && Text(obj["name"]) is string declared)
            {
                var local = new Slot(obj, declared, TypeJson.Read(obj["type"]));
                slots.Add(local);
                scope[declared] = local;
            }
        }
        foreach (var statement in executable) Bind(statement, bindings);
        if (reads.Count == 0 && parameterOrigins.Count == 0) return;

        var origins = new Dictionary<Slot, TypeNode.Tv>();
        foreach (var (slot, variable) in parameterOrigins) origins[slot] = variable;
        TypeNode.Tv Origin(JsonNode node)
        {
            if (node is not JsonObject obj) return null;
            if (reads.TryGetValue(obj, out var read)) return read;
            if (Text(obj["k"]) == "local" && uses.TryGetValue(obj, out var slot))
                return origins.GetValueOrDefault(slot);
            if (Text(obj["k"]) == "cast" && NullableBool(obj["reifiedTypeOperand"]) == false
                && TypeJson.Read(obj["type"]) is TypeNode.Tv target && Origin(obj["e"]) is { } source
                && target == source) return target;
            if (Text(obj["k"]) == "valueBlock") return Origin(obj["result"]);
            if (Text(obj["k"]) == "cond") return Origin(obj["then"]) ?? Origin(obj["else"]);
            return null;
        }
        bool Record(Slot slot, JsonNode value)
        {
            if (addressed.Contains(slot) || origins.ContainsKey(slot) || Origin(value) is not { } source) return false;
            if (slot.Type is TypeNode.Tv variable) origins[slot] = variable;
            else if (IsObject(slot.Type)) origins[slot] = source;
            else return false;
            return true;
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var slot in slots) changed |= Record(slot, slot.Declaration["init"]);
            foreach (var (use, slot) in uses)
                if (Text(use["k"]) == "setLocal") changed |= Record(slot, use["value"]);
        } while (changed);

        var casts = new Dictionary<JsonObject, TypeNode>();
        var constrained = new HashSet<JsonObject>();
        var erasedResults = new HashSet<JsonObject>();
        void Plan(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array) Plan(child); return; }
            if (node is not JsonObject obj) return;
            if (obj["k"] == null && obj["params"] is JsonArray && obj["body"] is JsonArray) return;
            if (Text(obj["k"]) == "cast" && NullableBool(obj["reifiedTypeOperand"]) == false
                && TypeJson.Read(obj["type"]) is TypeNode.Tv target && Origin(obj["e"]) == target)
                casts[obj] = Bound(target, new HashSet<TypeNode.Tv>());
            if (Text(obj["k"]) == "constrainedCall" && Origin(obj["recv"]) != null)
                constrained.Add(obj);
            if (valueResults.Contains(obj) && Origin(obj) != null
                && (TypeJson.Read(obj["type"]) is TypeNode.Tv || TypeJson.Read(obj["sty"]) is TypeNode.Tv))
                erasedResults.Add(obj);
            foreach (var (key, child) in obj)
                if (key is not ("attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey)
                    && !(key == "body" && Text(obj["k"]) is "newClosure" or "newSam" or "newSuspendLambda")) Plan(child);
        }

        TypeNode Bound(TypeNode.Tv variable, HashSet<TypeNode.Tv> visited)
            => ErasedBound(variable, method, owner, isInterface, frames, isValue, policy, visited);
        foreach (var statement in executable) Plan(statement);
        foreach (var (read, _) in reads)
        {
            read["sty"] = TypeJson.Write(ObjectType);
        }
        foreach (var result in erasedResults)
        {
            if (TypeJson.Read(result["type"]) is TypeNode.Tv) result["type"] = TypeJson.Write(ObjectType);
            if (result["sty"] != null) result["sty"] = TypeJson.Write(ObjectType);
        }
        foreach (var slot in origins.Keys)
            if (slot.Type is TypeNode.Tv && Text(slot.Declaration["k"]) == "var")
                slot.Declaration["type"] = TypeJson.Write(ObjectType);
        foreach (var (use, slot) in uses)
            if (Text(use["k"]) == "local" && origins.ContainsKey(slot))
                use["sty"] = TypeJson.Write(ObjectType);
        foreach (var closure in captures)
            if (closure[Text(closure["k"]) == "newSuspendLambda" ? "capValues" : "captures"] is JsonArray values)
                for (var index = 0; index < values.Count; index++)
                    if (values[index] is JsonObject value && Origin(value) != null)
                    {
                        // A capture is physical storage, not a request for the caller's closed CLR T.
                        // An ordinary Object boundary carries this fact through intervening rewrites.
                        values[index] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(ObjectType),
                            ["e"] = value.DeepClone() };
                    }
        foreach (var (cast, bound) in casts)
        {
            // A raw non-reified generic guard admits null. The typed consumer,
            // if any, still owns its actual physical extraction and null check.
            var target = IsObject(bound) ? ObjectType : new TypeNode.Nullable(bound);
            cast["type"] = TypeJson.Write(target);
            if (cast["sty"] != null) cast["sty"] = TypeJson.Write(target);
        }
        foreach (var call in constrained)
        {
            var selected = call["iface"]?.DeepClone()
                ?? throw new InvalidOperationException("Erased array receiver lost its selected constraint owner");
            call["k"] = "callInstance";
            call["ownerType"] = selected;
            call["virtual"] = true;
            call["recv"] = new JsonObject { ["k"] = "cast", ["type"] = selected.DeepClone(), ["e"] = call["recv"].DeepClone() };
            call.Remove("iface");
            call.Remove("recvType");
        }
    }

    // Shared by erased reads and generic query entry slots. A non-reified parameter promises its declared
    // upper bound, not the caller's closed CLR instantiation. Representation-frame metadata owns that bound.
    internal static TypeNode ErasedBound(TypeNode.Tv variable, JsonObject method, JsonObject owner,
        Func<TypeNode, bool> isInterface, IReadOnlyDictionary<string, NullableRepresentationFrame> frames = null,
        ValueTypeOracle isValue = null, GenericRepresentationPolicy policy = null, HashSet<TypeNode.Tv> visited = null)
        => ErasedBound(variable, MethodBounds(method), OwnerBounds(owner), isInterface, frames, isValue, policy, visited);

    // Source and physical readers supply the same declaration-owned bounds. The
    // choice of representative bound and its frame mapping lives only here.
    internal sealed record BoundDeclaration(NullableRepresentationFrame Frame, IReadOnlyList<TypeNode[]> Constraints,
        IReadOnlyDictionary<int, TypeNode[]> SavedBounds);

    internal static BoundDeclaration MethodBounds(JsonObject method) => new(
        method == null ? null : NullableRepresentationTypes.DeclarationMethodFrame(method),
        DeclarationConstraints((method?["typeParams"] as JsonArray ?? new JsonArray())),
        Text(method?[NullableGenericErasure.MethodTypeParameterBoundsPre]) is string saved
            ? SourceBoundsSnapshot.ReadFrozenText(saved).Bounds : null);

    internal static BoundDeclaration OwnerBounds(JsonObject owner) => new(
        owner == null ? null : KotlinSupertypesRecord.ReadNullableFrame(owner),
        DeclarationConstraints(TypeParameterFrame.Declarations(owner)),
        owner == null ? null : KotlinSupertypesRecord.ReadBounds(owner));

    static TypeNode[][] DeclarationConstraints(IEnumerable<JsonNode> parameters) => parameters.Select(parameter =>
        parameter is JsonObject declaration && declaration["constraints"] is JsonArray constraints
            ? constraints.Select(TypeJson.Read).Where(type => type != null).ToArray()
            : Array.Empty<TypeNode>()).ToArray();

    internal static TypeNode ErasedBound(TypeNode.Tv variable, BoundDeclaration method, BoundDeclaration owner,
        Func<TypeNode, bool> isInterface, IReadOnlyDictionary<string, NullableRepresentationFrame> frames = null,
        ValueTypeOracle isValue = null, GenericRepresentationPolicy policy = null, HashSet<TypeNode.Tv> visited = null)
    {
        visited ??= new HashSet<TypeNode.Tv>();
        if (!visited.Add(variable)) throw new InvalidOperationException("Cyclic erased element bound");
        var declaration = variable.Scope == "method" ? method : owner;
        var frame = declaration?.Frame;
        var index = frame?.PhysicalSlot(variable.I).SourceIndex ?? variable.I;
        var position = frame?.SourcePosition(index) ?? variable.I;
        var source = declaration?.SavedBounds?.GetValueOrDefault(index);
        var bounds = source ?? (position < (declaration?.Constraints.Count ?? 0)
            ? declaration.Constraints[position] : Array.Empty<TypeNode>());
        if (source != null)
        {
            var mapping = new NullableRepresentationTypes(
                owner?.Frame, method?.Frame,
                frames ?? new Dictionary<string, NullableRepresentationFrame>(), isValue ?? (_ => false), policy: policy);
            bounds = bounds.Select(mapping.Slot).ToArray();
        }
        var bound = bounds.FirstOrDefault(type => !isInterface(type)) ?? bounds.FirstOrDefault() ?? ObjectType;
        return bound is TypeNode.Tv parent
            ? ErasedBound(parent, method, owner, isInterface, frames, isValue, policy, visited) : bound;
    }

    static bool IsObject(TypeNode type) => type is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any" }
        || type is TypeNode.Nullable nullable && IsObject(nullable.Of);

    internal static void SelfTest()
    {
        SourceScalarSelfTest();
        SourceElementSelfTest();
    }

    static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static bool? NullableBool(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;
}
