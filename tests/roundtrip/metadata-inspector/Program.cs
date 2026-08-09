using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Klib.Metadata;

const string Ns = "roundtrip.dispatchsurface.";
const string CarrierAttribute = "DotKt.Runtime.CompilerServices.KotlinCompanionAttribute";
const string LateinitAttribute = "DotKt.Runtime.CompilerServices.KotlinLateinitAttribute";
const string StaticCarrierAttribute = "DotKt.Runtime.CompilerServices.KotlinStaticCarrierAttribute";
// A hoisted companion carrier's reserved separator keeps compiler companion types disjoint from ordinary source
// names. Star-projection existential association is metadata-authoritative and does not depend on this spelling.
const string HoistedMarker = "$companion$";

if (args.Length >= 4 && args[0] == "--volatile-consumer")
{
    foreach (var method in args.Skip(3)) VerifyVolatileMethod(args[1], args[2], method);
    Console.WriteLine("volatile field access IL: OK");
    return;
}

if (args.Length != 7)
    throw new ArgumentException(
        "usage: CompanionMetadataInspector <producer.dll> <producer.klib> <companion.bir.json> <companion.cir.json> <ownership.bir.json> <ownership.cir.json> <consumer.dll>");

VerifyLayerBoundary(args[2], args[3]);
VerifyOwnershipLayerBoundary(args[4], args[5]);
VerifyDll(args[0]);
VerifyOwnershipDll(args[0]);
VerifyUnsafeAccessorDll(args[6]);
VerifyKlib(args[1]);
Console.WriteLine("companion + nested ownership semantic BIR / physical CIR / DLL / KLIB linkage: OK");

static void VerifyOwnershipLayerBoundary(string birPath, string cirPath)
{
    static JsonObject Root(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidDataException($"{path} is not a JSON object");
    static JsonArray Types(JsonObject root) => root["types"] as JsonArray
        ?? throw new InvalidDataException("compiler artifact has no types array");
    static string? Text(JsonNode? node) => (node as JsonValue)?.GetValue<string>();
    static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.Select(pair => pair.Value))
                foreach (var nested in Objects(child)) yield return nested;
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                foreach (var nested in Objects(child)) yield return nested;
    }

    var bir = Root(birPath);
    var cir = Root(cirPath);
    var birOwnedTypes = Types(bir).OfType<JsonObject>()
        .Where(t => Text(t["semanticOwner"]) is not null)
        .ToArray();
    Require(birOwnedTypes.Length >= 5, "ownership fixture has an incomplete semantic child set");
    Require(birOwnedTypes.All(t => t["nestedIn"] is null),
        "kotc BIR chose a CLR nested representation instead of carrying semantic ownership");

    var cirTypes = Types(cir).OfType<JsonObject>()
        .ToDictionary(t => Text(t["name"])!, StringComparer.Ordinal);
    foreach (var semantic in birOwnedTypes)
    {
        var name = Text(semantic["name"])!;
        var owner = Text(semantic["semanticOwner"])!;
        Require(cirTypes.TryGetValue(name, out var physical), $"CIR lost semantic child {name}");
        Require(Text(physical!["nestedIn"]) == owner,
            $"bir2cir did not nest {name} under semantic owner {owner}");
        Require(physical["semanticOwner"] is null,
            $"CIR retained BIR-only semanticOwner on {name}");
    }
    Require(cirTypes.Values.Count(t => Text(t["nestedIn"]) == "roundtrip.ownership.Owner" &&
            t["generated"] is JsonValue generated && generated.TryGetValue<bool>(out var isGenerated) && isGenerated) >= 3,
        "local/anonymous/closure synthesis did not preserve Owner as its physical owner");

    var birLocalDeclarations = Objects(bir)
        .Where(node => Text(node["k"]) == "localFun")
        .ToArray();
    Require(birLocalDeclarations.Length >= 5, "ownership fixture has no lexical local-function declarations");
    Require((bir["methods"] as JsonArray ?? []).OfType<JsonObject>()
            .All(method => !Text(method["name"])!.StartsWith("dotkt$local", StringComparison.Ordinal)),
        "kotc flattened a local function onto the file facade");
    var declarationIds = birLocalDeclarations.Select(local => Text(local["id"])!).ToArray();
    Require(declarationIds.Distinct(StringComparer.Ordinal).Count() == declarationIds.Length,
        "BIR local-function declaration ids are not unique within the file");
    Require(birLocalDeclarations.All(local => local["decl"] is JsonObject declaration
            && Text(declaration["sourceName"]) is not null
            && declaration["name"] is null && declaration["semanticOwner"] is null),
        "kotc chose a CLR method name or physical owner for a lexical local function");
    var localUses = Objects(bir)
        .Where(node => Text(node["k"]) is "callLocal" or "localFunRef")
        .ToArray();
    Require(localUses.Length >= birLocalDeclarations.Length
            && localUses.All(use => declarationIds.Contains(Text(use["id"]), StringComparer.Ordinal)),
        "BIR local-function reference is not linked to an explicit declaration id");

    Require(!Objects(cir).Any(node => Text(node["k"]) is "localFun" or "callLocal" or "localFunRef"),
        "CIR retained a BIR-only lexical local-function node");
    var physicalLocalMethods = (cir["methods"] as JsonArray ?? []).OfType<JsonObject>()
        .Concat(cirTypes.Values.SelectMany(type =>
            (type["methods"] as JsonArray ?? []).OfType<JsonObject>()))
        .Where(method => Text(method["name"])?.StartsWith("dotkt$local", StringComparison.Ordinal) == true)
        .ToArray();
    var primaryLocalMethods = physicalLocalMethods
        .Where(method => !Text(method["name"])!.EndsWith("$dotkt_suspend", StringComparison.Ordinal))
        .ToArray();
    Require(primaryLocalMethods.Length == birLocalDeclarations.Length,
        "bir2cir did not materialize exactly one primary CLR MethodDef per lexical local function");

    static bool IsMethodSlot(JsonNode? node, int slot) => node is JsonObject type
        && Text(type["t"]) == "tv" && Text(type["scope"]) == "method"
        && type["i"] is JsonValue index && index.TryGetValue<int>(out var value) && value == slot;
    var sparseLocal = primaryLocalMethods.Single(method =>
        Objects(method).Any(node => Text(node["k"]) == "callStatic" && Text(node["method"]) == "selectSecond"));
    Require(Text(sparseLocal["name"])!.EndsWith("_read", StringComparison.Ordinal)
            && sparseLocal["typeParams"] is JsonArray typeParameters && typeParameters.Count == 1,
        "sparse local-function fixture did not materialize as the expected generic read method");
    Require(IsMethodSlot(sparseLocal["ret"], 0)
            && sparseLocal["params"] is JsonArray sparseParams
            && sparseParams.OfType<JsonObject>().All(parameter => IsMethodSlot(parameter["type"], 0)),
        "sparse lexical generic slots were not compacted into the physical local-method frame");
    var selectSecondCall = Objects(sparseLocal)
        .Single(node => Text(node["k"]) == "callStatic" && Text(node["method"]) == "selectSecond");
    Require(selectSecondCall["sig"] is JsonArray calleeSignature
            && calleeSignature.Count == 2 && IsMethodSlot(calleeSignature[1], 1)
            && selectSecondCall["typeArgs"] is JsonArray suppliedTypeArguments
            && suppliedTypeArguments.Count == 2 && IsMethodSlot(suppliedTypeArguments[1], 0),
        "local-method frame compaction rewrote the callee declaration frame or missed the supplied type argument");
}

