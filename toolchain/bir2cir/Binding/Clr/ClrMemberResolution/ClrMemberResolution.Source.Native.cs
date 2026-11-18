using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ClrMemberResolution
{
    // The same final physical operation clock as the former DOM resolver.
    // Selection stays in the existing scalar policies; this walk owns only
    // child-before-parent traversal and the actual records receiving the facts.
    internal static void Apply(BirGraphValue root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypes)
    {
        _localTypes = localTypes ?? new HashSet<string>();
        var declarations = new List<string>();
        Walk(root);

        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            var named = node is BirFunctionDeclaration { Name.HasValue: true, Params.HasValue: true } method
                ? method.Name.Value : null;
            if (named != null) declarations.Add(named);
            try
            {
                node.VisitOwnChildSlots((_, role, slot) => {
                    if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
                });
                try
                {
                    if (node is BirFunctionDeclaration declaration) ResolveNativeOverrideSource(refs, declaration);
                    ResolveNativeCallsSource(refs, node);
                    ResolveNativeStorageSource(refs, node);
                    ResolveNativeTemplatesSource(refs, node);
                }
                catch (InvalidOperationException error)
                {
                    throw new InvalidOperationException(
                        $"{string.Join(" / ", declarations.AsEnumerable().Reverse())} [{node.GetType().Name}]: {error.Message}", error);
                }
            }
            finally
            {
                if (named != null) declarations.RemoveAt(declarations.Count - 1);
            }
        }
    }
}
