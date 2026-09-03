using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// FBoundStarProjectionErasure gives Kotlin's G<*> / erased G<T> cast a physical non-generic
// compiler-generated existential interface. GenericDowncastRealignment subsequently aligns a local declaration
// with such a cast. Calls through that local must then target the exact existential interface
// slot, including its deterministic owner-T-dependent bridge name. Leaving the original G<T>
// MemberRef on that existential receiver is invalid CLR IL.
//
// This is an explicit CIR binding pass: it consumes the synthesized interface's actual method
// table (or reference metadata), and ilemit merely emits the owner/member recorded here.
static class ExistentialReceiverBinding
{
    public sealed class Index
    {
        internal readonly Dictionary<string, List<Member>> Members = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> SemanticOwnerByPhysical = new(StringComparer.Ordinal);
    }

    internal sealed record Member(string Name, string SourceName, string AccessorKind,
        TypeNode[] Parameters, TypeNode Return, int GenericArity)
    {
        public int ParamCount => Parameters.Length;
    }

    public static Index Collect(IEnumerable<JsonNode> roots)
    {
        var index = new Index();
        foreach (var root in roots) CollectTypes(root, index);
        return index;
    }

    static void CollectTypes(JsonNode node, Index index)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            var name = Str(type["name"]);
            if (name != null && ExistentialSemanticOwner(type) is string semanticOwner
                && type["methods"] is JsonArray methods)
            {
                var slots = new List<Member>();
                foreach (var method in methods.OfType<JsonObject>())
                    if (!Bool(method["static"]) && Str(method["name"]) is string mn)
                    {
                        var isProperty = KotlinPropertyAccessors.TryIdentity(method,
                            out var propertyName, out var accessorKind);
                        slots.Add(new Member(
                            mn,
                            isProperty ? propertyName
                                : Str(method[FBoundStarProjectionErasure.SourceMemberKey]) ?? mn,
                            isProperty ? accessorKind : null,
                            (method["params"] as JsonArray)?.OfType<JsonObject>()
                                .Select(p => TypeJson.Read(p["type"]))
                                .Where(t => t != null).ToArray() ?? Array.Empty<TypeNode>(),
                            TypeJson.Read(method["ret"]),
                            (method["typeParams"] as JsonArray)?.Count ?? 0));
                    }
                index.Members[name] = slots;
                index.SemanticOwnerByPhysical[name] = semanticOwner;
            }
            CollectTypes(type, index);
        }
    }

    public static void Apply(JsonNode root, Index index, ReferenceMetadataIndex refs)
    {
        VisitDeclarations(root, index, refs);
    }

    static void VisitDeclarations(JsonNode node, Index index, ReferenceMetadataIndex refs)
    {
        if (node is not JsonObject obj) return;

        if (obj["body"] is JsonArray body && obj["params"] is JsonArray)
        {
            var vars = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            foreach (var p in (obj["params"] as JsonArray).OfType<JsonObject>())
                if (Str(p["name"]) is string pn && TypeJson.Read(p["type"]) is TypeNode pt)
                    vars[pn] = pt;
            VisitStatements(body, vars, index, refs);
        }

        if (obj["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) VisitDeclarations(type, index, refs);
        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>()) VisitDeclarations(method, index, refs);
        if (obj["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>()) VisitDeclarations(ctor, index, refs);
    }

    static void VisitStatements(JsonNode node, Dictionary<string, TypeNode> vars,
        Index index, ReferenceMetadataIndex refs)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item != null) VisitStatements(item, vars, index, refs);
            return;
        }
        if (node is not JsonObject obj) return;

        var kind = Str(obj["k"]);
        if (kind == "var")
        {
            if (obj["init"] != null) VisitStatements(obj["init"], vars, index, refs);
            if (Str(obj["name"]) is string vn && TypeJson.Read(obj["type"]) is TypeNode vt)
                vars[vn] = vt;
            return;
        }

        if (kind == "callInstance")
            BindCall(obj, vars, index, refs);

        // A nested carrier owns its own parameters/locals. It is visited as a declaration
        // elsewhere if materialized; do not leak the enclosing lexical environment into it.
        if (kind is "lambda" or "inlineLambda" or "newClosure" or "newDelegate"
            or "newSuspendLambda" or "forEachInline" or "repeatInline")
            return;

        foreach (var value in obj.Select(kv => kv.Value).ToList())
            if (value != null) VisitStatements(value, vars, index, refs);
    }

    static void BindCall(JsonObject call, IReadOnlyDictionary<string, TypeNode> vars,
        Index index, ReferenceMetadataIndex refs)
    {
        if (Str(call["method"]) is not string authoredMethod
            || authoredMethod.StartsWith("$star$", StringComparison.Ordinal))
            return;
        var propertyCall = KotlinPropertyAccessors.TryCallIdentity(call,
            out var sourcePropertyName, out var accessorKind);
        var sourceMethod = propertyCall ? sourcePropertyName : authoredMethod;
        // Inline substitution replaces a parameter local with the concrete argument type, but the selected call owner
        // remains the existential interface whose slot must be invoked. Prefer that explicit declaration carrier when
        // it is already physical; receiver inference remains the path for casts and realigned locals whose authored
        // owner still uses the semantic generic surface.
        var authoredOwner = TypeJson.Read(call["ownerType"]) as TypeNode.Fqn;
        var receiverType = authoredOwner != null
            && (index.SemanticOwnerByPhysical.ContainsKey(authoredOwner.Name)
                || refs.IsExistentialPhysicalOwner(authoredOwner.Name))
            ? authoredOwner
            : ReceiverType(call["recv"], vars) as TypeNode.Fqn;
        if (receiverType == null || (!index.SemanticOwnerByPhysical.ContainsKey(receiverType.Name)
            && !refs.IsExistentialPhysicalOwner(receiverType.Name))) return;

        var pc = (call["sig"] as JsonArray)?.Count
            ?? (call["argTypes"] as JsonArray)?.Count
            ?? (call["args"] as JsonArray)?.Count ?? 0;
        var ga = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        var authoredSignature = ((call["sig"] ?? call["argTypes"]) as JsonArray)?
            .Select(TypeJson.Read).ToArray();
        if (authoredSignature?.Any(t => t == null) == true) authoredSignature = null;
        string physicalMethod = null;
        TypeNode[] physicalParameters = null;
        TypeNode physicalResult = null;

        if (index.Members.TryGetValue(receiverType.Name, out var members))
        {
            var candidates = members
                .Where(m => m.ParamCount == pc && m.GenericArity == ga
                    && m.SourceName == sourceMethod && m.AccessorKind == accessorKind
                    && SignatureMatches(m.Parameters, authoredSignature))
                // Duplicate roots may describe the same physical slot. Coalesce only an identical name+descriptor;
                // grouping by name alone discards the frontend-resolved overload signature and selects whichever
                // same-name accessor was enumerated first.
                .GroupBy(m => m.Name + "\u001f" + string.Join("\u001f", m.Parameters.Select(p => p.ToString())),
                    StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (candidates.Count == 1)
            {
                physicalMethod = candidates[0].Name;
                physicalParameters = candidates[0].Parameters;
                physicalResult = candidates[0].Return;
            }
        }
        else
        {
            var sourceOwner = index.SemanticOwnerByPhysical.GetValueOrDefault(receiverType.Name);
            if (sourceOwner == null)
                refs.TryExistentialSemanticOwner(receiverType.Name, out sourceOwner);
            var semanticOwner = TypeJson.Read(call["ownerType"]) as TypeNode.Fqn
                ?? new TypeNode.Fqn(sourceOwner, Array.Empty<TypeNode>());
            if (semanticOwner.Name != sourceOwner)
                semanticOwner = new TypeNode.Fqn(sourceOwner, semanticOwner.Args);
            if (refs.TryStarProjectionMember(semanticOwner, sourceMethod, accessorKind,
                    ga, authoredSignature, pc, Str(call[DeclarationIdentityBinding.Key]),
                    out var erasedOwner, out var erasedMethod, out var erasedSignature, out _,
                    out var erasedResult)
                && erasedOwner == receiverType.Name)
            {
                physicalMethod = erasedMethod;
                physicalParameters = erasedSignature;
                physicalResult = erasedResult;
            }
        }

        if (physicalMethod == null) return; // ambiguous or absent: never guess a physical slot
        call["ownerType"] = TypeJson.Write(receiverType);
        call["method"] = physicalMethod;
        if (propertyCall)
        {
            KotlinPropertyAccessors.PreserveCallIdentity(call, sourcePropertyName, accessorKind);
            call.Remove("prop");
        }
        if (physicalParameters != null)
            call["sig"] = new JsonArray(physicalParameters.Select(TypeJson.Write).ToArray());
        call["virtual"] = true;
        AlignResult(call, physicalResult);
    }

    static void AlignResult(JsonObject call, TypeNode physicalResult)
    {
        var methodArgs = (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray()
            ?? Array.Empty<TypeNode>();
        physicalResult = FBoundStarProjectionErasure.SubstituteMethodTypeArguments(physicalResult, methodArgs);
        var semanticResult = TypeJson.Read(call["dynRet"])
            ?? TypeJson.Read(call["sty"])
            ?? TypeJson.Read(call["ret"]);
        if (physicalResult == null || semanticResult == null || physicalResult.Equals(semanticResult)
            || FBoundStarProjectionErasure.IsVoidResult(physicalResult)
                && FBoundStarProjectionErasure.IsVoidResult(semanticResult)) return;

        var hadSty = call["sty"] != null;
        var inner = call.DeepClone().AsObject();
        inner["ret"] = TypeJson.Write(physicalResult);
        if (inner["dynRet"] != null) inner["dynRet"] = TypeJson.Write(physicalResult);
        if (inner["sty"] != null) inner["sty"] = TypeJson.Write(physicalResult);

        foreach (var key in call.Select(pair => pair.Key).ToList()) call.Remove(key);
        call["k"] = "cast";
        call["type"] = TypeJson.Write(semanticResult);
        call["e"] = inner;
        if (hadSty) call["sty"] = TypeJson.Write(semanticResult);
    }

    static bool SignatureMatches(IReadOnlyList<TypeNode> declaration, IReadOnlyList<TypeNode> call)
    {
        if (call == null) return true;
        return declaration.Count == call.Count
            && declaration.Select((parameter, index) =>
                    ReferenceMetadataIndex.AccessorDeclarationDescribesCall(parameter, call[index]))
                .All(match => match);
    }

    static TypeNode ReceiverType(JsonNode receiver, IReadOnlyDictionary<string, TypeNode> vars)
    {
        if (receiver is not JsonObject obj) return null;
        if (Str(obj["k"]) == "cast") return TypeJson.Read(obj["type"]);
        if (Str(obj["k"]) == "local" && Str(obj["name"]) is string name
            && vars.TryGetValue(name, out var type)) return type;
        return TypeJson.Read(obj["sty"]) ?? TypeJson.Read(obj["ret"]) ?? TypeJson.Read(obj["type"]);
    }

    static string ExistentialSemanticOwner(JsonObject type)
    {
        if (!Bool(type["generated"]) || Str(type["kotlinType"]) is not string encoded) return null;
        try
        {
            return TypeNode.Parse(encoded) is TypeNode.Fqn { Args: { Length: > 0 } args } f
                && args.All(a => a is TypeNode.Star) ? f.Name : null;
        }
        catch { return null; }
    }

    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
