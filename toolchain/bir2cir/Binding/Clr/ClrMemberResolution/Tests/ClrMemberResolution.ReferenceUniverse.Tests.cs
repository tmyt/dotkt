using DotKt.Bir;

static partial class ClrMemberResolution
{
    internal static void ReferenceUniverseIsolationSelfTest()
    {
        var previous = _refs;
        var first = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var second = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        try
        {
            foreach (var references in new[] { first, second, first })
            {
                _refs = references;
                var integer = references.ResolveRefType("System.Int32");
                var nullable = references.ResolveRefType("System.Nullable", 1).MakeGenericType(integer);
                if (!DeclaredConstructorSlotMatches(new TypeNode.Nullable(new TypeNode.Fqn("System.Int32")), nullable))
                    throw new InvalidOperationException("Native nullable selection retained another reference universe's Type handle");
                if (SystemArrayMlc() != references.ResolveRefType("System.Array"))
                    throw new InvalidOperationException("Native array selection retained another reference universe's Type handle");
            }
        }
        finally
        {
            first.Dispose(); second.Dispose();
            _refs = previous;
        }
        Console.WriteLine("[reference-universe native selection] self-test OK (interleaved nullable and array declaration identities)");
    }
}
