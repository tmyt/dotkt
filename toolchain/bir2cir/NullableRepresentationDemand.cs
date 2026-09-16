using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Analysis only: source declarations and bodies are not rewritten here. A body-only demand must not change an
// independently declared virtual slot. Frame materialization and metadata publication consume these facts separately.
static partial class NullableRepresentationDemand
{
    internal sealed class Variables
    {
        public HashSet<int> Type { get; } = new();
        public HashSet<int> Method { get; } = new();

        public void Add(TypeNode.Tv variable)
        {
            if (variable.Scope == "type") Type.Add(variable.I);
            else if (variable.Scope == "method") Method.Add(variable.I);
            else throw new InvalidOperationException("Unknown generic parameter scope");
        }
    }

    internal sealed record MethodDemand(JsonObject Declaration, Variables Signature, Variables Body)
    {
        // A static implementation owns its generic MethodDef frame. It has no inherited dispatch slot whose
        // arity must remain fixed; the explicit metadata frame restores its unchanged Kotlin source arity.
        // Instance dispatch still needs a separate implementation entry for body-only demand.
        public bool CanExtendBodyFrame => Flag(Declaration["static"])
            && !Flag(Declaration["virtual"]) && !Flag(Declaration["override"]) && !Flag(Declaration["abstract"])
            && (Declaration["overrides"] as JsonArray)?.Count is not > 0;

        public NullableRepresentationFrame Frame => new(
            (Declaration["typeParams"] as JsonArray)?.Count ?? 0,
            Signature.Method.Concat(CanExtendBodyFrame ? Body.Method : Enumerable.Empty<int>()).Distinct().OrderBy(i => i));
    }

