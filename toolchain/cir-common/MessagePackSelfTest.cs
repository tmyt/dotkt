#nullable enable
using System;
using System.Buffers;
using System.Linq;
using DotKt.Bir;
using MessagePack;

namespace DotKt.Cir;

public static class MessagePackSelfTest
{
    private delegate void WriteValue(ref MessagePackWriter writer);

    public static void Run()
    {
        var integer = new TypeNode.Fqn("System.Int32");
        var types = new TypeNode[] {
            new TypeNode.Fqn("Namespace.Generic`1", [new TypeNode.Tv("method", 0)]) {
                Classifier = new TypeNode.SourceClassifier("Namespace", ["Outer"], "Generic") },
            new TypeNode.Tv("type", 2), new TypeNode.Star(),
            new TypeNode.Projection("out", integer), new TypeNode.Nullable(integer), new TypeNode.Oblivious(integer),
            new TypeNode.Fn(true, integer, [integer], integer, "System.Func", [integer]),
            new TypeNode.Array(integer), TypeNode.Array.General(integer, 1), TypeNode.Array.General(integer, 3),
            new TypeNode.ByRef(integer), new TypeNode.Ptr(integer),
            new TypeNode.FnPtr(5, 1, 1, integer, [integer, integer]), new TypeNode.Mod(true, integer, integer),
        };
        var file = new CirFileDeclaration { FileClass = "N\ud800\0\udfff", HasMain = false, Methods = [], Types = [],
            Fields = types.Select((type, index) => new CirField { Name = "field" + index, Type = type }).ToArray() };
        var bytes = CirFileCodec.Encode(file);
        var copy = CirFileCodec.Decode(bytes);
        if (copy.FileClass != file.FileClass || !copy.Fields.Select(field => field.Type).SequenceEqual(types))
            throw new Exception("CIR binary changed a type frame, classifier, array shape, signature or UTF-16 unit");
        Reject(() => CirFileCodec.Decode(bytes[..^1]));
        Reject(() => CirFileCodec.Decode((byte[])[..bytes, 0]));
        var wrongKind = (byte[])bytes.Clone();
        wrongKind[6] = (byte)'B';
        Reject(() => CirFileCodec.Decode(wrongKind));

        // Broken CURRENT graphs, not previous compiler formats or diagnostic wording.
        Malformed((ref MessagePackWriter writer) => {
            writer.WriteMapHeader(2);
            writer.Write("fileClass"); writer.Write("a");
            writer.Write("fileClass"); writer.Write("b");
        });
        Malformed((ref MessagePackWriter writer) => { writer.WriteMapHeader(0); });
        Malformed((ref MessagePackWriter writer) => {
            writer.WriteMapHeader(5);
            writer.Write("fileClass"); writer.Write("a");
            writer.Write("hasMain"); writer.Write(false);
            writer.Write("fields"); writer.WriteArrayHeader(0);
            writer.Write("types"); writer.WriteArrayHeader(0);
            writer.Write("methods"); writer.WriteArrayHeader(1); writer.WriteNil();
        });
        Malformed((ref MessagePackWriter writer) => {
            writer.WriteMapHeader(1); writer.Write("methods"); writer.WriteArrayHeader(int.MaxValue);
        });
        Console.WriteLine("[CIR MessagePack] self-test OK (typed frames, SZ/general arrays, UTF16, framing, malformed current graphs)");
    }

    private static void Malformed(WriteValue write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        write(ref writer);
        writer.Flush();
        Reject(() => {
            var reader = new MessagePackReader(buffer.WrittenMemory);
            CirMessagePack.Read(ref reader);
        });
    }

    private static void Reject(Action read)
    {
        try { read(); }
        catch (Exception error) when (error is FormatException or MessagePackSerializationException
                                     or System.IO.EndOfStreamException) { return; }
        throw new Exception("Malformed current CIR MessagePack was accepted");
    }
}
