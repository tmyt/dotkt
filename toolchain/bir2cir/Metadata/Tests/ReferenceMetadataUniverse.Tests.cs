using System.Reflection;
using DotKt.Toolchain;

sealed partial class ReferenceMetadataUniverse
{
    internal static void SelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Reference universe: " + message); }
        static void Closed(Action query)
        {
            try { query(); }
            catch (ObjectDisposedException) { return; }
            throw new InvalidOperationException("A disposed reference universe accepted a query");
        }
        var corePath = typeof(object).Assembly.Location;
        var catalog = ManagedReferenceCatalog.Create(new[] { corePath }, "reference-universe-test");
        using var first = new ReferenceMetadataUniverse(catalog);
        using var second = new ReferenceMetadataUniverse(catalog);
        Check(first.referencesContext == null && !first.referencesInitialized
            && first.PhysicalStdlib == null && first.referencesContext == null,
            "construction or an absent physical twin eagerly opened the reference context");
        var text = first.ResolveType("System.String", 0);
        var originalContext = first.referencesContext;
        Check(text != null && ReferenceEquals(text, first.ResolveType("System.String", 0))
            && first.types.Count == 1 && first.Assemblies.Count == 1,
            "repeated queries did not share the same fixed input/cache/context");
        Check(first.ResolveType("probe.Absent", 0) == null, "an absent declaration was synthesized");
        var count = first.types.Count;
        Check(first.ResolveType("probe.Absent", 0) == null && first.types.Count == count,
            "a cache miss was not retained");
        var task = first.ResolveType("System.Threading.Tasks.Task", 0);
        var genericTask = first.ResolveType("System.Threading.Tasks.Task", 1);
        Check(task != null && !task.IsGenericType && genericTask?.IsGenericTypeDefinition == true
            && genericTask.GetGenericArguments().Length == 1,
            "generic and non-generic declarations shared a cache entry");
        var assemblyName = text.Assembly.GetName().Name;
        Check(first.ResolveTypeIn("System.String", assemblyName.ToLowerInvariant()) == text
            && first.ResolveTypeIn("System.String", "probe.AbsentAssembly") == null
            && first.CoreType("System.String") == text && first.FirstType("System.String", 0) == text,
            "scoped/core/ordered lookup used another declaration universe");
        var otherText = second.ResolveType("System.String", 0);
        Check(otherText != text && second.referencesContext != originalContext
            && first.ResolveType("System.String", 0) == text,
            "interleaved universes shared mutable state or reflection handles");
        first.Dispose();
        Check(first.types.Count == 0 && first.references.Count == 0
            && first.referencesContext == null && first.physicalContext == null && first.physicalStdlib == null,
            "disposal retained context-backed query state");
        Closed(() => originalContext.LoadFromAssemblyPath(corePath));
        Closed(() => first.ResolveType("System.String", 0));
        Closed(() => first.ResolveTypeIn("System.String", assemblyName));
        Closed(() => first.CoreType("System.String"));
        Closed(() => _ = first.Assemblies);
        Closed(() => _ = first.PhysicalStdlib);
        Check(second.ResolveType("System.String", 0) == otherText,
            "one compilation's disposal invalidated another");
        using var following = new ReferenceMetadataUniverse(catalog);
        Check(following.ResolveType("System.String", 0) != otherText,
            "a subsequent compilation inherited a reflection handle");

        MetadataLoadContext unwoundContext = null;
        try
        {
            using var unwound = new ReferenceMetadataUniverse(catalog);
            _ = unwound.CoreType("System.Int32");
            unwoundContext = unwound.referencesContext;
            throw new UniverseProbeException();
        }
        catch (UniverseProbeException) { }
        Check(unwoundContext != null, "the exceptional-path test never acquired its real resource");
        Closed(() => unwoundContext.LoadFromAssemblyPath(corePath));
        using var empty = new ReferenceMetadataUniverse(ManagedReferenceCatalog.Create(
            Array.Empty<string>(), "empty-universe-test"));
        Check(empty.ResolveType("System.String", 0) == null && empty.Assemblies.Count == 0
            && empty.referencesInitialized && empty.referencesContext == null,
            "an unavailable metadata context retained partially initialized state");
        Console.WriteLine("[reference universe] self-test OK (lazy contexts, cached hits/misses, arity/scope, independent lifetimes, actual disposal and exceptional unwind)");
    }

    sealed class UniverseProbeException : Exception { }
}
