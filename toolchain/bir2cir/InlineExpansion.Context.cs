using DotKt.Bir;
using DotKt.Bir.Lowering;

// A file expansion owns one worklist and lexical cursor. The identity source is
// shared across files; labels and mutable expansion requests belong to this file.
sealed class InlinePassContext
{
    internal required BirFileDeclaration File { get; init; }
    internal required ReferenceMetadataIndex References { get; init; }
    internal required InlineBirIndex Bodies { get; init; }
    internal required IReadOnlyDictionary<string, InlineSplice.DispatchDef> Dispatch { get; init; }
    internal required CompanionExtensionBinding.LocalIndex CompanionExtensions { get; init; }
    internal required Func<int> NextSpliceIdentity { get; init; }
    internal required HashSet<string> AppLocalMethods { get; init; }
    internal bool RequireCompleteDefaults { get; init; }
    internal int NextLabelId { get; set; }
    internal int NextLabel() => NextLabelId++;
    internal List<BirFunctionDeclaration> PendingHelpers { get; } = new();
    internal Dictionary<BirTypeParameter[], NullableRepresentationFrame> RepresentationFrames { get; }
        = new(ReferenceEqualityComparer.Instance);
}

// BodyOwner is the actual declaration occurrence, not a name used to rediscover
// it later. Construction operands retain their enclosing frame at nested edges.
sealed record InlineFrame(
    BirGraphValue BodyOwner,
    BirTypeParameter[] OwnerParameters,
    NullableRepresentationFrame OwnerRepresentation,
    BirTypeParameter[] MethodParameters,
    NullableRepresentationFrame MethodRepresentation,
    string DiagnosticContext,
    int Depth)
{
    internal static InlineFrame Empty { get; } = new(null, null, null, null, null, null, 0);
}

readonly record struct InlineMaterialization(string LocalName, string RefusalReason)
{
    internal bool Succeeded => LocalName != null;
    internal static InlineMaterialization Success(string name) => new(name, null);
    internal static InlineMaterialization Refused(string reason) => new(null, reason);
}

sealed partial class InlineExpansion
{
    internal readonly InlinePassContext Pass;
    internal InlineFrame Frame = InlineFrame.Empty;
    internal InlineExpansion(InlinePassContext pass) => Pass = pass;
}