    static bool Flag(JsonNode node) => (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;

    internal sealed record OwnerDemand(JsonObject Declaration, Variables Signature, Variables Body, List<MethodDemand> Methods)
    {
        public OwnerDemand CapturedOwner { get; set; }
        public int CaptureOffset { get; set; }

        public NullableRepresentationFrame Frame
        {
            get
            {
                var arity = (Declaration["typeParams"] as JsonArray)?.Count ?? 0;
                var enclosing = CapturedOwner?.Frame;
                var indices = Signature.Type.Concat(Methods.SelectMany(method => method.Signature.Type))
                    .Concat(enclosing?.NullableIndices.Select(index => CaptureOffset + index) ?? Enumerable.Empty<int>())
                    .Distinct().OrderBy(i => i).ToArray();
                if (enclosing == null) return new NullableRepresentationFrame(arity, indices);
                // Source capture segments can occur after the child's own variables. CLR requires the entire
                // enclosing physical frame first, including its companions, in exactly the enclosing order.
                var prefix = enclosing.PhysicalOrder.Select(slot => slot < enclosing.SourceArity
                    ? CaptureOffset + slot
                    : arity + Array.IndexOf(indices, CaptureOffset + enclosing.NullableIndices[slot - enclosing.SourceArity]))
                    .ToArray();
                var captured = prefix.ToHashSet();
                return new NullableRepresentationFrame(arity, indices,
                    prefix.Concat(Enumerable.Range(0, arity + indices.Length).Where(slot => !captured.Contains(slot))));
            }
        }
    }

    public static IReadOnlyList<OwnerDemand> Collect(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, NullableRepresentationFrame> referencedTypes = null,
        IReadOnlyDictionary<string, NullableRepresentationFrame> referencedMethods = null)
    {
        var owners = new List<OwnerDemand>();
        void Discover(JsonObject declaration)
        {
            var methods = (declaration["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(method => new MethodDemand(method, new Variables(), new Variables())).ToList();
            owners.Add(new OwnerDemand(declaration, new Variables(), new Variables(), methods));
            foreach (var nested in (declaration["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Discover(nested);
        }
        foreach (var root in roots.OfType<JsonObject>()) Discover(root);

        var declarations = owners.Where(owner => Text(owner.Declaration["kind"]) != null)
            .ToDictionary(owner => Text(owner.Declaration["name"]), StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            if (owner.Declaration["outerTypeParamCount"] is not JsonValue capturedValue
                || !capturedValue.TryGetValue<int>(out var captured) || captured == 0) continue;
            if (Text(owner.Declaration["semanticOwner"]) is not string parentName
                || !declarations.TryGetValue(parentName, out var parent)) continue;
            var offset = (owner.Declaration["outerTypeParamOffset"] as JsonValue)?.GetValue<int>() ?? 0;
            if (captured != (parent.Declaration["typeParams"] as JsonArray)?.Count || offset < 0
                || offset + captured > (owner.Declaration["typeParams"] as JsonArray)?.Count)
                throw new InvalidOperationException("Semantic owner capture does not match source generic frame");
            owner.CapturedOwner = parent;
            owner.CaptureOffset = offset;
        }
        foreach (var owner in owners)
        {
            var seen = new HashSet<JsonObject>();
            for (var cursor = owner; cursor != null; cursor = cursor.CapturedOwner)
                if (!seen.Add(cursor.Declaration)) throw new InvalidOperationException("Cyclic semantic owner capture");
        }

        bool changed;
        do
        {
            var typeFrames = referencedTypes == null
                ? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal)
                : new Dictionary<string, NullableRepresentationFrame>(referencedTypes, StringComparer.Ordinal);
            var methodFrames = referencedMethods == null
                ? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal)
                : new Dictionary<string, NullableRepresentationFrame>(referencedMethods, StringComparer.Ordinal);
            foreach (var owner in owners)
            {
                if (Text(owner.Declaration["kind"]) != null)
                    typeFrames[Text(owner.Declaration["name"])] = owner.Frame;
                foreach (var method in owner.Methods)
                    if (Text(method.Declaration[DeclarationIdentityBinding.Key]) is string id)
                        methodFrames[id] = method.Frame;
            }
            var before = Count(owners);
            foreach (var owner in owners)
            {
                foreach (var key in new[] { "base", "interfaces", "typeParams" })
                    Scan(owner.Declaration[key], owner.Signature, typeFrames, methodFrames);
                foreach (var key in new[] { "fields", "properties" })
                    foreach (var slot in (owner.Declaration[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        Scan(slot["type"], owner.Signature, typeFrames, methodFrames);
                        Scan(slot["init"], owner.Body, typeFrames, methodFrames);
                    }
                foreach (var ctor in (owner.Declaration["ctors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    Scan(ctor["params"], owner.Signature, typeFrames, methodFrames);
                    Scan(ctor["body"], owner.Body, typeFrames, methodFrames);
                }
                foreach (var method in owner.Methods)
                {
                    foreach (var key in new[] { "params", "ret", "typeParams" })
                        Scan(method.Declaration[key], method.Signature, typeFrames, methodFrames);
                    Scan(method.Declaration["body"], method.Body, typeFrames, methodFrames);
                }
            }
            changed = Count(owners) != before;
        } while (changed);
        return owners;
    }

    static int Count(IEnumerable<OwnerDemand> owners) => owners.Sum(owner =>
        owner.Signature.Type.Count + owner.Signature.Method.Count + owner.Body.Type.Count + owner.Body.Method.Count + owner.Methods.Sum(method =>
            method.Signature.Type.Count + method.Signature.Method.Count + method.Body.Type.Count + method.Body.Method.Count));

    static void Scan(JsonNode node, Variables result,
        IReadOnlyDictionary<string, NullableRepresentationFrame> types,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methods, bool argument = false)
    {
        if (node == null) return;
        if (TypeJson.Read(node) is TypeNode type)
        {
            Visit(type, argument, result, types);
            return;
        }
        if (node is JsonArray array)
        {
            foreach (var item in array) Scan(item, result, types, methods, argument);
        }
        else if (node is JsonObject obj)
        {
            if (Text(obj["k"]) != null && Text(obj[DeclarationIdentityBinding.Key]) is string id
                && methods.TryGetValue(id, out var frame) && frame.NullableIndices.Count != 0)
            {
                if (obj["typeArgs"] is not JsonArray arguments || arguments.Count != frame.SourceArity)
                    throw new InvalidOperationException("Generic call does not match its declared nullable frame");
                foreach (var index in frame.NullableIndices)
                    RequireNullable(TypeJson.Read(arguments[index]), result);
            }
            foreach (var (key, value) in obj)
                if (key is not ("attrs" or "overrides" or "inheritedImplementation"))
                {
                    if (Text(obj["k"]) != null && (NullableRepresentationTypes.IsDeclarationFrameKey(key, Text(obj["k"]), obj)
                        || key == "resolvedMemberParams" || key == ClrMemberResolution.ResolvedMemberReturnKey
                        || key == "argTypes" && ClrBoundNode.IsAny(Text(obj["k"])))) continue;
                    Scan(value, result, types, methods, key == "typeArgs"
                        || key == "elem" && NullableGenericErasure.IsArgumentElementKind(Text(obj["k"])));
                }
        }
    }

    static void RequireNullable(TypeNode type, Variables result)
    {
        if (type is TypeNode.Tv variable) result.Add(variable);
        else if (type is TypeNode.Nullable nullable) RequireNullable(nullable.Of, result);
        else if (type is TypeNode.Oblivious oblivious) RequireNullable(oblivious.Of, result);
    }

    static void Visit(TypeNode type, bool argument, Variables result,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames)
    {
        switch (type)
        {
            case TypeNode.Nullable { Of: TypeNode.Tv variable } when argument:
                result.Add(variable);
                break;
            case TypeNode.Nullable nullable:
                Visit(nullable.Of, false, result, frames);
                break;
            case TypeNode.Oblivious oblivious:
                Visit(oblivious.Of, argument, result, frames);
                break;
            case TypeNode.Projection projection:
                Visit(projection.Of, argument, result, frames);
                break;
            case TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn }:
                break;
            case TypeNode.Fqn { Args: { } arguments } named:
                foreach (var item in arguments) Visit(item, true, result, frames);
                if (frames.TryGetValue(named.Name, out var frame))
                {
                    if (arguments.Length != frame.SourceArity)
                        throw new InvalidOperationException("Constructed type does not match its declared nullable frame");
                    foreach (var index in frame.NullableIndices) RequireNullable(arguments[index], result);
                }
                break;
            case TypeNode.Array array:
                Visit(array.Elem, true, result, frames);
                break;
            case TypeNode.ByRef byRef:
                Visit(byRef.Of, false, result, frames);
                break;
            case TypeNode.Fn function:
                Visit(function.Ret, true, result, frames);
                foreach (var parameter in function.DelegateParams) Visit(parameter, false, result, frames);
                foreach (var context in function.Ctx ?? Array.Empty<TypeNode>()) Visit(context, false, result, frames);
                break;
        }
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
