using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A compiler-generated closure is physical CLR storage: each capture value, constructor parameter, backing field,
// and field read must agree on one exact representation. FBoundStarProjectionErasure can turn the successful
// smart-cast value captured by a callable reference into a non-generic existential carrier after ClosureSynthesis
// has already copied the Kotlin constructed type into the generated class. Leaving that earlier type on the field
// makes newobj unverifiable (`G$star` on the stack, `G<T>` in the constructor descriptor).
//
// Align only a carrier whose trusted local/reference metadata names the capture field's original generic classifier.
// The generated closure declaration and its construction node are the complete authority; no function names, class
// layout guesses, or old artifact spellings participate. The ordinary ExistentialReceiverBinding pass subsequently
// binds calls through the retyped field to the exact carrier slot and preserves their semantic result with an explicit
// projection.
static class ExistentialCaptureAlignment
{
    sealed record CaptureUse(JsonObject Expression, TypeNode.Fqn Carrier);

    public static void ApplyAll(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners,
        ReferenceMetadataIndex refs)
    {
        var semanticByPhysical = localExistentialOwners.ToDictionary(
            pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        foreach (var root in roots.OfType<JsonObject>())
            Apply(root, semanticByPhysical, refs);
    }

    static void Apply(JsonObject root, IReadOnlyDictionary<string, string> semanticByPhysical,
        ReferenceMetadataIndex refs)
    {
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        CollectTypes(root, definitions);
        var uses = new Dictionary<(string Closure, int Position), List<CaptureUse>>();
        VisitOwner(root, uses, semanticByPhysical, refs);

        foreach (var ((closureName, position), captureUses) in uses)
        {
            var carriers = captureUses.Select(use => use.Carrier).Distinct().ToArray();
            if (carriers.Length != 1)
                throw new InvalidOperationException(
                    $"bir2cir: generated closure '{closureName}' capture {position} has conflicting existential carriers");
            if (!definitions.TryGetValue(closureName, out var closure) || !Bool(closure["generated"])
                || closure["fields"] is not JsonArray fields || position >= fields.Count
                || fields[position] is not JsonObject field
                || Str(field["name"]) is not string fieldName
                || TypeJson.Read(field["type"]) is not TypeNode fieldType)
                throw new InvalidOperationException(
                    $"bir2cir: existential capture {position} has no exact generated closure storage in '{closureName}'");

            var carrier = carriers[0];
            if (!TrySemanticOwner(carrier.Name, semanticByPhysical, refs, out var semanticOwner))
                continue;
            if (fieldType.Equals(carrier)) continue;
            if (fieldType is not TypeNode.Fqn { Args: { Length: > 0 } } logical
                || logical.Name != semanticOwner)
                continue;

            if (closure["ctors"] is not JsonArray { Count: 1 } constructors
                || constructors[0] is not JsonObject constructor
                || constructor["params"] is not JsonArray parameters || position >= parameters.Count
                || parameters[position] is not JsonObject parameter || Str(parameter["name"]) != fieldName)
                throw new InvalidOperationException(
                    $"bir2cir: generated closure '{closureName}' capture '{fieldName}' has no exact constructor slot");

            field["type"] = TypeJson.Write(carrier);
            parameter["type"] = TypeJson.Write(carrier);
            RetypeFieldUses(closure, closureName, fieldName, carrier);
            foreach (var use in captureUses)
                RetypeCaptureBoundary(use.Expression, semanticOwner, carrier);
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
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs)
    {
        if (owner["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>()) VisitDeclaration(method, uses, semanticByPhysical, refs);
        if (owner["ctors"] is JsonArray constructors)
            foreach (var constructor in constructors.OfType<JsonObject>())
                VisitDeclaration(constructor, uses, semanticByPhysical, refs);
        if (owner["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) VisitOwner(type, uses, semanticByPhysical, refs);
    }

    static void VisitDeclaration(JsonObject declaration,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs)
    {
        if (declaration["body"] is not JsonArray body) return;
        var vars = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        if (declaration["params"] is JsonArray parameters)
            foreach (var parameter in parameters.OfType<JsonObject>())
                if (Str(parameter["name"]) is string name && TypeJson.Read(parameter["type"]) is TypeNode type)
                    vars[name] = type;
        Visit(body, vars, uses, semanticByPhysical, refs);
    }

    static void Visit(JsonNode node, Dictionary<string, TypeNode> vars,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array)
                if (child != null) Visit(child, vars, uses, semanticByPhysical, refs);
            return;
        }
        if (node is not JsonObject obj) return;

        if (Str(obj["k"]) == "var")
        {
            if (obj["init"] != null) Visit(obj["init"], vars, uses, semanticByPhysical, refs);
            if (Str(obj["name"]) is string name && TypeJson.Read(obj["type"]) is TypeNode type) vars[name] = type;
            return;
        }
        if (Str(obj["k"]) == "localFun" && obj["body"] is JsonArray)
        {
            VisitDeclaration(obj, uses, semanticByPhysical, refs);
            return;
        }
        if (Str(obj["k"]) == "newClosure"
            && TypeJson.Read(obj["closureType"]) is TypeNode.Fqn closure
            && obj["captures"] is JsonArray captures)
        {
            for (var position = 0; position < captures.Count; position++)
                if (captures[position] is JsonObject capture
                    && PhysicalType(capture, vars) is TypeNode.Fqn carrier
                    && TrySemanticOwner(carrier.Name, semanticByPhysical, refs, out _))
                {
                    var key = (closure.Name, position);
                    if (!uses.TryGetValue(key, out var list)) uses[key] = list = new List<CaptureUse>();
                    list.Add(new CaptureUse(capture, carrier));
                }
        }

        foreach (var value in obj.Select(pair => pair.Value).ToList())
            if (value != null) Visit(value, vars, uses, semanticByPhysical, refs);
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
