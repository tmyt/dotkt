using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using DotKt.Bir;
using DotKt.Cir;
using MessagePack;
using A = DotKt.Cir.AttributeArgument;

static class CarrierTransportTests
{
    internal static void Run()
    {
        byte[] Encode(string codec, byte[] value) => BirCarrier.EncodeBody(codec, value,
            (writer, bytes) => writer.WriteBase64StringValue(bytes),
            (ref MessagePackWriter writer, byte[] bytes) => writer.Write(bytes));
        byte[] Decode(string codec, byte[] bytes) => BirCarrier.DecodeBody(codec, bytes,
            (ref Utf8JsonReader reader) => reader.GetBytesFromBase64(),
            (ref MessagePackReader reader) => reader.ReadBytes()!.Value.ToArray());
        var opaque = new byte[4096];
        for (var i = 0; i < opaque.Length; i++) opaque[i] = (byte)(i % 32);
        var raw = Encode(BirCarrier.MessagePackV1, opaque);
        var debug = BirCarrier.SelectTransport(BirCarrier.MessagePackV1, raw, release: false);
        Need(debug.Codec == BirCarrier.MessagePackV1 && ReferenceEquals(debug.Content, raw), "Debug changed its transport");
        var release = BirCarrier.SelectTransport(BirCarrier.MessagePackV1, raw, release: true);
        Need(release.Codec == BirCarrier.MessagePackDeflateV1 && release.Content.Length < raw.Length / 2,
            "Release did not select beneficial compression");
        Need(Decode(release.Codec, release.Content).SequenceEqual(opaque), "Opaque nested bytes changed");
        // Explicit encoding and policy selection use one registered codec. A
        // reader has no target-configuration setting and decodes either form.
        Need(Encode(release.Codec, opaque).SequenceEqual(release.Content), "Different compressed encoders");
        var tiny = Encode(BirCarrier.MessagePackV1, new byte[20]);
        var tinyResult = BirCarrier.SelectTransport(BirCarrier.MessagePackV1, tiny, release: true);
        Need(tinyResult.Codec == BirCarrier.MessagePackV1 && ReferenceEquals(tinyResult.Content, tiny), "Small payload was compressed");
        var noise = new byte[1024]; new Random(1729).NextBytes(noise);
        var incompressible = Encode(BirCarrier.MessagePackV1, noise);
        var noiseResult = BirCarrier.SelectTransport(BirCarrier.MessagePackV1, incompressible, release: true);
        Need(noiseResult.Codec == BirCarrier.MessagePackV1 && ReferenceEquals(noiseResult.Content, incompressible),
            "Release retained an unhelpful compressed body");
        var json = Encode(BirCarrier.JsonV1, opaque);
        Need(BirCarrier.SelectTransport(BirCarrier.JsonV1, json, release: true) is { Codec: BirCarrier.JsonV1, Content: var unchanged }
            && ReferenceEquals(unchanged, json), "JSON comparison transport became a compressed writer");

        // RFC 1951 final stored block containing MessagePack "abc", with no
        // zlib/gzip framing. Independent of our encoder's quality or bit stream.
        var text = BirCarrier.DecodeBody(BirCarrier.MessagePackDeflateV1,
            new byte[] { 1, 4, 0, 251, 255, 0xa3, 0x61, 0x62, 0x63 },
            (ref Utf8JsonReader reader) => reader.GetString(), (ref MessagePackReader reader) => reader.ReadString());
        Need(text == "abc", "Raw DEFLATE interoperability failed");
        for (var length = 0; length < release.Content.Length; length++)
            Refuse(() => Decode(release.Codec, release.Content[..length]));
        Refuse(() => Decode(release.Codec, [..release.Content, 0]));
        Refuse(() => Decode(release.Codec, [..release.Content, ..release.Content]));
        Refuse(() => Decode(release.Codec, [7])); // reserved RFC 1951 block type
        byte[] Wrap(Func<Stream, Stream> wrap, byte[] content)
        {
            using var output = new MemoryStream();
            using (var stream = wrap(output)) stream.Write(content);
            return output.ToArray();
        }
        Refuse(() => Decode(release.Codec, Wrap(stream => new ZLibStream(stream, CompressionLevel.Optimal, true), raw)));
        Refuse(() => Decode(release.Codec, Wrap(stream => new GZipStream(stream, CompressionLevel.Optimal, true), raw)));
        Refuse(() => Decode(release.Codec, Wrap(stream => new DeflateStream(stream, CompressionLevel.Optimal, true), [..raw, 0])));

        // Only explicit compiler carrier declarations receive an emission policy.
        // Their exact selected codec and content must travel together in CIR.
        foreach (var name in new[] { "KotlinInlineAttribute", "KotlinDefaultAttribute", "KotlinTypeAliasAttribute",
                     "KotlinConstructorAdapterAttribute", "KotlinTypeAttribute", "KotlinNullableGenericAttribute",
                     "KotlinCollectionIdentityAttribute", "KotlinSuspendFunctionTypeAttribute", "KotlinSuspendResultAttribute" })
        {
            var slot = name == "KotlinDefaultAttribute" ? 1 : 0;
            var args = new List<A>();
            if (slot == 1) args.Add(new A.Int32Value(3));
            args.Add(new A.TextValue(BirCarrier.MessagePackV1)); args.Add(new A.BytesValue(raw));
            var attr = new AttributeApplication(new TypeNode.Fqn("DotKt.Runtime.CompilerServices." + name), [], args.ToArray());
            var selected = RoundtripMetadata.SelectCarrierTransport(attr, release: true);
            Need(selected.Args[slot] is A.TextValue { Value: BirCarrier.MessagePackDeflateV1 }
                && selected.Args[slot + 1] is A.BytesValue bytes && Decode(release.Codec, bytes.Bytes).SequenceEqual(opaque),
                "Emitted carrier codec/content disagree");
        }
        var user = new AttributeApplication(new TypeNode.Fqn("User.Annotation"), [],
            [new A.TextValue(BirCarrier.MessagePackV1), new A.BytesValue(raw)]);
        Need(ReferenceEquals(user, RoundtripMetadata.SelectCarrierTransport(user, release: true))
            && user.Args[0] is A.TextValue { Value: BirCarrier.MessagePackV1 }, "Inferred a carrier from user arguments");
        Need(!DriverOptions.Parse(["out", "in.bir.json"]).Release, "Default target became Release");
        foreach (var configuration in new[] { "Release", "release", "Debug", "Profile" })
            Need(DriverOptions.Parse(["out", "--configuration", configuration, "in.bir.json"]).Release
                == configuration.Equals("Release", StringComparison.OrdinalIgnoreCase), "Wrong target configuration");
        Console.WriteLine("[carrier transport] self-test OK (raw DEFLATE, declared codecs, fallback, opaque bytes, malformed streams, target policy)");
    }

    static void Need(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Refuse(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is FormatException or MessagePackSerializationException or EndOfStreamException) { return; }
        throw new Exception("Malformed current compressed carrier was accepted");
    }
}
