using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using RepresentationRole = DotKt.Bir.NullableRepresentationFrame.Role;

// Analysis only: source declarations and bodies are not rewritten here. A body-only demand must not change an
// independently declared virtual slot. Frame materialization and metadata publication consume these facts separately.
static partial class NullableRepresentationDemand
{
    static readonly RepresentationRole[] CompanionRoles = {
        RepresentationRole.Nullable, RepresentationRole.Storage, RepresentationRole.NullableStorage,
    };

    internal sealed class Variables
    {
        readonly Dictionary<(string Scope, RepresentationRole Role), HashSet<int>> _indices = CompanionRoles
            .SelectMany(role => new[] { ("type", role), ("method", role) })
            .ToDictionary(key => key, _ => new HashSet<int>());
        public HashSet<int> Type => For("type", RepresentationRole.Nullable);
        public HashSet<int> Method => For("method", RepresentationRole.Nullable);
        public int Count => _indices.Values.Sum(indices => indices.Count);

        public HashSet<int> For(string scope, RepresentationRole role) => _indices.TryGetValue((scope, role), out var indices)
            ? indices : throw new InvalidOperationException("Unknown representation demand scope or role");

        public void Add(TypeNode.Tv variable, RepresentationRole role = RepresentationRole.Nullable) =>
            For(variable.Scope, role).Add(variable.I);
    }

    internal sealed record MethodDemand(JsonObject Declaration, Variables Signature, Variables Body,
        string ImplementationKey = null, bool IsLocal = false)
    {
        public JsonObject Implementation => ImplementationKey == null ? null : (JsonObject)Declaration[ImplementationKey];
        public JsonArray TypeParameters => (Implementation ?? Declaration)["typeParams"] as JsonArray;
        // An independent nonvirtual implementation owns its generic MethodDef frame, whether static or instance.
        // Only dispatch slots must keep a separate body entry. In particular, an inline instance body must retain
        // its lambda uses instead of exporting a call to a private out-of-line helper.
        public bool CanExtendBodyFrame => ImplementationKey == null
            && !Flag(Declaration["virtual"]) && !Flag(Declaration["override"]) && !Flag(Declaration["abstract"])
            && (Declaration["overrides"] as JsonArray)?.Count is not > 0;

        IEnumerable<int> Indices(RepresentationRole role) => Signature.For("method", role)
            .Concat(CanExtendBodyFrame ? Body.For("method", role) : Enumerable.Empty<int>()).Distinct().OrderBy(i => i);

        public NullableRepresentationFrame Frame => new(TypeParameters?.Count ?? 0,
            Indices(RepresentationRole.Nullable), storageIndices: Indices(RepresentationRole.Storage),
            nullableStorageIndices: Indices(RepresentationRole.NullableStorage));
    }

