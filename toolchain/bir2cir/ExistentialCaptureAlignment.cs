using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A compiler-generated capture class is physical CLR storage: each capture value, constructor parameter, backing field,
// and field read must agree on one exact representation. FBoundStarProjectionErasure can turn a captured successful
// smart-cast value into a non-generic existential carrier after closure/SAM synthesis copied the Kotlin constructed
// type into a generated class, or before suspend-lambda lowering copies its capture declaration into a state machine.
// Leaving that earlier type on the field makes newobj unverifiable (`G$star` on the stack, `G<T>` in the descriptor).
//
// Align only a carrier whose trusted local/reference metadata names the capture field's original generic classifier.
// The generated declaration and its construction node are the complete authority; no function names, class
// layout guesses, or old artifact spellings participate. The ordinary ExistentialReceiverBinding pass subsequently
// binds calls through the retyped field to the exact carrier slot and preserves their semantic result with an explicit
// projection. Ordinary erased generic values use the same storage rule: copying a source T into a generated
// field must not recover the caller's closed CLR T after the value was deliberately boxed/erased.
static class ExistentialCaptureAlignment
{
    sealed record CaptureUse(JsonObject Construction, int Position, TypeNode InitialType)
    {
        public JsonObject Expression => Construction["captures"]?[Position] as JsonObject;
    }

