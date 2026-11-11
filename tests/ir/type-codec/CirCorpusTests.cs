using DotKt.Cir;

internal static class CirCorpusTests
{
    public static void Run(string manifest)
    {
        var paths = File.ReadAllLines(manifest).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new Exception("No current compiler CIR corpus was selected");
        foreach (var path in paths)
        {
            var original = DeclarationJson.ReadFile(path);
            var copy = CirFileCodec.Decode(CirFileCodec.Encode(original));
            // Canonical JSON is an independent value comparison, never the binary production path.
            if (DeclarationJson.Write(copy) != DeclarationJson.Write(original))
                throw new Exception("CIR MessagePack changed the actual compiler graph: " + path);
        }
        Console.WriteLine($"CIR MessagePack: {paths.Length} actual compiler graphs preserved all schema fields");
    }
}
