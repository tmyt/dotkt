using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Klib.Metadata;

const string Ns = "roundtrip.dispatchsurface.";
const string CarrierAttribute = "DotKt.Runtime.CompilerServices.KotlinCompanionAttribute";

if (args.Length != 4)
    throw new ArgumentException(
        "usage: CompanionMetadataInspector <producer.dll> <producer.klib> <producer.bir.json> <producer.cir.json>");

VerifyLayerBoundary(args[2], args[3]);
VerifyDll(args[0]);
VerifyKlib(args[1]);
Console.WriteLine("companion semantic BIR + nested physical CIR + metadata carrier + KLIB linkage: OK");

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

    var bir = Root(birPath);
    var birTypes = Types(bir);
    var semantic = birTypes.OfType<JsonObject>()
        .Where(t => t["kotlinCompanion"] is JsonObject)
        .ToArray();
    Require(semantic.Length == 9, "producer BIR has an unexpected semantic companion declaration set");
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
        var physicalName = declarationOwner + ".$" + sourceName;
        var carrier = Type(cirTypes, physicalName);
        Require(Text(carrier["nestedIn"]) == declarationOwner,
            $"carrier is not an ordinary nested type of its declaration owner: {physicalName}");
        Require(Text(carrier["vis"]) == "public" && Flag(carrier, "generated") &&
                Flag(carrier["mods"] as JsonObject ?? new JsonObject(), "object"),
            $"carrier is not a public generated object TypeDef: {physicalName}");
        Require((carrier["fields"] as JsonArray ?? []).OfType<JsonObject>()
                .Count(f => Text(f["name"]) == "$INSTANCE" && Flag(f, "static")) == 1,
            $"carrier has no unique physical $INSTANCE: {physicalName}");

        var owner = Type(cirTypes, declarationOwner);
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
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
    var md = pe.GetMetadataReader();
    var carrierDefinition = md.TypeDefinitions.Single(h => DefinitionName(md, h) == CarrierAttribute);
    Require(HasAttribute(md, carrierDefinition, "System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
        "KotlinCompanionAttribute is not a compiler-generated trusted carrier definition");

    var carriers = new List<(TypeDefinitionHandle Handle, string Owner, string Name, string Visibility,
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
        Require(root.GetProperty("kind").GetString() == "nested", "companion carrier kind is not nested");
        carriers.Add((handle,
            root.GetProperty("owner").GetString()!, root.GetProperty("name").GetString()!,
            root.GetProperty("visibility").GetString()!, root.GetProperty("physicalOwner").GetString()!,
            root.GetProperty("physicalOwnerArity").GetInt32()));
    }

    Require(carriers.Count >= 9, "producer DLL has no complete companion carrier set");
    foreach (var carrier in carriers)
    {
        var def = md.GetTypeDefinition(carrier.Handle);
        var parent = def.GetDeclaringType();
        Require(!parent.IsNil && StripArities(DefinitionName(md, parent)) == carrier.PhysicalOwner,
            $"carrier parent does not match physicalOwner: {DefinitionName(md, carrier.Handle)}");
        Require(md.GetString(def.Name).Split('`')[0] == "$" + carrier.Name,
            $"carrier physical name is not reserved: {DefinitionName(md, carrier.Handle)}");
        Require((def.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.NestedPublic,
            $"physical carrier is not NestedPublic: {DefinitionName(md, carrier.Handle)}");
        Require(HasAttribute(md, carrier.Handle, "System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
            $"physical carrier is not compiler-generated: {DefinitionName(md, carrier.Handle)}");
        Require(def.GetGenericParameters().Count == carrier.PhysicalOwnerArity,
            $"carrier capture arity does not match physical owner: {DefinitionName(md, carrier.Handle)}");
        foreach (var gpHandle in def.GetGenericParameters())
        {
            var gp = md.GetGenericParameter(gpHandle);
            Require(gp.GetConstraints().Count == 0 &&
                    (gp.Attributes & GenericParameterAttributes.SpecialConstraintMask) == 0,
                $"generated companion carrier capture inherited an owner constraint: {DefinitionName(md, carrier.Handle)}");
        }
        Require(def.GetFields().Count(h => IsExactSelfTypedInstance(md, carrier.Handle, h)) == 1,
            $"carrier has no unique public static self-typed $INSTANCE: {DefinitionName(md, carrier.Handle)}");
    }

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
    VerifyCompanion(fragment, Ns + "ConstrainedGenericOwnerCompanionHost", "Companion", ["marker"]);
    VerifyCompanion(fragment, Ns + "NestedCompanionOwners.NestedInterface", "Companion", ["marker"]);
    VerifyCompanion(fragment, Ns + "NestedCompanionOwners.NestedEnum", "Companion", ["marker"]);
    VerifyCompanion(fragment, Ns + "ProtectedCompanionHost", "Shield", ["marker", "suspendMarker"], expectProtected: true);

    Require(!fragment.Class.Any(c => QualifiedName(fragment, c.FqName).Split('.').Any(p => p.StartsWith('$'))),
        "reserved physical companion carrier leaked into Kotlin metadata");
    Require(!fragment.Class.Any(c => QualifiedName(fragment, c.FqName) == Ns + "PrivateCompanionHost.Secret"),
        "private companion class leaked into public KLIB metadata");
    var privateOwner = Class(fragment, Ns + "PrivateCompanionHost");
    Require(!privateOwner.HasCompanionObjectName, "private companion synthesized a public KLIB companion link");
    var constrainedGenericCompanion = Class(fragment, Ns + "ConstrainedGenericOwnerCompanionHost.Companion");
    Require(constrainedGenericCompanion.TypeParameter.Count == 0,
        "constrained owner's physical carrier capture parameters leaked onto the semantic companion");
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