    public static void ApplyAll(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue = null, GenericRepresentationPolicy policy = null,
        Action<string, string, int, int, TypeNode> recordStorage = null)
    {
        isValue ??= _ => false;
        var files = roots.OfType<JsonObject>().ToArray();
        var semanticByPhysical = localExistentialOwners.ToDictionary(
            pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var root in files) CollectTypes(root, definitions);
        var frames = refs.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (name, definition) in definitions)
            if (KotlinSupertypesRecord.ReadNullableFrame(definition) is { } frame) frames[name] = frame;
        foreach (var root in files)
            Apply(root, semanticByPhysical, refs, definitions, frames, isValue, policy, recordStorage);
    }

    static void Apply(JsonObject root, IReadOnlyDictionary<string, string> semanticByPhysical,
        ReferenceMetadataIndex refs, IReadOnlyDictionary<string, JsonObject> definitions,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy, Action<string, string, int, int, TypeNode> recordStorage)
    {
        var uses = new Dictionary<(string Closure, int Position), List<CaptureUse>>();
        void PrepareSuspend(JsonObject lambda) => ErasedElementValues.ApplySuspendBody(
            lambda, refs, definitions, frames, isValue, policy);
        VisitOwner(root, uses, semanticByPhysical, refs, PrepareSuspend);
        var dependents = new Dictionary<string, HashSet<(string Closure, int Position)>>(StringComparer.Ordinal);
        foreach (var (key, captureUses) in uses)
            foreach (var use in captureUses)
                for (JsonNode parent = use.Construction.Parent; parent != null; parent = parent.Parent)
                    if (parent is JsonObject owner && Str(owner["name"]) is string ownerName
                        && definitions.TryGetValue(ownerName, out var definition) && ReferenceEquals(owner, definition))
                    {
                        if (!dependents.TryGetValue(ownerName, out var set)) dependents[ownerName] = set = new();
                        set.Add(key);
                        break;
                    }
        bool IsObject(TypeNode type) => type != null && BirTypeLowering.SamePhysicalSlotType(type,
            new TypeNode.Fqn("object"), refs.Aliases, isValue, refs.PhysicalTypeNames,
            returnPosition: false, nullableFrames: frames);
        TypeNode Current(CaptureUse use)
        {
            var current = PhysicalType(use.Expression, new Dictionary<string, TypeNode>());
            return IsObject(current) || current is TypeNode.Fqn f && TrySemanticOwner(f.Name, semanticByPhysical, refs, out _)
                ? current : use.InitialType;
        }
        var pending = new HashSet<(string Closure, int Position)>(uses.Keys);
        var queue = new Queue<(string Closure, int Position)>(uses.Keys);

        while (queue.TryDequeue(out var key))
        {
            pending.Remove(key);
            var (closureName, position) = key;
            var captureUses = uses[key];
            var types = captureUses.Select(Current).ToArray();
            var erased = types.Any(IsObject);
            var carriers = types.OfType<TypeNode.Fqn>()
                .Where(type => TrySemanticOwner(type.Name, semanticByPhysical, refs, out _)).Distinct().ToArray();
            if (!erased && carriers.Length == 0) continue;
            if (!erased && carriers.Length != 1)
                throw new InvalidOperationException(
                    $"bir2cir: generated closure '{closureName}' capture {position} has conflicting existential carriers");
            if (!definitions.TryGetValue(closureName, out var closure) || !Bool(closure["generated"])
                || closure["fields"] is not JsonArray fields || position >= fields.Count
                || fields[position] is not JsonObject field
                || Str(field["name"]) is not string fieldName
                || TypeJson.Read(field["type"]) is not TypeNode fieldType)
                throw new InvalidOperationException(
                    $"bir2cir: existential capture {position} has no exact generated closure storage in '{closureName}'");

            string semanticOwner = null;
            var raw = erased && fieldType is TypeNode.Tv;
            var carrier = raw ? new TypeNode.Fqn("object") : carriers.FirstOrDefault();
            if (carrier == null) continue;
            if (fieldType.Equals(carrier)) continue;
            if (!raw && (!TrySemanticOwner(carrier.Name, semanticByPhysical, refs, out semanticOwner)
                || fieldType is not TypeNode.Fqn { Args: { Length: > 0 } } logical || logical.Name != semanticOwner))
                continue;

            if (closure["ctors"] is not JsonArray { Count: 1 } constructors
                || constructors[0] is not JsonObject constructor
                || constructor["params"] is not JsonArray parameters || position >= parameters.Count
                || parameters[position] is not JsonObject parameter || Str(parameter["name"]) != fieldName)
                throw new InvalidOperationException(
                    $"bir2cir: generated closure '{closureName}' capture '{fieldName}' has no exact constructor slot");

            field["type"] = TypeJson.Write(carrier);
            parameter["type"] = TypeJson.Write(carrier);
            recordStorage?.Invoke(closureName, fieldName, position, parameters.Count, carrier);
            if (raw)
            {
                var sourceType = KotlinSupertypesRecord.ReadNullableFrame(closure)?.SemanticVariable((TypeNode.Tv)fieldType)
                    ?? fieldType;
                field["kotlinType"] ??= TypeNode.ToJson(sourceType);
                parameter["kotlinType"] ??= TypeNode.ToJson(sourceType);
            }
            RetypeFieldUses(closure, closureName, fieldName, carrier);
            foreach (var use in captureUses)
                if (raw)
                {
                    var expression = use.Expression;
                    if (Str(expression["k"]) != "cast" || !IsObject(TypeJson.Read(expression["type"])))
                        use.Construction["captures"][position] = new JsonObject {
                            ["k"] = "cast", ["type"] = TypeJson.Write(carrier), ["e"] = expression.DeepClone(),
                        };
                }
                else RetypeCaptureBoundary(use.Expression, semanticOwner, carrier);
            if (raw) ErasedElementValues.ApplyType(closure, refs, definitions, frames, isValue, policy);
            // Only changed generated storage invalidates its nested capture consumers. Do not normalize the
            // complete module repeatedly or depend on the order closure declarations were appended.
            if (dependents.TryGetValue(closureName, out var affected))
                foreach (var consumer in affected)
                    if (pending.Add(consumer)) queue.Enqueue(consumer);
        }
    }

    static void CollectTypes(JsonObject owner, Dictionary<string, JsonObject> definitions)
    {
        if (owner["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Str(type["name"]) is string name) definitions[name] = type;
            CollectTypes(type, definitions);
        }
    }

    static void VisitOwner(JsonObject owner,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs, Action<JsonObject> prepareSuspend)
    {
        if (owner["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>()) VisitDeclaration(method, uses, semanticByPhysical, refs, prepareSuspend);
        if (owner["ctors"] is JsonArray constructors)
            foreach (var constructor in constructors.OfType<JsonObject>())
                VisitDeclaration(constructor, uses, semanticByPhysical, refs, prepareSuspend);
        if (owner["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) VisitOwner(type, uses, semanticByPhysical, refs, prepareSuspend);
    }

    static void VisitDeclaration(JsonObject declaration,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs, Action<JsonObject> prepareSuspend)
    {
        if (declaration["body"] is not JsonArray body) return;
        var vars = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        if (declaration["params"] is JsonArray parameters)
            foreach (var parameter in parameters.OfType<JsonObject>())
                if (Str(parameter["name"]) is string name && TypeJson.Read(parameter["type"]) is TypeNode type)
                    vars[name] = type;
        Visit(body, vars, uses, semanticByPhysical, refs, prepareSuspend);
    }

    static void Visit(JsonNode node, Dictionary<string, TypeNode> vars,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs, Action<JsonObject> prepareSuspend)
    {
        if (node is JsonArray array)
        {
            vars = new Dictionary<string, TypeNode>(vars, StringComparer.Ordinal);
            foreach (var child in array)
                if (child != null) Visit(child, vars, uses, semanticByPhysical, refs, prepareSuspend);
            return;
        }
        if (node is not JsonObject obj) return;

        if (Str(obj["k"]) == "var")
        {
            if (obj["init"] != null) Visit(obj["init"], vars, uses, semanticByPhysical, refs, prepareSuspend);
            if (Str(obj["name"]) is string name && TypeJson.Read(obj["type"]) is TypeNode type) vars[name] = type;
            return;
        }
        if (Str(obj["k"]) == "newSuspendLambda")
        {
            AlignSuspendCaptures(obj, vars, uses, semanticByPhysical, refs, prepareSuspend);
            return;
        }
        var generatedTypeSlot = Str(obj["k"]) switch
        {
            "newClosure" => "closureType",
            "newSam" => "samType",
            _ => null,
        };
        if (generatedTypeSlot != null
            && TypeJson.Read(obj[generatedTypeSlot]) is TypeNode.Fqn closure
            && obj["captures"] is JsonArray captures)
        {
            for (var position = 0; position < captures.Count; position++)
                if (captures[position] is JsonObject capture)
                {
                    var key = (closure.Name, position);
                    if (!uses.TryGetValue(key, out var list)) uses[key] = list = new List<CaptureUse>();
                    list.Add(new CaptureUse(obj, position, PhysicalType(capture, vars)));
                }
            foreach (var capture in captures)
                if (capture != null) Visit(capture, vars, uses, semanticByPhysical, refs, prepareSuspend);
            return;
        }

        // These are declaration/carrier boundaries, not children in the current lexical frame. Materialized
        // newClosure/newSam values were handled above; the remaining carrier kinds are either consumed by earlier
        // lowering or own independent parameters/locals.
        if (Str(obj["k"]) is "lambda" or "inlineLambda" or "newDelegate"
            or "forEachInline" or "repeatInline") return;

        foreach (var value in obj.Select(pair => pair.Value).ToList())
            if (value != null) Visit(value, vars, uses, semanticByPhysical, refs, prepareSuspend);
    }

    // A suspend lambda is materialized later, so align its capture declaration before SuspendLambdaLowering copies
    // that declaration into the state-machine constructor and field. The construction value is either the explicit
    // capValues entry supplied by cold lowering or the same-named local in the enclosing frame. Recurse into the
    // lambda body with its own declared frame so nested suspend lambdas are handled without leaking locals outward.
    static void AlignSuspendCaptures(JsonObject node, IReadOnlyDictionary<string, TypeNode> outerVars,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs, Action<JsonObject> prepareSuspend)
    {
        var nested = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var captures = node["captures"] as JsonArray;
        var captureValues = node["capValues"] as JsonArray;
        var rawChanged = false;
        if (captures != null)
            for (var position = 0; position < captures.Count; position++)
            {
                if (captures[position] is not JsonObject capture
                    || Str(capture["name"]) is not string name
                    || TypeJson.Read(capture["type"]) is not TypeNode declared)
                    continue;
                var value = captureValues != null && position < captureValues.Count
                    ? captureValues[position]
                    : null;
                var physical = value != null
                    ? PhysicalType(value, outerVars)
                    : outerVars.GetValueOrDefault(name);
                if (declared is TypeNode.Tv && physical != null && BirTypeLowering.SamePhysicalSlotType(
                        physical, new TypeNode.Fqn("object"), refs.Aliases, refs.IsValueType,
                        refs.PhysicalTypeNames, returnPosition: false, nullableFrames: refs.NullableTypeFrames))
                {
                    capture["kotlinType"] ??= TypeNode.ToJson(declared);
                    capture["type"] = TypeJson.Fqn("object");
                    declared = new TypeNode.Fqn("object");
                    rawChanged = true;
                }
                else if (physical is TypeNode.Fqn carrier
                    && TrySemanticOwner(carrier.Name, semanticByPhysical, refs, out var semanticOwner)
                    && declared is TypeNode.Fqn { Args: { Length: > 0 } } logical
                    && logical.Name == semanticOwner)
                {
                    capture["type"] = TypeJson.Write(carrier);
                    if (value is JsonObject expression)
                        RetypeCaptureBoundary(expression, semanticOwner, carrier);
                    declared = carrier;
                }
                nested[name] = declared;
            }
        if (rawChanged) prepareSuspend(node);
        if (node["params"] is JsonArray parameters)
            foreach (var parameter in parameters.OfType<JsonObject>())
                if (Str(parameter["name"]) is string name
                    && TypeJson.Read(parameter["type"]) is TypeNode type)
                    nested[name] = type;
        if (node["body"] != null)
            Visit(node["body"], nested, uses, semanticByPhysical, refs, prepareSuspend);
    }

    // Ask for the value actually delivered to a physical storage slot. A valueBlock/conditional can retain its
    // frontend unified Kotlin type after an inner erased cast was retyped; derive those wrappers from their result
    // instead. Local declarations remain the authority for otherwise-unstamped reads.
    static TypeNode PhysicalType(JsonNode node, IReadOnlyDictionary<string, TypeNode> vars)
    {
        if (node is not JsonObject obj) return null;
        var kind = Str(obj["k"]);
        if (kind == "local" && Str(obj["name"]) is string name && vars.TryGetValue(name, out var local)) return local;
        if (kind == "valueBlock")
        {
            var nested = new Dictionary<string, TypeNode>(vars, StringComparer.Ordinal);
            foreach (var key in new[] { "stmts", "body" })
                if (obj[key] is JsonArray statements)
                    foreach (var statement in statements.OfType<JsonObject>())
                        if (Str(statement["k"]) == "var" && Str(statement["name"]) is string variable
                            && TypeJson.Read(statement["type"]) is TypeNode type)
                            nested[variable] = type;
            return PhysicalType(obj["result"], nested);
        }
        if (kind == "cond")
        {
            var thenType = PhysicalType(obj["then"], vars);
            var elseType = PhysicalType(obj["else"], vars);
            if (NodeType.IsNothing(thenType)) return elseType;
            if (NodeType.IsNothing(elseType)) return thenType;
            return thenType != null && thenType.Equals(elseType) ? thenType : null;
        }
        return NodeType.Of(obj, child => PhysicalType(child, vars));
    }

    static bool TrySemanticOwner(string physical,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs,
        out string semantic)
    {
        if (semanticByPhysical.TryGetValue(physical, out semantic)) return true;
        return refs.TryExistentialSemanticOwner(physical, out semantic);
    }

    static void RetypeCaptureBoundary(JsonObject expression, string semanticOwner, TypeNode.Fqn carrier)
    {
        foreach (var key in new[] { "type", "sty" })
            if (TypeJson.Read(expression[key]) is TypeNode.Fqn { Args: { Length: > 0 } } logical
                && logical.Name == semanticOwner)
                expression[key] = TypeJson.Write(carrier);
    }

    static void RetypeFieldUses(JsonNode node, string closureName, string fieldName, TypeNode.Fqn carrier)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) == "field" && Str(obj["name"]) == fieldName
                && TypeJson.Read(obj["ownerType"]) is TypeNode.Fqn owner && owner.Name == closureName)
            {
                obj["sty"] = TypeJson.Write(carrier);
                if (obj["memberType"] != null) obj["memberType"] = TypeJson.Write(carrier);
            }
            foreach (var value in obj.Select(pair => pair.Value).ToList())
                if (value != null) RetypeFieldUses(value, closureName, fieldName, carrier);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child != null) RetypeFieldUses(child, closureName, fieldName, carrier);
    }

    static bool Bool(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
