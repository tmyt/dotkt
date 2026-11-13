using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Current declaration inventory after representation placement and ownership
// preparation. Unlike SourceModuleIndex this includes newly materialized owners
// and methods. Only detached scalar/type facts cross the remaining DOM frontier.
sealed partial class TypedModuleInventory
{
    internal HashSet<string> LocalTopLevelFunctions { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> EmittedTypeFqns { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, List<string>> DirectSuperNames { get; } = new(StringComparer.Ordinal);
    internal bool HasUserCharSeqImplementation { get; }
    internal Dictionary<string, TypeNode[]> CalleeTypeParameters { get; }

    internal TypedModuleInventory(IReadOnlyList<BirFileDeclaration> roots)
    {
        foreach (var file in roots)
        {
            // Only file-scope methods participate in ownerless top-level lookup.
            foreach (var method in file.Methods.OrEmpty())
                if (method.Name.HasValue) LocalTopLevelFunctions.Add(method.Name.Value);

            // A file facade is an emitted type even though it has no Types row.
            if (file.FileClass.OrDefault() is { Length: > 0 } fileClass)
                EmittedTypeFqns.Add(fileClass);

            // Current typed declaration placement is flat; NestedIn/semantic
            // ownership do not change which declarations this module emits.
            foreach (var type in file.Types.OrEmpty())
            {
                var interfaces = type.Interfaces.OrEmpty();
                if (interfaces.Any(edge => edge is TypeNode.Fqn { Name: SharedSyntheticSynthesis.CharSeq }))
                    HasUserCharSeqImplementation = true;
                if (!type.Name.HasValue) continue;
                var name = type.Name.Value;
                EmittedTypeFqns.Add(name);
                var supers = new List<string>();
                if (type.Base.OrDefault() is TypeNode.Fqn baseType) supers.Add(baseType.Name);
                foreach (var edge in interfaces)
                    if (edge is TypeNode.Fqn supertype) supers.Add(supertype.Name);
                // Preserve the current declaration-order last-writer contract,
                // including an empty vector on a later same-name declaration.
                DirectSuperNames[name] = supers;
            }
        }
        CalleeTypeParameters = MapVarianceRealign.CollectCalleeTypeParams(roots);
    }
}
