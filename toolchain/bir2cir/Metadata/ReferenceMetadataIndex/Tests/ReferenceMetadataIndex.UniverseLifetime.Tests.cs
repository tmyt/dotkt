using DotKt.Bir;

sealed partial class ReferenceMetadataIndex
{
    internal static void UniverseLifetimeSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Reference query lifetime: " + message); }
        using var references = Build(new[] { typeof(object).Assembly.Location });
        var integer = new TypeNode.Fqn("System.Int32");
        var reflectedInteger = references.ResolveRefType(integer.Name);
        Check(references.TryResolveLiteralFieldValue(integer, "MaxValue", out _, out var maximum)
            && maximum == new BirLiteral.IntegerLiteral(int.MaxValue), "the initial literal query did not run");
        _ = references.TryResolveVolatileField(integer, "MaxValue");
        Check(references.ResolveAwaitable("System.Threading.Tasks.Task", 0) != null
            && references._literalFieldCache.Count > 0 && references._volatileFieldCache.Count > 0
            && references._awaitPlanCache.Count > 0, "the derived query caches were not populated");
        references.SetLocalEmittedTypes(new HashSet<string> { integer.Name, "System.Threading.Tasks.Task" });
        Check(references._literalFieldCache.Count == 0 && references._volatileFieldCache.Count == 0
            && references._awaitPlanCache.Count == 0 && references.ResolveRefType(integer.Name) == null
            && !references.TryResolveLiteralFieldValue(integer, "MaxValue", out _, out _)
            && references.ResolveAwaitable("System.Threading.Tasks.Task", 0) == null,
            "a local-owner transition retained derived reference decisions");
        references.SetLocalEmittedTypes(new HashSet<string>());
        Check(references.ResolveRefType(integer.Name) == reflectedInteger
            && references.TryResolveLiteralFieldValue(integer, "MaxValue", out _, out maximum)
            && maximum == new BirLiteral.IntegerLiteral(int.MaxValue)
            && references.ResolveAwaitable("System.Threading.Tasks.Task", 0) != null,
            "local shadowing polluted fixed reference facts or retained a local miss");
        var count = references._literalFieldCache.Count;
        references.SetLocalEmittedTypes(new HashSet<string>());
        Check(references._literalFieldCache.Count == count,
            "an unchanged local-owner set invalidated unrelated fixed decisions");
        references.Dispose();
        Check(references._literalFieldCache.Count == 0 && references._volatileFieldCache.Count == 0
            && references._awaitPlanCache.Count == 0 && references._shippedCache.Count == 0,
            "index disposal retained derived or context-backed caches");
        try { references.ResolveRefType(integer.Name); }
        catch (ObjectDisposedException)
        {
            Console.WriteLine("[reference query lifetime] self-test OK (owner-driven invalidation, fixed-cache isolation and disposed query boundary)");
            return;
        }
        throw new InvalidOperationException("A disposed reference index reopened its universe");
    }
}
