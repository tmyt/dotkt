#nullable enable
using System;
using System.Buffers;
using System.Linq;
using MessagePack;

namespace DotKt.Bir;

public static class TypeNodeCarrierSelfTest
{
    public static void Run()
    {
        var integer = new TypeNode.Fqn("System.Int32");
        var shapes = new TypeNode[] {
            new TypeNode.Fqn("N.Outer.Generic", [new TypeNode.Tv("method", 0)]) {
                Classifier = new TypeNode.SourceClassifier("N", ["Outer"], "Generic") },
            new TypeNode.Fqn("Empty", []), new TypeNode.Fqn("Absent"),
            new TypeNode.Tv("type", 2), new TypeNode.Star(), new TypeNode.Projection("in", integer),
            new TypeNode.Nullable(integer), new TypeNode.Oblivious(integer),
            new TypeNode.Fn(true, integer, [integer], integer, "System.Func", [integer]),
            new TypeNode.Array(integer), TypeNode.Array.General(integer, 1), TypeNode.Array.General(integer, 3),
            new TypeNode.ByRef(integer), new TypeNode.Ptr(integer),
            new TypeNode.FnPtr(5, 1, 1, integer, [integer, integer]), new TypeNode.Mod(true, integer, integer),
        };
        foreach (var codec in new[] { BirCarrier.JsonV1, BirCarrier.MessagePackV1 })
        foreach (var shape in shapes)
        {
            var bytes = TypeNodeCarrier.Encode(codec, shape);
            var copy = TypeNodeCarrier.Decode(codec, bytes);
            if (!TypeNodeDataEquality.Equal(copy, shape))
                throw new Exception("TypeNode carrier changed a structural type, classifier, frame or optional vector");
            if (codec == BirCarrier.JsonV1 && !bytes.AsSpan().SequenceEqual(BirCarrier.EncodeBody(codec, shape, TypeNode.WriteJson)))
                throw new Exception("Explicit JSON type codec changed its current encoding");
            if (codec == BirCarrier.MessagePackV1)
            {
                var expected = new ArrayBufferWriter<byte>();
                var writer = new MessagePackWriter(expected);
                TypeNodeMessagePack.Write(ref writer, shape);
                writer.Flush();
                if (!bytes.AsSpan().SequenceEqual(expected.WrittenSpan)) throw new Exception("TypeNode carrier inserted a second binary encoding");
            }
            Refuse(() => TypeNodeCarrier.Decode(codec, [..bytes, 0]));
        }
        var utf16 = new TypeNode.Fqn("N\ud800\0\udfff");
        if (TypeNodeCarrier.Decode(TypeNodeCarrier.Codec, TypeNodeCarrier.Encode(TypeNodeCarrier.Codec, utf16)) != utf16)
            throw new Exception("TypeNode MessagePack lost UTF-16 code units");
        var original = new TypeNode.Fqn("Owned", [integer]);
        var encoded = TypeNodeCarrier.Encode(TypeNodeCarrier.Codec, original);
        var first = (TypeNode.Fqn)TypeNodeCarrier.Decode(TypeNodeCarrier.Codec, encoded);
        var second = (TypeNode.Fqn)TypeNodeCarrier.Decode(TypeNodeCarrier.Codec, encoded);
        first.Args![0] = new TypeNode.Star();
        if (second.Args![0] != integer || original.Args![0] != integer)
            throw new Exception("Decoded type payloads shared mutable vector occurrences");
        Console.WriteLine("[TypeNode carriers] self-test OK (JSON+MessagePack, shared generated types, all variants, frames, UTF16, independent vectors)");
    }

    static void Refuse(Action decode)
    {
        try { decode(); }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException) { return; }
        throw new Exception("TypeNode carrier accepted trailing current payload bytes");
    }
}