    static bool Flag(JsonNode node) => (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;

    internal sealed record OwnerDemand(JsonObject Declaration, Variables Signature, Variables Body, List<MethodDemand> Methods,
        bool IsRefCell = false)
    {
        public bool IsTypeDeclaration => IsRefCell || Text(Declaration["kind"]) != null;
        public OwnerDemand CapturedOwner { get; set; }
        public int CaptureOffset { get; set; }

        public NullableRepresentationFrame Frame
        {
            get
            {
                var arity = (Declaration["typeParams"] as JsonArray)?.Count ?? 0;
                var enclosing = CapturedOwner?.Frame;
                // A compiler-owned TypeDef can carry its implementation's nullable owner arguments too.
                // Unlike a virtual method's generic arity, this frame is closed at every constructed-type use.
                IEnumerable<int> Indices(RepresentationRole role) => Signature.For("type", role).Concat(Body.For("type", role))
                    .Concat(Methods.SelectMany(method => method.Signature.For("type", role).Concat(method.Body.For("type", role))))
                    .Concat(enclosing?.Companions.Where(slot => slot.Representation == role)
                        .Select(slot => CaptureOffset + slot.SourceIndex) ?? Enumerable.Empty<int>())
                    .Distinct().OrderBy(i => i).ToArray();
                var frame = new NullableRepresentationFrame(arity, Indices(RepresentationRole.Nullable),
                    storageIndices: Indices(RepresentationRole.Storage),
                    nullableStorageIndices: Indices(RepresentationRole.NullableStorage));
                if (enclosing == null) return frame;
                // Source capture segments can occur after the child's own variables. CLR requires the entire
                // enclosing physical frame first, including its companions, in exactly the enclosing order.
                return frame.WithEnclosingPrefix(enclosing, CaptureOffset);
            }
        }
    }

    public static IReadOnlyList<OwnerDemand> Collect(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, NullableRepresentationFrame> referencedTypes = null,
        IReadOnlyDictionary<string, NullableRepresentationFrame> referencedMethods = null,
        GenericRepresentationPolicy policy = null)
    {
        var rootList = roots.ToArray();
        var localBindings = BindLocalFunctions(rootList);
        var owners = new List<OwnerDemand>();
        void Discover(JsonObject declaration, bool isRefCell = false)
        {
            var methods = (declaration["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(method => new MethodDemand(method, new Variables(), new Variables())).ToList();
            foreach (var key in InheritedMemberKeys)
                foreach (var fact in (declaration[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    methods.Add(new MethodDemand(fact, new Variables(), new Variables(),
                        key == "inheritedClassMethods" ? "inheritedImplementation" : "implementation"));
            void DiscoverLocals(JsonNode node)
            {
                if (node is JsonObject obj)
                {
                    if (Text(obj["k"]) == "localFun" && obj["decl"] is JsonObject local)
                        methods.Add(new MethodDemand(local, new Variables(), new Variables(), IsLocal: true));
                    foreach (var (key, child) in obj)
                        if (key is not ("types" or "refTypes" or "attrs")) DiscoverLocals(child);
                }
                else if (node is JsonArray array)
                    foreach (var child in array) DiscoverLocals(child);
            }
            DiscoverLocals(declaration);
            owners.Add(new OwnerDemand(declaration, new Variables(), new Variables(), methods, isRefCell));
            foreach (var nested in (declaration["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Discover(nested);
            foreach (var cell in (declaration["refTypes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Discover(cell, true);
        }
        foreach (var root in rootList.OfType<JsonObject>()) Discover(root);

        var declarations = owners.Where(owner => owner.IsTypeDeclaration)
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
                if (owner.IsTypeDeclaration)
                    typeFrames[Text(owner.Declaration["name"])] = owner.Frame;
                foreach (var method in owner.Methods)
                    if (Text(method.Declaration[DeclarationIdentityBinding.Key]) is string id)
                        methodFrames[id] = method.Frame;
            }
            var localDeclarations = owners.SelectMany(owner => owner.Methods).Where(method => method.IsLocal)
                .ToDictionary(method => method.Declaration, method => method.Frame);
            var localFrames = localBindings.ToDictionary(pair => pair.Key, pair => localDeclarations[pair.Value]);
            var before = Count(owners);
            foreach (var owner in owners)
            {
                foreach (var key in new[] { "base", "interfaces", "typeParams" })
                    Scan(owner.Declaration[key], owner.Signature, typeFrames, methodFrames, localFrames, policy: policy);
                if (owner.IsRefCell) Scan(owner.Declaration["elem"], owner.Signature, typeFrames, methodFrames, localFrames, policy: policy);
                foreach (var key in new[] { "fields", "properties" })
                    foreach (var slot in (owner.Declaration[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        Scan(slot["type"], owner.Signature, typeFrames, methodFrames, localFrames, policy: policy);
                        Scan(slot["init"], owner.Body, typeFrames, methodFrames, localFrames, policy: policy);
                    }
                foreach (var ctor in (owner.Declaration["ctors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    Scan(ctor["params"], owner.Signature, typeFrames, methodFrames, localFrames, policy: policy);
                    Scan(ctor["body"], owner.Body, typeFrames, methodFrames, localFrames, policy: policy);
                }
                foreach (var method in owner.Methods)
                {
                    foreach (var key in new[] { "params", "ret" })
                        Scan(method.Declaration[key], method.Signature, typeFrames, methodFrames, localFrames, policy: policy);
                    // Implementation constraints are declaration-owned; only ordinary declarations' constraints
                    // refer to this owner's variables. An inherited fact's instantiated params/ret are above.
                    if (method.ImplementationKey == null)
                        Scan(method.TypeParameters, method.Signature, typeFrames, methodFrames, localFrames, policy: policy);
                    else
                    {
                        var implementationConstraints = new Variables();
                        Scan(method.TypeParameters, implementationConstraints, typeFrames, methodFrames, localFrames, policy: policy);
                        // The method frame is shared by this inherited fact and its selected implementation.
                        // Owner variables in those same constraints still belong to the implementation owner.
                        foreach (var role in CompanionRoles)
                            method.Signature.For("method", role).UnionWith(implementationConstraints.For("method", role));
                    }
                    Scan(method.Declaration["body"], method.Body, typeFrames, methodFrames, localFrames, policy: policy);
                }
            }
            changed = Count(owners) != before;
        } while (changed);
        return owners;
    }

    internal static readonly string[] InheritedMemberKeys = {
        KotlinPropertyAccessors.InheritedDefaultMethodsKey,
        KotlinPropertyAccessors.InheritedDefaultAccessorsKey,
        "inheritedClassMethods",
    };

    internal static IReadOnlyDictionary<JsonObject, JsonObject> BindLocalFunctions(IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<JsonObject, JsonObject>();
        foreach (var root in roots)
        {
            var declarations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var uses = new List<JsonObject>();
            void Walk(JsonNode node)
            {
                if (node is JsonObject obj)
                {
                    if (Text(obj["k"]) is "localFun" or "callLocal" or "localFunRef")
                    {
                        var id = Text(obj["id"]) ?? throw new InvalidOperationException("Local function edge has no lexical identity");
                        uses.Add(obj);
                        if (Text(obj["k"]) == "localFun" && obj["decl"] is JsonObject declaration
                            && !declarations.TryAdd(id, declaration))
                            throw new InvalidOperationException("Duplicate local function identity: " + id);
                    }
                    foreach (var (key, child) in obj) if (key != "attrs") Walk(child);
                }
                else if (node is JsonArray array) foreach (var child in array) Walk(child);
            }
            Walk(root);
            foreach (var use in uses)
                result.Add(use, declarations[Text(use["id"])]);
        }
        return result;
    }

    static int Count(IEnumerable<OwnerDemand> owners) => owners.Sum(owner =>
        owner.Signature.Count + owner.Body.Count + owner.Methods.Sum(method => method.Signature.Count + method.Body.Count));

    static void Scan(JsonNode node, Variables result,
        IReadOnlyDictionary<string, NullableRepresentationFrame> types,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methods,
        IReadOnlyDictionary<JsonObject, NullableRepresentationFrame> localFrames, bool argument = false,
        GenericRepresentationPolicy policy = null, bool storage = false)
    {
        if (node == null) return;
        if (TypeJson.Read(node) is TypeNode type)
        {
            Visit(type, argument, result, types, policy, storage);
            return;
        }
        if (node is JsonArray array)
        {
            foreach (var item in array) Scan(item, result, types, methods, localFrames, argument, policy, storage);
        }
        else if (node is JsonObject obj)
        {
            if (localFrames.TryGetValue(obj, out var localFrame))
            {
                var arguments = Text(obj["k"]) == "localFun"
                    ? obj["decl"]?["_syntheticTypeArgs"] as JsonArray : obj["typeArgs"] as JsonArray;
                if (localFrame.RequiresMetadata)
                {
                    if (arguments?.Count != localFrame.SourceArity)
                        throw new InvalidOperationException("Local function arguments do not match their declaration frame");
                    foreach (var slot in localFrame.Companions)
                    {
                        var origin = TypeJson.Read(arguments[slot.SourceIndex]);
                        // Method origins also include the local function's own parameters; only actual call
                        // arguments relate those variables to an enclosing method. Owner origins are lexical.
                        if (Text(obj["k"]) != "localFun" || origin is TypeNode.Tv { Scope: "type" })
                            RequireRepresentation(origin, slot.Representation, result);
                    }
                }
                if (Text(obj["k"]) == "localFun") return;
            }
            if (Text(obj["k"]) != null && Text(obj[DeclarationIdentityBinding.Key]) is string id
                && methods.TryGetValue(id, out var frame) && frame.RequiresMetadata)
            {
                if (obj["typeArgs"] is not JsonArray arguments || arguments.Count != frame.SourceArity)
                    throw new InvalidOperationException("Generic call does not match its declared nullable frame");
                foreach (var slot in frame.Companions)
                    RequireRepresentation(TypeJson.Read(arguments[slot.SourceIndex]), slot.Representation, result);
            }
            foreach (var (key, value) in obj)
                if (key is not ("attrs" or "overrides" or "inheritedImplementation"))
                {
                    if (Text(obj["k"]) != null && (NullableRepresentationTypes.IsDeclarationFrameKey(key, Text(obj["k"]), obj)
                        || key == "resolvedMemberParams" || key == ClrMemberResolution.ResolvedMemberReturnKey
                        || key == "argTypes" && ClrBoundNode.IsAny(Text(obj["k"])))) continue;
                    var storageElement = policy?.IsStorageElement(Text(obj["k"]), key) == true;
                    Scan(value, result, types, methods, localFrames, key == "typeArgs" || storageElement
                        || key == "elem" && NullableGenericErasure.IsArgumentElementKind(Text(obj["k"])), policy, storageElement);
                }
        }
    }

    static void RequireRepresentation(TypeNode type, RepresentationRole role, Variables result)
    {
        if (type is TypeNode.Tv variable) result.Add(variable, role);
        else if (type is TypeNode.Nullable nullable)
            RequireRepresentation(nullable.Of, role == RepresentationRole.Storage ? RepresentationRole.NullableStorage : role, result);
        else if (type is TypeNode.Oblivious oblivious) RequireRepresentation(oblivious.Of, role, result);
        else if (type is TypeNode.Projection projection) RequireRepresentation(projection.Of, role, result);
    }

    static void Visit(TypeNode type, bool argument, Variables result,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, GenericRepresentationPolicy policy, bool storage = false)
    {
        switch (type)
        {
            case TypeNode.Nullable { Of: TypeNode.Tv variable } when argument:
                result.Add(variable, storage ? RepresentationRole.NullableStorage : RepresentationRole.Nullable);
                break;
            case TypeNode.Tv variable when argument && storage:
                result.Add(variable, RepresentationRole.Storage);
                break;
            case TypeNode.Nullable nullable:
                if (argument) RequireRepresentation(nullable.Of,
                    storage ? RepresentationRole.NullableStorage : RepresentationRole.Nullable, result);
                Visit(nullable.Of, false, result, frames, policy);
                break;
            case TypeNode.Oblivious oblivious:
                Visit(oblivious.Of, argument, result, frames, policy, storage);
                break;
            case TypeNode.Projection projection:
                Visit(projection.Of, argument, result, frames, policy, storage);
                break;
            case TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn }:
                break;
            case TypeNode.Fqn { Args: { } arguments } named:
                foreach (var item in arguments) Visit(item, true, result, frames, policy,
                    policy?.UsesStorageArguments(named.Name) == true);
                if (frames.TryGetValue(named.Name, out var frame))
                {
                    if (arguments.Length != frame.SourceArity)
                        throw new InvalidOperationException($"Constructed type '{named.Name}' has {arguments.Length} arguments, "
                            + $"but its declared nullable frame has source arity {frame.SourceArity}");
                    foreach (var slot in frame.Companions)
                        RequireRepresentation(arguments[slot.SourceIndex],
                            policy?.ApplicationRole(named.Name, slot.Representation) ?? slot.Representation, result);
                }
                break;
            case TypeNode.Array array:
                Visit(array.Elem, true, result, frames, policy);
                break;
            case TypeNode.ByRef byRef:
                Visit(byRef.Of, false, result, frames, policy);
                break;
            case TypeNode.Fn function:
                Visit(function.Ret, true, result, frames, policy);
                foreach (var parameter in function.DelegateParams) Visit(parameter, false, result, frames, policy);
                foreach (var context in function.Ctx ?? Array.Empty<TypeNode>()) Visit(context, false, result, frames, policy);
                break;
        }
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