static void VerifyOwnershipDll(string path)
{
    using var stream = File.OpenRead(path);
    // Keep the backing stream available because the volatile-prefix assertion below reads a method body as well as
    // metadata. A metadata-only prefetch intentionally makes PE section data unavailable.
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var owner = md.TypeDefinitions.Single(h => StripArities(DefinitionName(md, h)) == "roundtrip.ownership.Owner");
    var ownerDef = md.GetTypeDefinition(owner);
    var ownerChildren = ownerDef.GetNestedTypes().ToArray();
    Require(ownerChildren.Any(h => md.GetString(md.GetTypeDefinition(h).Name).Split('`')[0] == "Nested"),
        "ordinary Kotlin nested class is not a CLR nested TypeDef of Owner");
    var inner = ownerChildren.Single(h => md.GetString(md.GetTypeDefinition(h).Name).Split('`')[0] == "Inner");
    Require(md.GetTypeDefinition(inner).GetGenericParameters().Count == 1,
        "inner class did not re-declare the generic owner's capture slot");

    var shadowOwner = md.TypeDefinitions.Single(h =>
        StripArities(DefinitionName(md, h)) == "roundtrip.ownership.ShadowOwner");
    var shadowEntry = md.GetTypeDefinition(shadowOwner).GetNestedTypes().Single(h =>
        md.GetString(md.GetTypeDefinition(h).Name).Split('`')[0] == "Entry");
    var shadowParameters = md.GetTypeDefinition(shadowEntry).GetGenericParameters()
        .Select(h => md.GetString(md.GetGenericParameter(h).Name))
        .ToArray();
    Require(shadowParameters.Length == 2 && shadowParameters.Distinct(StringComparer.Ordinal).Count() == 2,
        "shadowed inner type-parameter names collapsed distinct CLR generic slots");
    Require(shadowParameters[0].StartsWith("dotkt$outer", StringComparison.Ordinal),
        "captured outer generic slot does not use its compiler-owned physical name");
    Require(ownerChildren.Count(h => HasAttribute(md, h,
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) >= 2,
        "local/anonymous/closure implementation types are not nested under Owner");

    var sparseSuspendOwner = md.TypeDefinitions.Single(h =>
        StripArities(DefinitionName(md, h)) == "roundtrip.ownership.SparseGenericSuspendOwner");
    var sparseSuspendSm = md.GetTypeDefinition(sparseSuspendOwner).GetNestedTypes().Single(h =>
        md.GetString(md.GetTypeDefinition(h).Name).Contains("$sm", StringComparison.Ordinal));
    var sparseSuspendParams = md.GetTypeDefinition(sparseSuspendSm).GetGenericParameters().ToArray();
    Require(sparseSuspendParams.Length == 2,
        "generic-owner suspend lambda did not capture the complete owner parameter prefix");
    Require(md.GetGenericParameter(sparseSuspendParams[0]).GetConstraints().Count > 0,
        "generic-owner suspend lambda dropped the captured owner constraint");

    var ownershipFacade = md.TypeDefinitions.Single(h =>
        StripArities(DefinitionName(md, h)) == "roundtrip.ownership.NestedOwnershipKt");
    var sparseMethodSm = md.GetTypeDefinition(ownershipFacade).GetNestedTypes().Single(h =>
        md.GetString(md.GetTypeDefinition(h).Name).Contains("sparseGenericSuspend", StringComparison.Ordinal));
    Require(md.GetTypeDefinition(sparseMethodSm).GetGenericParameters().Count == 1,
        "sparse generic-method suspend lambda did not compact its free method slot");
    foreach (var lexicalOwnerName in new[] {
                 "roundtrip.ownership.AccessorOwner",
                 "roundtrip.ownership.DefaultInterfaceOwner",
             })
    {
        var lexicalOwner = md.TypeDefinitions.Single(h =>
            StripArities(DefinitionName(md, h)) == lexicalOwnerName);
        Require(md.GetTypeDefinition(lexicalOwner).GetNestedTypes().Any(h => HasAttribute(md, h,
                "System.Runtime.CompilerServices.CompilerGeneratedAttribute")),
            $"accessor/default-interface synthesized type is not nested under {lexicalOwnerName}");
    }

    var valueGetter = ownerDef.GetMethods()
        .Select(h => md.GetMethodDefinition(h))
        .Single(x => md.GetString(x.Name) == "get_value");
    Require((valueGetter.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private,
        "nested private access still widened Owner.get_value");

    var privateDefaultOwner = md.TypeDefinitions.Single(h =>
        StripArities(DefinitionName(md, h)) == "roundtrip.nc.PrivateDefaultOwner");
    var privateDefaultMethods = md.GetTypeDefinition(privateDefaultOwner).GetMethods()
        .Select(h => md.GetMethodDefinition(h))
        .ToArray();
    var secretGetter = privateDefaultMethods.Single(method => md.GetString(method.Name) == "get_secret");
    Require((secretGetter.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private,
        "default carrier widened PrivateDefaultOwner.get_secret");
    Require(!privateDefaultMethods.Any(method =>
            md.GetString(method.Name).StartsWith("dotkt$access$", StringComparison.Ordinal)),
        "producer still exposes a compatibility access bridge for a private default");

    var nonconstFacade = md.TypeDefinitions.Single(h => DefinitionName(md, h) == "roundtrip.nc.NonconstKt");
    var topLevelPrivate = md.GetTypeDefinition(nonconstFacade).GetMethods().Select(md.GetMethodDefinition)
        .Single(method => md.GetString(method.Name) == "privateFromNestedGenericCaller");
    Require((topLevelPrivate.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private,
        "top-level file-private function is still widened to assembly visibility");
    var capturedCaller = md.TypeDefinitions.Single(h =>
        StripArities(DefinitionName(md, h)) == "roundtrip.nc.CapturedGenericNestedAccessorCaller+Entry");
    var capturedAccessor = md.GetTypeDefinition(capturedCaller).GetMethods().Select(md.GetMethodDefinition)
        .Single(method => md.GetString(method.Name).Contains("$privateFromNestedGenericCaller", StringComparison.Ordinal));
    Require((capturedAccessor.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private &&
            (capturedAccessor.Attributes & MethodAttributes.Static) != 0 && capturedAccessor.RelativeVirtualAddress == 0,
        "captured-owner caller did not receive a private static extern UnsafeAccessor");
    Require(md.GetTypeDefinition(capturedCaller).GetGenericParameters().Count == 1,
        "captured-owner UnsafeAccessor host lost its enclosing generic frame");

    var facade = md.TypeDefinitions.Single(h => DefinitionName(md, h) == "roundtrip.ownership.NestedOwnershipKt");
    Require(md.GetTypeDefinition(facade).GetNestedTypes().Any(),
        "top-level local class is not nested under its file facade");
}

static void VerifyUnsafeAccessorDll(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var accessors = md.TypeDefinitions.SelectMany(typeHandle => md.GetTypeDefinition(typeHandle).GetMethods()
        .Select(methodHandle => (TypeHandle: typeHandle, Handle: methodHandle,
            Definition: md.GetMethodDefinition(methodHandle))))
        .Where(pair => pair.Definition.GetCustomAttributes().Select(md.GetCustomAttribute)
            .Any(attribute => AttributeName(md, attribute) ==
                "System.Runtime.CompilerServices.UnsafeAccessorAttribute"))
        .ToArray();
    Require(accessors.Length >= 10, "consumer did not synthesize all private-member UnsafeAccessors");
    foreach (var (_, _, accessor) in accessors)
    {
        Require((accessor.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private,
            "UnsafeAccessor declaration is not private");
        Require((accessor.Attributes & MethodAttributes.Static) != 0 && accessor.RelativeVirtualAddress == 0,
            "UnsafeAccessor declaration is not static extern/bodyless");
        var attributes = accessor.GetCustomAttributes().Select(md.GetCustomAttribute)
            .Select(attribute => AttributeName(md, attribute)).ToHashSet(StringComparer.Ordinal);
        Require(attributes.Contains("System.Runtime.CompilerServices.UnsafeAccessorAttribute"),
            "caller-side extern is missing UnsafeAccessorAttribute");
        Require(attributes.Contains("System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
            "caller-side UnsafeAccessor is missing CompilerGeneratedAttribute");
    }

    var companionTests = md.TypeDefinitions.Single(handle =>
        DefinitionName(md, handle).EndsWith("CompanionStaticRoundtripTests", StringComparison.Ordinal));
    var externalVolatileAccess = md.GetTypeDefinition(companionTests).GetMethods().Single(handle =>
        md.GetString(md.GetMethodDefinition(handle).Name) == "companionBlockStorageStaysOneLogicalMemberOnAGenericOwner");
    var externalVolatileIl = pe.GetMethodBody(md.GetMethodDefinition(externalVolatileAccess).RelativeVirtualAddress)
        .GetILBytes() ?? Array.Empty<byte>();
    Require(ContainsVolatileFieldAccess(externalVolatileIl),
        "consumer access to a referenced @Volatile field omitted the CIR-carried volatile. prefix");

    var secretAccessors = accessors.Where(pair =>
        md.GetString(pair.Definition.Name).Contains("$get_secret", StringComparison.Ordinal)).ToArray();
    Require(secretAccessors.Length == 6, "unexpected get_secret UnsafeAccessor set");
    Require(secretAccessors.Count(pair => md.GetTypeDefinition(pair.TypeHandle).GetGenericParameters().Count == 1) == 5,
        "generic owner slots were not preserved on generic UnsafeAccessor holder types");
    Require(secretAccessors.Any(pair => md.GetTypeDefinition(pair.TypeHandle).GetGenericParameters().Any(handle =>
            md.GetGenericParameter(handle).GetConstraints().Count > 0)),
        "constrained owner UnsafeAccessor lost its generic constraint");
    var identityAccessor = accessors.Single(pair =>
        md.GetString(pair.Definition.Name).Contains("$identity", StringComparison.Ordinal));
    Require(md.GetTypeDefinition(identityAccessor.TypeHandle).GetGenericParameters().Count == 1 &&
            identityAccessor.Definition.GetGenericParameters().Count == 1,
        "owner and method generic frames were not kept in their respective forms on the UnsafeAccessor");
    Require(accessors.Any(pair => md.GetString(pair.Definition.Name)
            .Contains("$privateTopLevelDefaultValue", StringComparison.Ordinal)),
        "top-level private default did not route through UnsafeAccessor");
    var genericCallableAccessor = accessors.Single(pair =>
        md.GetString(pair.Definition.Name).Contains("$secretValue", StringComparison.Ordinal));
    Require(md.GetTypeDefinition(genericCallableAccessor.TypeHandle).GetGenericParameters().Any(handle =>
            md.GetGenericParameter(handle).GetConstraints().Count > 0),
        "generic callable-reference UnsafeAccessor lost its owner constraint");

    var wrappers = md.TypeDefinitions
        .Where(handle => DefinitionName(md, handle).StartsWith("dotkt$unsafe$holder$", StringComparison.Ordinal))
        .SelectMany(handle => md.GetTypeDefinition(handle).GetMethods())
        .Select(md.GetMethodDefinition)
        .Where(method => md.GetString(method.Name).EndsWith("$invoke", StringComparison.Ordinal))
        .ToArray();
    Require(wrappers.Length == 7 && wrappers.All(method =>
            (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Assembly &&
            (method.Attributes & MethodAttributes.Static) != 0 && method.RelativeVirtualAddress != 0),
        "generic UnsafeAccessor holders do not expose only compiler-generated internal wrappers");
}

static void VerifyLayerBoundary(string birPath, string cirPath)
{
    static JsonObject Root(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidDataException($"{path} is not a JSON object");
    static JsonArray Types(JsonObject root) => root["types"] as JsonArray
        ?? throw new InvalidDataException("compiler artifact has no types array");
    static string? Text(JsonNode? node) => (node as JsonValue)?.GetValue<string>();
    static bool Flag(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    static JsonObject Type(JsonArray types, string name) =>
        types.OfType<JsonObject>().Single(t => Text(t["name"]) == name);
    // A CLR TypeDef's arity counts the slots it captured from an enclosing generic type as well as its own.
    static int PhysicalTypeParamCount(JsonObject type) =>
        ((type["capturedTypeParams"] as JsonArray)?.Count ?? 0) +
        ((type["typeParams"] as JsonArray)?.Count ?? 0);
    static string PhysicalMetadataName(JsonArray types, string name)
    {
        var type = Type(types, name);
        if (Text(type["nestedIn"]) is not string parent) return name;
        return PhysicalMetadataName(types, parent) + "+" + name[(name.LastIndexOf('.') + 1)..];
    }

    var bir = Root(birPath);
    var birTypes = Types(bir);
    var semantic = birTypes.OfType<JsonObject>()
        .Where(t => t["kotlinCompanion"] is JsonObject)
        .ToArray();
    Require(semantic.Length == 17, "producer BIR has an unexpected semantic companion declaration set");
    foreach (var companion in semantic)
    {
        var name = Text(companion["name"]);
        Require(name?.Contains(".<companion:", StringComparison.Ordinal) == true,
            $"association-bearing BIR declaration is not semantic: {name}");
        Require(!Flag(companion["mods"] as JsonObject ?? new JsonObject(), "object"),
            $"semantic companion was pre-shaped as a CLR object: {name}");
        Require(!(companion["fields"] as JsonArray ?? []).OfType<JsonObject>()
                .Any(f => Text(f["name"]) == "$INSTANCE"),
            $"semantic companion already contains its physical singleton slot: {name}");
    }

    var cir = Root(cirPath);
    var cirTypes = Types(cir);
    var cirText = cir.ToJsonString();
    Require(!cirText.Contains("<companion:", StringComparison.Ordinal),
        "semantic companion identity survived into CIR");
    Require(!cirText.Contains("\"k\":\"companionValue\"", StringComparison.Ordinal),
        "semantic companionValue survived into CIR");

    foreach (var semanticCompanion in semantic)
    {
        var fact = (JsonObject)semanticCompanion["kotlinCompanion"]!;
        var semanticName = Text(semanticCompanion["name"])!;
        var declarationOwner = semanticName[..semanticName.LastIndexOf(".<companion:", StringComparison.Ordinal)];
        var sourceName = Text(fact["name"])!;
        var owner = Type(cirTypes, declarationOwner);
        // The carrier of a GENERIC owner is hoisted beside it, because a nested TypeDef would redeclare the owner's
        // slots and hold one singleton per closed instantiation. A non-generic owner keeps CLR nesting.
        var ownerPhysicalArity = PhysicalTypeParamCount(owner);
        var hoisted = ownerPhysicalArity > 0;
        var physicalName = hoisted
            ? PhysicalMetadataName(cirTypes, declarationOwner).Replace('+', '$') + HoistedMarker + sourceName
            : declarationOwner + ".$" + sourceName;
        var carrier = Type(cirTypes, physicalName);
        Require(Text(carrier["nestedIn"]) == (hoisted ? null : declarationOwner),
            $"carrier has the wrong CLR ownership for its owner's genericity: {physicalName}");
        Require(PhysicalTypeParamCount(carrier) == 0,
            $"carrier declares generic parameters: {physicalName}");
        Require(Text(carrier["vis"]) == "public" && Flag(carrier, "generated") &&
                Flag(carrier["mods"] as JsonObject ?? new JsonObject(), "object"),
            $"carrier is not a public generated object TypeDef: {physicalName}");
        Require((carrier["fields"] as JsonArray ?? []).OfType<JsonObject>()
                .Count(f => Text(f["name"]) == "$INSTANCE" && Flag(f, "static")) == 1,
            $"carrier has no unique physical $INSTANCE: {physicalName}");

        if (Text(owner["kind"]) != "enum")
        {
            var accessor = (owner["fields"] as JsonArray ?? []).OfType<JsonObject>()
                .Single(f => Text(f["name"]) == sourceName);
            Require(Flag(accessor, "static") && Text(accessor["vis"]) == Text(fact["visibility"]),
                $"source-name accessor lost companion visibility: {declarationOwner}.{sourceName}");
            Require(accessor["init"]?.ToJsonString().Contains("\"name\":\"$INSTANCE\"", StringComparison.Ordinal) == true,
                $"source-name accessor does not load the carrier singleton: {declarationOwner}.{sourceName}");
        }
    }
}

static void VerifyDll(string path)
{
    // The volatile-prefix assertion below needs PE section data as well as metadata.
    using var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(path)));
    var md = pe.GetMetadataReader();
    var carrierDefinition = md.TypeDefinitions.Single(h => DefinitionName(md, h) == CarrierAttribute);
    Require(HasAttribute(md, carrierDefinition, "System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
        "KotlinCompanionAttribute is not a compiler-generated trusted carrier definition");

    var carriers = new List<(TypeDefinitionHandle Handle, string Kind, string Owner, string Name, string Visibility,
        string PhysicalOwner, int PhysicalOwnerArity)>();
    foreach (var handle in md.TypeDefinitions)
    foreach (var attributeHandle in md.GetTypeDefinition(handle).GetCustomAttributes())
    {
        var attribute = md.GetCustomAttribute(attributeHandle);
        if (AttributeName(md, attribute) != CarrierAttribute) continue;
        var blob = md.GetBlobReader(attribute.Value);
        Require(blob.ReadUInt16() == 1, "invalid KotlinCompanionAttribute prolog");
        var version = blob.ReadSerializedString() ?? throw new InvalidDataException("missing carrier version");
        var length = blob.ReadInt32();
        Require(length >= 0 && length <= blob.RemainingBytes - 2, "invalid companion carrier byte array");
        var payload = blob.ReadBytes(length);
        Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0,
            "unexpected named arguments in KotlinCompanionAttribute");
        using var doc = JsonDocument.Parse(BirCarrier.DecodeBody(version, payload).ToJsonString());
        var root = doc.RootElement;
        var carrierKind = root.GetProperty("kind").GetString();
        Require(carrierKind is "nested" or "sidecar", "companion carrier kind is neither nested nor sidecar");
        carriers.Add((handle, carrierKind!,
            root.GetProperty("owner").GetString()!, root.GetProperty("name").GetString()!,
            root.GetProperty("visibility").GetString()!, root.GetProperty("physicalOwner").GetString()!,
            root.GetProperty("physicalOwnerArity").GetInt32()));
    }

    Require(carriers.Count >= 17, "producer DLL has no complete companion carrier set");
    Require(carriers.Any(c => c.Kind == "sidecar") && carriers.Any(c => c.Kind == "nested"),
        "producer DLL does not witness both companion carrier shapes");
    foreach (var carrier in carriers)
    {
        var def = md.GetTypeDefinition(carrier.Handle);
        var parent = def.GetDeclaringType();
        Require((carrier.Kind == "sidecar") == (carrier.PhysicalOwnerArity > 0),
            $"carrier shape does not follow its physical owner's genericity: {DefinitionName(md, carrier.Handle)}");
        if (carrier.Kind == "sidecar")
        {
            Require(parent.IsNil, $"hoisted carrier is nested: {DefinitionName(md, carrier.Handle)}");
            Require(DefinitionName(md, carrier.Handle) ==
                    StripArities(carrier.PhysicalOwner).Replace('+', '$') + HoistedMarker + carrier.Name,
                $"hoisted carrier physical name is not derived from its owner: {DefinitionName(md, carrier.Handle)}");
            Require((def.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public,
                $"hoisted carrier is not public: {DefinitionName(md, carrier.Handle)}");
        }
        else
        {
            Require(!parent.IsNil && StripArities(DefinitionName(md, parent)) == carrier.PhysicalOwner,
                $"carrier parent does not match physicalOwner: {DefinitionName(md, carrier.Handle)}");
            Require(md.GetString(def.Name).Split('`')[0] == "$" + carrier.Name,
                $"carrier physical name is not reserved: {DefinitionName(md, carrier.Handle)}");
            Require((def.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.NestedPublic,
                $"physical carrier is not NestedPublic: {DefinitionName(md, carrier.Handle)}");
        }
        Require(HasAttribute(md, carrier.Handle, "System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
            $"physical carrier is not compiler-generated: {DefinitionName(md, carrier.Handle)}");
        // Whatever its CLR owner, the carrier is one closed type: that is what makes its `$INSTANCE` one singleton.
        Require(def.GetGenericParameters().Count == 0,
            $"carrier declares generic parameters: {DefinitionName(md, carrier.Handle)}");
        Require(def.GetFields().Count(h => IsExactSelfTypedInstance(md, carrier.Handle, h)) == 1,
            $"carrier has no unique public static self-typed $INSTANCE: {DefinitionName(md, carrier.Handle)}");
    }

    var fileFacade = md.TypeDefinitions.Single(h =>
        DefinitionName(md, h) == "roundtrip.companionstatics.CompanionStaticsKt");
    var facadeFields = md.GetTypeDefinition(fileFacade).GetFields()
        .ToDictionary(h => md.GetString(md.GetFieldDefinition(h).Name), StringComparer.Ordinal);
    var privateTop = md.GetFieldDefinition(facadeFields["PRIVATE_TOP_TAG"]);
    Require((privateTop.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Private &&
            (privateTop.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) ==
            (FieldAttributes.Static | FieldAttributes.Literal),
        "private top-level const did not retain private CLR visibility");
    var internalTop = md.GetFieldDefinition(facadeFields["INTERNAL_TOP_TAG"]);
    Require((internalTop.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Assembly &&
            (internalTop.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) ==
            (FieldAttributes.Static | FieldAttributes.Literal),
        "internal top-level const did not retain assembly CLR visibility");

    var counter = md.TypeDefinitions.Single(h => DefinitionName(md, h) == "roundtrip.companionstatics.Counter");
    var counterFields = md.GetTypeDefinition(counter).GetFields()
        .ToDictionary(h => md.GetString(md.GetFieldDefinition(h).Name), StringComparer.Ordinal);
    var tag = md.GetFieldDefinition(counterFields["TAG"]);
    Require((tag.Attributes & (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal)) ==
            (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal) &&
            !tag.GetDefaultValue().IsNil,
        "companion-block const is not a public CLR literal with a metadata constant");
    var later = md.GetFieldDefinition(counterFields["later"]);
    Require((later.Attributes & (FieldAttributes.Public | FieldAttributes.Static)) ==
            (FieldAttributes.Public | FieldAttributes.Static) &&
            HasFieldAttribute(md, counterFields["later"], LateinitAttribute),
        "companion-block lateinit field lost its trusted CLR metadata marker");

    var counterCompanionCarrier = carriers.Single(c =>
        c.Owner == "roundtrip.companionstatics.Counter" && c.Name == "Companion");
    var objectTagHandle = md.GetTypeDefinition(counterCompanionCarrier.Handle).GetFields().Single(h =>
        md.GetString(md.GetFieldDefinition(h).Name) == "OBJECT_TAG");
    var objectTag = md.GetFieldDefinition(objectTagHandle);
    Require((objectTag.Attributes & (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal)) ==
            (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal) &&
            !objectTag.GetDefaultValue().IsNil,
        "companion-object const is not a physical CLR literal with a metadata constant");

    var namedObject = md.TypeDefinitions.Single(h =>
        DefinitionName(md, h) == "roundtrip.companionstatics.NamedConstants");
    var namedConstantHandle = md.GetTypeDefinition(namedObject).GetFields().Single(h =>
        md.GetString(md.GetFieldDefinition(h).Name) == "NAME");
    var namedConstant = md.GetFieldDefinition(namedConstantHandle);
    Require((namedConstant.Attributes & (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal)) ==
            (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal) &&
            !namedConstant.GetDefaultValue().IsNil,
        "named-object const is not a physical CLR literal with a metadata constant");

    var box = md.TypeDefinitions.Single(h =>
        HasCarrierOwner(md, h, StaticCarrierAttribute, "roundtrip.companionstatics.Box"));
    var boxFields = md.GetTypeDefinition(box).GetFields()
        .ToDictionary(h => md.GetString(md.GetFieldDefinition(h).Name), StringComparer.Ordinal);
    var boxCode = md.GetFieldDefinition(boxFields["CODE"]);
    Require((boxCode.Attributes & (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal)) ==
            (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal) &&
            !boxCode.GetDefaultValue().IsNil,
        "generic-owner companion const is not a public CLR literal with a metadata constant");
    var boxLater = md.GetFieldDefinition(boxFields["later"]);
    Require((boxLater.Attributes & (FieldAttributes.Public | FieldAttributes.Static)) ==
            (FieldAttributes.Public | FieldAttributes.Static) &&
            HasFieldAttribute(md, boxFields["later"], LateinitAttribute),
        "generic-owner companion lateinit lost its trusted CLR metadata marker");
    Require(md.GetTypeDefinition(box).GetGenericParameters().Count == 0,
        "generic-owner companion statics were not placed on one non-generic carrier");

    var boxOwner = md.TypeDefinitions.Single(h =>
        StripArities(DefinitionName(md, h)) == "roundtrip.companionstatics.Box");
    foreach (var methodName in new[] { "readPrivateVolatile", "writePrivateVolatile" })
    {
        var method = md.GetTypeDefinition(boxOwner).GetMethods().Single(h =>
            md.GetString(md.GetMethodDefinition(h).Name) == methodName);
        var methodIl = pe.GetMethodBody(md.GetMethodDefinition(method).RelativeVirtualAddress).GetILBytes()
            ?? Array.Empty<byte>();
        Require(ContainsVolatileFieldAccess(methodIl),
            $"UnsafeAccessor rewrite of private companion static omitted volatile. in {methodName}");
    }

    var readVolatile = md.GetTypeDefinition(counter).GetMethods().Single(h =>
        md.GetString(md.GetMethodDefinition(h).Name) == "readVolatileLater");
    var il = pe.GetMethodBody(md.GetMethodDefinition(readVolatile).RelativeVirtualAddress).GetILBytes()
        ?? Array.Empty<byte>();
    Require(ContainsVolatileFieldAccess(il),
        "lateinitGet of an @Volatile field omitted the volatile. prefix");

    var protectedCarrier = carriers.Single(c => c.Owner == Ns + "ProtectedCompanionHost");
    var protectedMethods = md.GetTypeDefinition(protectedCarrier.Handle).GetMethods()
        .Select(md.GetMethodDefinition)
        .Where(m => md.GetString(m.Name) is "marker" or "get_token")
        .ToArray();
    Require(protectedMethods.Length == 2 && protectedMethods.All(m =>
            (m.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public),
        "source-public members of a protected companion were intersected to CLR Family");
    var privateMethods = md.GetTypeDefinition(protectedCarrier.Handle).GetMethods()
        .Select(md.GetMethodDefinition)
        .Where(m => md.GetString(m.Name) == "privateSecret")
        .ToArray();
    Require(privateMethods.Length == 1 && privateMethods.All(m =>
            (m.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private),
        "private companion members lost their own visibility");
}

static void VerifyKlib(string path)
{
    using var archive = ZipFile.OpenRead(path);
    var entry = archive.Entries.Single(e =>
        e.FullName.EndsWith("/package_roundtrip.dispatchsurface/0_dispatchsurface.knm", StringComparison.Ordinal));
    using var stream = entry.Open();
    var fragment = PackageFragment.Parser.ParseFrom(stream);

    VerifyCompanion(fragment, Ns + "NamedCompanionHost", "Key", ["marker", "suspendMarker", "id"]);
    VerifyCompanion(fragment, Ns + "DefaultCompanionHost", "Companion", ["marker"]);
    VerifyCompanion(fragment, Ns + "EnumCompanionHost", "Key", ["marker"]);
    VerifyCompanion(fragment, Ns + "ConstrainedGenericOwnerCompanionHost", "Companion", ["marker", "peek"]);
    VerifyCompanion(fragment, Ns + "NestedCompanionOwners.NestedInterface", "Companion", ["marker"]);
    VerifyCompanion(fragment, Ns + "NestedCompanionOwners.NestedEnum", "Companion", ["marker"]);
    VerifyCompanion(fragment, Ns + "ProtectedCompanionHost", "Shield", ["marker", "suspendMarker"], expectProtected: true);
    VerifyCompanion(fragment, Ns + "GenericSecretHost", "Companion", ["open", "peek", "suspendPeek"]);
    VerifyCompanion(fragment, Ns + "NestedGenericCompanionOwners.Inner", "Key", ["marker"]);
    VerifyCompanion(fragment, Ns + "StarProjectedCompanionHost", "dotkt_star", ["marker"]);
    VerifyCompanion(fragment, Ns + "LateinitGenericCompanionHost", "Companion", ["fill"]);
    VerifyCompanion(fragment, Ns + "ProviderDelegateCompanionHost", "Companion", ["bump", "updatePrivateProvider"]);
    Require(fragment.Package.Property.Count(p => String(fragment, p.Name) == "roundtripDelegatedCounter") == 1,
        "top-level delegated property did not round-trip as one package property");
    Require(fragment.Package.Property.Count(p => String(fragment, p.Name) == "roundtripNullableDelegated") == 1,
        "nullable top-level delegated property did not round-trip as one package property");

    var staticsEntry = archive.Entries.Single(e =>
        e.FullName.EndsWith("/package_roundtrip.companionstatics/0_companionstatics.knm", StringComparison.Ordinal));
    using var staticsStream = staticsEntry.Open();
    var statics = PackageFragment.Parser.ParseFrom(staticsStream);
    var topTag = statics.Package.Property.Single(p => String(statics, p.Name) == "TOP_TAG");
    Require((topTag.Flags & ((1 << 11) | (1 << 13))) == ((1 << 11) | (1 << 13)) &&
            topTag.CompileTimeValue is not null,
        "top-level const lost KLIB IS_CONST/HAS_CONSTANT or its compile-time value");
    Require(!statics.Package.Property.Any(p =>
            String(statics, p.Name) is "PRIVATE_TOP_TAG" or "INTERNAL_TOP_TAG"),
        "non-public top-level const leaked into reference KLIB");
    var counter = Class(statics, "roundtrip.companionstatics.Counter");
    var tag = counter.Property.Single(p => String(statics, p.Name) == "TAG");
    Require((tag.Flags & ((1 << 11) | (1 << 13))) == ((1 << 11) | (1 << 13)) &&
            tag.CompileTimeValue is not null,
        "companion-block const lost KLIB IS_CONST/HAS_CONSTANT or its compile-time value");
    var later = counter.Property.Single(p => String(statics, p.Name) == "later");
    Require((later.Flags & (1 << 12)) != 0 &&
            later.PropertyAnnotation.Any(a => QualifiedName(statics, a.Id) == "kotlin.clr.ClrField") &&
            later.PropertyAnnotation.Any(a => QualifiedName(statics, a.Id) == "kotlin.clr.ClrLateinitField"),
        "companion-block lateinit lost KLIB IS_LATEINIT or its static-property declaration marker");
    var counterCompanion = Class(statics, "roundtrip.companionstatics.Counter.Companion");
    var objectTag = counterCompanion.Property.Single(p => String(statics, p.Name) == "OBJECT_TAG");
    Require((objectTag.Flags & ((1 << 11) | (1 << 13))) == ((1 << 11) | (1 << 13)) &&
            (objectTag.Flags & (1 << 19)) == 0 && objectTag.CompileTimeValue is not null,
        "companion-object const lost its semantic member shape or compile-time value");
    var namedObject = Class(statics, "roundtrip.companionstatics.NamedConstants");
    var namedConstant = namedObject.Property.Single(p => String(statics, p.Name) == "NAME");
    Require((namedConstant.Flags & ((1 << 11) | (1 << 13))) == ((1 << 11) | (1 << 13)) &&
            (namedConstant.Flags & (1 << 19)) == 0 && namedConstant.CompileTimeValue is not null,
        "named-object const lost its semantic member shape or compile-time value");
    var box = Class(statics, "roundtrip.companionstatics.Box");
    var boxCode = box.Property.Single(p => String(statics, p.Name) == "CODE");
    Require((boxCode.Flags & ((1 << 11) | (1 << 13))) == ((1 << 11) | (1 << 13)) &&
            boxCode.CompileTimeValue is not null,
        "generic-owner companion const lost KLIB IS_CONST/HAS_CONSTANT or its compile-time value");
    var boxLater = box.Property.Single(p => String(statics, p.Name) == "later");
    Require((boxLater.Flags & (1 << 12)) != 0 &&
            boxLater.PropertyAnnotation.Any(a => QualifiedName(statics, a.Id) == "kotlin.clr.ClrField") &&
            boxLater.PropertyAnnotation.Any(a => QualifiedName(statics, a.Id) == "kotlin.clr.ClrLateinitField"),
        "generic-owner companion lateinit lost KLIB IS_LATEINIT or its static-property declaration marker");
    var marker = statics.Package.Property.Single(p =>
        String(statics, p.Name) == "marker" &&
        p.ReceiverType is { HasClassName: true } receiver &&
        QualifiedName(statics, receiver.ClassName) == "roundtrip.companionstatics.Tag");
    Require((marker.Flags & (1 << 8)) == 0 && marker.SetterValueParameter is null,
        "companion extension val round-tripped as a writable property");
    var contextStates = statics.Package.Property.Where(p =>
        String(statics, p.Name) == "contextState" &&
        p.ReceiverType is { HasClassName: true } receiver &&
        QualifiedName(statics, receiver.ClassName) == "roundtrip.companionstatics.Tag").ToArray();
    Require(contextStates.Length == 2 && contextStates.All(p => p.ContextParameter.Count == 1),
        "context-overloaded companion properties did not round-trip as two declarations");
    var mutableContextState = contextStates.Single(p =>
        p.ContextParameter[0].Type is { HasClassName: true } context &&
        QualifiedName(statics, context.ClassName) == "roundtrip.companionstatics.MutableTagContext");
    var readOnlyContextState = contextStates.Single(p =>
        p.ContextParameter[0].Type is { HasClassName: true } context &&
        QualifiedName(statics, context.ClassName) == "roundtrip.companionstatics.ReadOnlyTagContext");
    Require(mutableContextState.SetterValueParameter is not null,
        "context-overloaded companion var lost its matching setter");
    Require(readOnlyContextState.SetterValueParameter is null,
        "context-overloaded companion val acquired another overload's setter");

    // Both carrier spellings, and only those: a nested carrier is a `$`-prefixed segment, a hoisted one carries the
    // reserved marker. Other compiler-generated CLR types (a star-projection existential, say) are a different
    // subject and do reach Kotlin metadata today.
    Require(!fragment.Class.Any(c => QualifiedName(fragment, c.FqName).Split('.').Any(p =>
            p.StartsWith('$') || p.Contains(HoistedMarker, StringComparison.Ordinal))),
        "reserved physical companion carrier leaked into Kotlin metadata");
    Require(!statics.Class.Any(c => QualifiedName(statics, c.FqName)
            .Contains("$dotkt_statics", StringComparison.Ordinal)),
        "generic-static implementation carrier leaked into Kotlin metadata");
    Require(!fragment.Class.Any(c => QualifiedName(fragment, c.FqName).Contains("$sm", StringComparison.Ordinal)),
        "compiler-generated suspend state-machine type leaked into Kotlin metadata");
    Require(!fragment.Class.Any(c => QualifiedName(fragment, c.FqName) == Ns + "PrivateCompanionHost.Secret"),
        "private companion class leaked into public KLIB metadata");
    // A hoisted carrier is a public top-level CLR type whatever the Kotlin companion's visibility, so a private or
    // internal companion of a generic owner is the case where the CLR shape and the source shape diverge most.
    foreach (var privateOwnerName in new[] {
                 Ns + "PrivateCompanionHost", Ns + "PrivateGenericCompanionHost",
                 Ns + "InternalGenericCompanionHost" })
        Require(!Class(fragment, privateOwnerName).HasCompanionObjectName,
            $"private companion synthesized a public KLIB companion link: {privateOwnerName}");
    foreach (var hiddenCompanion in new[] {
                 Ns + "PrivateGenericCompanionHost.Hidden", Ns + "InternalGenericCompanionHost.Restricted" })
        Require(!fragment.Class.Any(c => QualifiedName(fragment, c.FqName) == hiddenCompanion),
            $"source-invisible companion of a generic owner reached public KLIB metadata: {hiddenCompanion}");
    VerifyCompanion(fragment, Ns + "ProtectedGenericCompanionHost", "Shielded", ["marker"], expectProtected: true);
    foreach (var genericOwnerCompanion in new[] {
                 Ns + "ConstrainedGenericOwnerCompanionHost.Companion",
                 Ns + "GenericSecretHost.Companion",
                 Ns + "NestedGenericCompanionOwners.Inner.Key" })
        Require(Class(fragment, genericOwnerCompanion).TypeParameter.Count == 0,
            $"a generic owner's physical carrier parameters leaked onto the semantic companion: {genericOwnerCompanion}");

    var ownershipEntry = archive.Entries.Single(e =>
        e.FullName.EndsWith("/package_roundtrip.ownership/0_ownership.knm", StringComparison.Ordinal));
    using var ownershipStream = ownershipEntry.Open();
    var ownership = PackageFragment.Parser.ParseFrom(ownershipStream);
    Require(!ownership.Class.Any(c =>
            QualifiedName(ownership, c.FqName).Split('.').Any(p =>
                p.StartsWith("dotkt$", StringComparison.Ordinal) ||
                p.StartsWith("<>", StringComparison.Ordinal))),
        "nested compiler-generated implementation type leaked into Kotlin metadata");
    Require(!ownership.Package.Function
            .Concat(ownership.Class.SelectMany(c => c.Function))
            .Any(function => String(ownership, function.Name).StartsWith("dotkt$local", StringComparison.Ordinal)),
        "compiler-generated local function leaked into Kotlin metadata");
    Require(!ownership.Class.SelectMany(c => c.Property)
            .Any(property => String(ownership, property.Name) == "__outer"),
        "compiler-generated enclosing-instance field leaked into Kotlin metadata");
    var protectedNested = Class(ownership, "roundtrip.ownership.ProtectedNestedOwner.HiddenNested");
    Require((protectedNested.Flags & 0xE) == 4,
        "protected nested CLR type was projected as a public Kotlin classifier");
    var shadowOwner = Class(ownership, "roundtrip.ownership.ShadowOwner");
    Require(shadowOwner.TypeParameter.Count == 1 &&
            shadowOwner.TypeParameter[0].UpperBound.Count != 0,
        "generic owner constraint was dropped from the projected KLIB");
}

static void VerifyCompanion(
    PackageFragment fragment,
    string ownerName,
    string sourceName,
    IReadOnlyList<string> expectedFunctions,
    bool expectProtected = false)
{
    var owner = Class(fragment, ownerName);
    Require(owner.HasCompanionObjectName && String(fragment, owner.CompanionObjectName) == sourceName,
        $"{ownerName} has no companion_object_name '{sourceName}'");
    Require(owner.NestedClassName.Select(i => String(fragment, i)).Contains(sourceName),
        $"{ownerName} has no nested-class link for '{sourceName}'");
    var companion = Class(fragment, ownerName + "." + sourceName);
    foreach (var function in expectedFunctions)
        Require(companion.Function.Any(f => String(fragment, f.Name) == function),
            $"{ownerName}.{sourceName} lost function '{function}'");
    var expectedVisibility = expectProtected ? 4 : 6;
    Require((companion.Flags & 0xE) == expectedVisibility,
        $"{ownerName}.{sourceName} lost Kotlin visibility");
    foreach (var function in expectedFunctions)
        Require(companion.Function.Any(f => String(fragment, f.Name) == function &&
                (f.Flags & 0xE) == expectedVisibility),
            $"{ownerName}.{sourceName}.{function} lost Kotlin visibility");
    Require(!companion.Property.Any(p => String(fragment, p.Name) == "$INSTANCE"),
        $"{ownerName}.{sourceName} leaked the physical singleton slot");
}

static DotKt.Klib.Metadata.Class Class(PackageFragment fragment, string fqName) =>
    fragment.Class.Single(c => QualifiedName(fragment, c.FqName) == fqName);

static string QualifiedName(PackageFragment fragment, int id)
{
    var parts = new Stack<string>();
    while (id >= 0)
    {
        var item = fragment.QualifiedNames.QualifiedName[id];
        parts.Push(String(fragment, item.ShortName));
        id = item.ParentQualifiedName;
    }
    return string.Join('.', parts);
}

static string String(PackageFragment fragment, int id) => fragment.Strings.String[id];

static bool IsExactSelfTypedInstance(
    MetadataReader md,
    TypeDefinitionHandle carrierHandle,
    FieldDefinitionHandle fieldHandle)
{
    var field = md.GetFieldDefinition(fieldHandle);
    if (md.GetString(field.Name) != "$INSTANCE" ||
        (field.Attributes & FieldAttributes.Static) == 0 ||
        (field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public)
        return false;
    return true;
}

static bool HasAttribute(MetadataReader md, TypeDefinitionHandle handle, string name) =>
    md.GetTypeDefinition(handle).GetCustomAttributes()
        .Select(md.GetCustomAttribute)
        .Any(attribute => AttributeName(md, attribute) == name);

static bool HasFieldAttribute(MetadataReader md, FieldDefinitionHandle handle, string name) =>
    md.GetFieldDefinition(handle).GetCustomAttributes()
        .Select(md.GetCustomAttribute)
        .Any(attribute => AttributeName(md, attribute) == name);

static bool ContainsVolatileFieldAccess(byte[] il)
{
    var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(op => unchecked((ushort)op.Value));
    var volatilePrefix = false;
    for (var offset = 0; offset < il.Length;)
    {
        var first = il[offset++];
        var key = first == 0xfe && offset < il.Length
            ? (ushort)(0xfe00 | il[offset++])
            : first;
        if (!opCodes.TryGetValue(key, out var op)) return false;
        if (volatilePrefix &&
            (op == OpCodes.Ldfld || op == OpCodes.Ldsfld || op == OpCodes.Stfld || op == OpCodes.Stsfld ||
             op == OpCodes.Ldobj || op == OpCodes.Stobj))
            return true;
        volatilePrefix = op == OpCodes.Volatile;

        var operandSize = op.OperandType switch {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
                OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch when offset + 4 <= il.Length =>
                4 + checked(BitConverter.ToInt32(il, offset) * 4),
            _ => -1,
        };
        if (operandSize < 0 || offset + operandSize > il.Length) return false;
        offset += operandSize;
    }
    return false;
}

static void VerifyVolatileMethod(string path, string typeSuffix, string methodName)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var type = md.TypeDefinitions.Single(handle =>
        DefinitionName(md, handle).EndsWith(typeSuffix, StringComparison.Ordinal));
    var method = md.GetTypeDefinition(type).GetMethods().Single(handle =>
        md.GetString(md.GetMethodDefinition(handle).Name) == methodName);
    var il = pe.GetMethodBody(md.GetMethodDefinition(method).RelativeVirtualAddress).GetILBytes()
        ?? Array.Empty<byte>();
    Require(ContainsVolatileFieldAccess(il),
        $"{DefinitionName(md, type)}.{methodName} omitted volatile. on a CLR field access");
}

static bool HasCarrierOwner(MetadataReader md, TypeDefinitionHandle handle, string attributeName, string owner)
{
    foreach (var attribute in md.GetTypeDefinition(handle).GetCustomAttributes().Select(md.GetCustomAttribute))
    {
        if (AttributeName(md, attribute) != attributeName) continue;
        var blob = md.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() != 1) return false;
        var version = blob.ReadSerializedString();
        var length = blob.ReadInt32();
        if (version is null || length < 0 || length > blob.RemainingBytes - 2) return false;
        using var doc = JsonDocument.Parse(BirCarrier.DecodeBody(version, blob.ReadBytes(length)).ToJsonString());
        return blob.ReadUInt16() == 0 && blob.RemainingBytes == 0 &&
            doc.RootElement.TryGetProperty("owner", out var value) && value.GetString() == owner;
    }
    return false;
}

static string AttributeName(MetadataReader md, CustomAttribute attribute)
{
    var ctor = attribute.Constructor;
    return ctor.Kind switch
    {
        HandleKind.MemberReference => TypeName(md, md.GetMemberReference((MemberReferenceHandle)ctor).Parent),
        HandleKind.MethodDefinition => DefinitionName(md,
            md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType()),
        _ => "",
    };
}

static string TypeName(MetadataReader md, EntityHandle handle) => handle.Kind switch
{
    HandleKind.TypeDefinition => DefinitionName(md, (TypeDefinitionHandle)handle),
    HandleKind.TypeReference => ReferenceName(md, (TypeReferenceHandle)handle),
    _ => "",
};

static string DefinitionName(MetadataReader md, TypeDefinitionHandle handle)
{
    var definition = md.GetTypeDefinition(handle);
    var parent = definition.GetDeclaringType();
    return parent.IsNil
        ? Join(md.GetString(definition.Namespace), md.GetString(definition.Name))
        : DefinitionName(md, parent) + "+" + md.GetString(definition.Name);
}

static string ReferenceName(MetadataReader md, TypeReferenceHandle handle)
{
    var reference = md.GetTypeReference(handle);
    return reference.ResolutionScope.Kind == HandleKind.TypeReference
        ? ReferenceName(md, (TypeReferenceHandle)reference.ResolutionScope) + "+" + md.GetString(reference.Name)
        : Join(md.GetString(reference.Namespace), md.GetString(reference.Name));
}

static string Join(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;

static string StripArities(string metadataName) => string.Join('+', metadataName.Split('+').Select(part =>
    part.Contains('`') ? part[..part.IndexOf('`')] : part));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
