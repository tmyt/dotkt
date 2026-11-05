using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Toolchain;

sealed partial class Emitter
{
    const ushort PortablePdbFormatVersion = 0x0100;
    readonly string _portablePdbPath;
    MethodBase _sourceMethod;
    readonly Dictionary<MethodBase, List<SourcePoint>> _sourcePoints = new(ReferenceEqualityComparer.Instance);

    readonly record struct SourcePoint(int Offset, string File, int Line, int Column, int EndLine, int EndColumn)
    {
        public bool Hidden => File == null;
        public bool IsRepresentable => Offset is >= 0 and < 0x20000000
            && (Hidden || (Line is > 0 and < 0x20000000 and not 0xfeefee
                && EndLine is > 0 and < 0x20000000 and not 0xfeefee
                && Column is > 0 and < 0x10000 && EndColumn is > 0 and < 0x10000
                && (EndLine > Line || (EndLine == Line && EndColumn > Column))));
    }

    void RecordSourcePoint(JsonElement node)
    {
        if (_portablePdbPath == null) return;
        var point = node.TryGetProperty("span", out var span)
            ? new SourcePoint(_il.ILOffset, span.GetProperty("f").GetString(),
                span.GetProperty("l").GetInt32(), span.GetProperty("c").GetInt32(),
                span.GetProperty("el").GetInt32(), span.GetProperty("ec").GetInt32())
            : new SourcePoint(_il.ILOffset, null, 0, 0, 0, 0);
        // For example, a Kotlin line can be wider than Portable PDB's 16-bit column range.
        if (!point.IsRepresentable) return;
        if (!_sourcePoints.TryGetValue(_sourceMethod, out var points))
            _sourcePoints.Add(_sourceMethod, points = []);
        if (points.Count > 0 && points[^1].Offset == point.Offset)
        {
            if (point == points[^1] || point.Hidden) return;
            if (points[^1].Hidden)
            {
                points[^1] = point;
                return;
            }
            // Two different authored boundaries can coincide (e.g. an invocation followed immediately
            // by its inlined body). Give the earlier one a real breakpoint target without changing the stack.
            _il.Emit(OpCodes.Nop);
            point = point with { Offset = _il.ILOffset };
        }
        points.Add(point);
    }

    void WritePortablePdb(byte[] pe, ImmutableArray<int> rows, MethodDefinitionHandle entryPoint, BlobContentId id)
    {
        var symbols = new MetadataBuilder();
        var documents = new Dictionary<string, DocumentHandle>(StringComparer.Ordinal);
        DocumentHandle Document(string path)
        {
            if (!documents.TryGetValue(path, out var handle))
                documents.Add(path, handle = symbols.AddDocument(symbols.GetOrAddDocumentName(path), default, default, default));
            return handle;
        }

        // The public persisted emitter groups MarkSequencePoint by document and loses IL ordering when a
        // method moves back and forth between source files. Encode the ordered source points independently;
        // the finalized image supplies the actual MethodDef rows and local signatures without re-emitting IL.
        using var image = new PEReader(new MemoryStream(pe, writable: false));
        var assembly = image.GetMetadataReader();
        var byToken = _sourcePoints.ToDictionary(pair => pair.Key.MetadataToken, pair => pair.Value);
        foreach (var method in assembly.MethodDefinitions)
        {
            if (byToken.TryGetValue(MetadataTokens.GetToken(method), out var methodPoints))
            {
                var body = image.GetMethodBody(assembly.GetMethodDefinition(method).RelativeVirtualAddress);
                var codeSize = body.GetILReader().Length;
                var points = methodPoints.TakeWhile(p => p.Offset < codeSize).ToArray();
                var file = points.FirstOrDefault(p => !p.Hidden).File;
                if (file != null)
                {
                    var first = Document(file);
                    var multiple = points.Any(p => !p.Hidden && p.File != file);
                    var blob = EncodePoints(points, body.LocalSignature, first, multiple, Document);
                    symbols.AddMethodDebugInformation(multiple ? default : first, symbols.GetOrAddBlob(blob));
                    continue;
                }
            }
            // MethodDebugInformation is indexed by MethodDef, including bodyless and synthetic methods.
            symbols.AddMethodDebugInformation(default, default);
        }
        var builder = new PortablePdbBuilder(symbols, rows, entryPoint, _ => id);
        var pdb = new BlobBuilder();
        builder.Serialize(pdb);
        AtomicFile.Write(_portablePdbPath, stream => pdb.WriteContentTo(stream));
    }

    // ECMA Portable PDB sequence-points blob: IL offsets are strictly increasing; document records do
    // not reset either offset or source deltas. Hidden points never advance the visible source origin.
    static BlobBuilder EncodePoints(SourcePoint[] points, StandaloneSignatureHandle locals, DocumentHandle first,
        bool multiple, Func<string, DocumentHandle> document)
    {
        var blob = new BlobBuilder();
        blob.WriteCompressedInteger(MetadataTokens.GetRowNumber(locals));
        if (multiple) blob.WriteCompressedInteger(MetadataTokens.GetRowNumber(first));
        var active = first;
        int previousOffset = 0, previousLine = 0, previousColumn = 0;
        foreach (var point in points)
        {
            if (!point.Hidden)
            {
                var next = document(point.File);
                if (next != active)
                {
                    blob.WriteCompressedInteger(0);
                    blob.WriteCompressedInteger(MetadataTokens.GetRowNumber(next));
                    active = next;
                }
            }
            blob.WriteCompressedInteger(point.Offset - previousOffset);
            previousOffset = point.Offset;
            if (point.Hidden)
            {
                blob.WriteUInt16(0);
                continue;
            }
            var lines = point.EndLine - point.Line;
            var columns = point.EndColumn - point.Column;
            blob.WriteCompressedInteger(lines);
            if (lines == 0) blob.WriteCompressedInteger(columns);
            else blob.WriteCompressedSignedInteger(columns);
            if (previousLine == 0)
            {
                blob.WriteCompressedInteger(point.Line);
                blob.WriteCompressedInteger(point.Column);
            }
            else
            {
                blob.WriteCompressedSignedInteger(point.Line - previousLine);
                blob.WriteCompressedSignedInteger(point.Column - previousColumn);
            }
            previousLine = point.Line;
            previousColumn = point.Column;
        }
        return blob;
    }
}
