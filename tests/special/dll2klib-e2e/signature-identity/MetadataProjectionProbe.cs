#nullable enable
using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class MetadataProjectionProbe
{
    private const string Carrier = "DotKt.Runtime.CompilerServices.KotlinStaticCarrierAttribute";
    private const string Nullable = "System.Runtime.CompilerServices.NullableAttribute";

    public static void Run(Assembly tool)
    {
        var attributes = tool.GetType("MetadataAttributes", true)!;
        var find = attributes.GetMethod("Find")!;
        var document = attributes.GetMethod("CarrierDocument")!;
        var validate = attributes.GetMethod("ValidateCarrierTargets")!;
        var owners = tool.GetType("SignatureDecoderSeeds", true)!.GetMethod("DiscoverStaticTypeOwners",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object Attributes(Image image) => Activator.CreateInstance(attributes, image.Reader)!;
        object? Find(object instance, EntityHandle owner) => find.Invoke(instance, [owner, Nullable, false]);
        JsonDocument? Document(object instance, EntityHandle owner) =>
            (JsonDocument?)document.Invoke(instance, [owner, Carrier]);

        using (var image = Build(tool))
        {
            var instance = Attributes(image);
            Parallel.For(0, 256, _ =>
            {
                var result = Find(instance, image.Owner);
                Check(result?.GetType().GetProperty("ByteValue")!.GetValue(result) is byte value && value == 2,
                    "malformed nullable sibling hid the valid attribute");
            });
            using var body = Document(instance, image.Physical);
            Check(body?.RootElement.GetProperty("owner").GetString() == "Fixture.Owner",
                "trusted carrier was lost or a foreign same-name constructor was trusted");
            var bindings = (IDictionary)owners.Invoke(null, [image.Reader])!;
            Check(bindings.Count == 1 && Equals(bindings[image.Physical], image.Owner),
                "static carrier did not retain its exact generic owner");
        }
        using (var image = Build(tool, nullableNamespace: "Other"))
            Check(Find(Attributes(image), image.Owner) is null, "constructor cache leaked across metadata readers");
        foreach (var mode in new[] { (Compiler: false, Generated: true), (Compiler: true, Generated: false) })
        {
            using var image = Build(tool, compiler: mode.Compiler, generated: mode.Generated, malformed: true);
            Check(Document(Attributes(image), image.Physical) is null, "untrusted carrier became active");
            Check(((IDictionary)owners.Invoke(null, [image.Reader])!).Count == 0,
                "untrusted static carrier changed ownership");
        }
        foreach (var mode in new[] { (Copies: 2, Malformed: false), (Copies: 1, Malformed: true) })
        {
            using var image = Build(tool, copies: mode.Copies, malformed: mode.Malformed);
            Invalid(() => Document(Attributes(image), image.Physical));
            Invalid(() => owners.Invoke(null, [image.Reader]));
        }
        using (var image = Build(tool, owner: "Fixture.Missing"))
            Invalid(() => owners.Invoke(null, [image.Reader]));
        using (var image = Build(tool, wrongTarget: true))
            Invalid(() => validate.Invoke(Attributes(image), [Carrier, new[] { HandleKind.TypeDefinition }]));
        Console.WriteLine("Metadata attribute trust, reader isolation and static ownership: PASS");
    }

    private static Image Build(Assembly tool, bool compiler = true, bool generated = true, int copies = 1,
        bool malformed = false, bool wrongTarget = false, string owner = "Fixture.Owner",
        string nullableNamespace = "System.Runtime.CompilerServices")
    {
        var md = new MetadataBuilder();
        StringHandle S(string text) => md.GetOrAddString(text);
        BlobHandle B(params byte[] bytes) => md.GetOrAddBlob(bytes);
        md.AddModule(0, S("ProjectionProbe"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
        var assembly = md.AddAssembly(S("ProjectionProbe"), new Version(1, 0), default, default, 0,
            AssemblyHashAlgorithm.None);
        var runtime = md.AddAssemblyReference(S("System.Runtime"), new Version(10, 0), default, default, 0, default);
        var firstField = MetadataTokens.FieldDefinitionHandle(1);
        var firstMethod = MetadataTokens.MethodDefinitionHandle(1);
        md.AddTypeDefinition(0, default, S("<Module>"), default, firstField, firstMethod);
        var attribute = md.AddTypeDefinition(TypeAttributes.Public, S("DotKt.Runtime.CompilerServices"),
            S("KotlinStaticCarrierAttribute"), default, firstField, firstMethod);
        var semantic = md.AddTypeDefinition(TypeAttributes.Public, S("Fixture"), S("Owner`1"),
            default, firstField, firstMethod);
        md.AddGenericParameter(semantic, 0, S("T"), 0);
        var physical = md.AddTypeDefinition(TypeAttributes.Public, S("Fixture"), S("Carrier"),
            default, firstField, firstMethod);
        MemberReferenceHandle Ctor(EntityHandle parent, params byte[] signature) =>
            md.AddMemberReference(parent, S(".ctor"), B(signature));
        var generatedCtor = Ctor(md.AddTypeReference(runtime, S("System.Runtime.CompilerServices"),
            S("CompilerGeneratedAttribute")), 0x20, 0, 1);
        var metadataCtor = Ctor(md.AddTypeReference(runtime, S("System.Reflection"), S("AssemblyMetadataAttribute")),
            0x20, 2, 1, 0x0e, 0x0e);
        var nullableCtor = Ctor(md.AddTypeReference(runtime, S(nullableNamespace), S("NullableAttribute")),
            0x20, 1, 1, 0x05);
        var foreignCtor = Ctor(md.AddTypeReference(runtime, S("DotKt.Runtime.CompilerServices"),
            S("KotlinStaticCarrierAttribute")), 0x20, 2, 1, 0x0e, 0x1d, 0x05);
        var carrierCtor = Ctor(attribute, 0x20, 2, 1, 0x0e, 0x1d, 0x05);
        if (compiler)
            md.AddCustomAttribute(assembly, metadataCtor, Blob(blob =>
            {
                Text(blob, "DotKt.Compiler");
                Text(blob, "metadata-v1");
            }));
        if (generated) md.AddCustomAttribute(attribute, generatedCtor, B(1, 0, 0, 0));
        md.AddCustomAttribute(semantic, nullableCtor, B(0));
        md.AddCustomAttribute(semantic, nullableCtor, B(1, 0, 2, 0, 0));
        md.AddCustomAttribute(physical, foreignCtor, B(0));
        var carrier = tool.GetType("DotKt.Bir.BirCarrier", true)!;
        var version = (string)carrier.GetField("JsonV1")!.GetRawConstantValue()!;
        var bytes = (byte[])carrier.GetMethod("EncodeBody")!.Invoke(null,
            [version, new JsonObject { ["owner"] = owner }])!;
        for (var index = 0; index < copies; index++)
            md.AddCustomAttribute(wrongTarget ? assembly : physical, carrierCtor,
                malformed ? B(0) : Blob(blob =>
                {
                    Text(blob, version);
                    blob.WriteInt32(bytes.Length);
                    blob.WriteBytes(bytes);
                }));
        var image = new BlobBuilder();
        new MetadataRootBuilder(md).Serialize(image, 0, 0);
        return new Image(MetadataReaderProvider.FromMetadataImage(image.ToImmutableArray()), semantic, physical);

        BlobHandle Blob(Action<BlobBuilder> write)
        {
            var blob = new BlobBuilder();
            blob.WriteUInt16(1);
            write(blob);
            blob.WriteUInt16(0);
            return md.GetOrAddBlob(blob);
        }
    }

    private static void Text(BlobBuilder blob, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        blob.WriteCompressedInteger(bytes.Length);
        blob.WriteBytes(bytes);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Invalid(Action action)
    {
        try { action(); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { return; }
        throw new Exception("malformed current metadata was accepted");
    }

    private sealed record Image(MetadataReaderProvider Provider, TypeDefinitionHandle Owner,
        TypeDefinitionHandle Physical) : IDisposable
    {
        public MetadataReader Reader => Provider.GetMetadataReader();
        public void Dispose() => Provider.Dispose();
    }
}
