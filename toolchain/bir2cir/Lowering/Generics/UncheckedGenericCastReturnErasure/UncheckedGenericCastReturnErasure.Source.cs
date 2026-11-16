using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UncheckedGenericCastReturnErasure
{
    internal sealed record SourceRoot(BirFileDeclaration Declaration, string FileClass);
    internal sealed record SourceOccurrence(int File, int Type, int Method, string Identity, Decl Facts,
        BirFunctionDeclaration Declaration, IBirTypeDeclaration Owner, BirFunctionDeclaration[] OriginalMethods)
    {
        internal TypeNode ExpectedPhysicalReturn = Facts.Return;
    }

    public sealed class PendingSourceIndex
    {
        SourceRoot[] roots;
        SourceOccurrence[] occurrences;
        readonly DeclIndex index = new();

        internal PendingSourceIndex(SourceRoot[] roots, SourceOccurrence[] occurrences)
        {
            this.roots = roots; this.occurrences = occurrences;
            foreach (var occurrence in occurrences) index.ByIdentity.Add(occurrence.Identity, occurrence.Facts);
        }

        internal DeclIndex SourceDeclarations => roots != null ? index
            : throw new InvalidOperationException("Unchecked source declarations already crossed their owning frontier");

        // Only the actual compiler-owned declaration transitions can report a
        // changed physical return. Captured body-Tv facts remain unchanged.
        internal bool AlignDelegateTargets(BirFileDeclaration root, ValueTypeOracle isValue)
        {
            _ = SourceDeclarations;
            return DelegateTargetSlotAlignment.Apply(root, isValue, RecordPhysicalReturnChange);
        }

        void RecordPhysicalReturnChange(BirFunctionDeclaration actual, TypeNode before, TypeNode after)
        {
            _ = SourceDeclarations;
            var occurrence = occurrences.SingleOrDefault(value => ReferenceEquals(value.Declaration, actual));
            if (occurrence == null) return; // Expansion can create targets outside the captured catalog.
            if (!ReferenceEquals(occurrence.Facts.SourceMethod, actual)
                || actual.DeclarationId.OrDefault() != occurrence.Identity
                || actual.Static.OrDefault() != occurrence.Facts.Static
                || occurrence.ExpectedPhysicalReturn != before || actual.Ret.OrDefault() != after)
                throw ProjectionMismatch();
            occurrence.ExpectedPhysicalReturn = BirGraphValue.CopyType(after);
        }

        internal void EraseOwnedReturn(Decl declaration)
        {
            var actual = declaration.SourceMethod;
            var before = actual.Ret.Value;
            actual.Ret = new TypeNode.Fqn("object");
            RecordPhysicalReturnChange(actual, before, actual.Ret.Value);
        }

        internal (SourceRoot[] Roots, SourceOccurrence[] Occurrences, DeclIndex Index) Consume()
        {
            if (roots == null) throw new InvalidOperationException("Unchecked declaration snapshot was already bound");
            var result = (roots, occurrences, index);
            roots = null;
            occurrences = null;
            return result;
        }
    }

    public static PendingSourceIndex CaptureSource(IReadOnlyList<BirFileDeclaration> roots)
    {
        var sourceRoots = roots.Select(root => new SourceRoot(root, root.FileClass.OrDefault())).ToArray();
        var occurrences = new List<SourceOccurrence>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var fileIndex = 0; fileIndex < roots.Count; fileIndex++)
        {
            var file = roots[fileIndex];
            void Methods(BirFunctionDeclaration[] methods, string owner, int typeIndex, IBirTypeDeclaration declaration)
            {
                for (var methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                {
                    var method = methods[methodIndex];
                    if (!IsCandidate(method.DeclarationId.OrDefault(), method.Virtual.OrDefault(), method.Override.OrDefault(),
                            method.Abstract.OrDefault(), method.Ret.OrDefault())
                        || !method.Body.OrEmpty().Any(statement => HasReturnedUncheckedCast((BirGraphValue)statement,
                            (TypeNode.Tv)method.Ret.Value))) continue;
                    var identity = method.DeclarationId.Value;
                    if (!identities.Add(identity))
                        throw new ArgumentException($"An item with the same key has already been added. Key: {identity}");
                    var facts = new Decl { Owner = owner, TopLevel = typeIndex < 0, Static = method.Static.OrDefault(),
                        Return = (TypeNode.Tv)BirGraphValue.CopyType(method.Ret.Value), SourceMethod = method };
                    occurrences.Add(new SourceOccurrence(fileIndex, typeIndex, methodIndex, identity, facts, method, declaration, methods));
                }
            }
            Methods(file.Methods.OrEmpty(), sourceRoots[fileIndex].FileClass, -1, null);
            var types = file.Types.OrEmpty();
            for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
                if (types[typeIndex].Name.HasValue)
                    Methods(types[typeIndex].Methods.OrEmpty(), types[typeIndex].Name.Value, typeIndex, types[typeIndex]);
        }
        return new PendingSourceIndex(sourceRoots, occurrences.ToArray());
    }

    // Virtual slots share one hierarchy-wide ABI. This declaration-local rule
    // can change only a final slot selected by exact frontend identity.
    static bool IsCandidate(string identity, bool virtualSlot, bool overrideSlot, bool abstractSlot, TypeNode result)
        => identity != null && !virtualSlot && !overrideSlot && !abstractSlot && result is TypeNode.Tv;

    static bool HasReturnedUncheckedCast(BirGraphValue node, TypeNode.Tv result)
    {
        if (node is BirReturn statement
            && ReturnedValue("return", statement.Value.OrDefault() as BirGraphValue,
                value => (value as BirGraphNode)?.Tag,
                value => value is BirCast cast ? cast.Type.OrDefault() : null,
                value => value is BirCast cast ? cast.E.OrDefault() as BirGraphValue : null,
                value => value is BirValueBlock block ? block.Result.OrDefault() as BirGraphValue : null) is BirCast returned
            && returned.E.OrDefault() is BirGraphValue source
            && IsUncheckedReturnCast(returned.Tag, returned.ReifiedTypeOperand.OrDefault(), returned.Type.OrDefault(),
                result, source.ExplicitSourceType)) return true;

        // Match the former owning traversal exactly: only the transient
        // synthClass edge is another scope. Local and suspend bodies still walk.
        BirGraphValue excluded = node switch {
            BirNewSam sam => sam.SynthClass.OrDefault(),
            BirNewClosure closure => closure.SynthClass.OrDefault(),
            _ => null,
        };
        var found = false;
        node.VisitChildren(child => {
            if (!found && !ReferenceEquals(child, excluded)) found = HasReturnedUncheckedCast(child, result);
        });
        return found;
    }

    // Its last call consumer has finished. Validate the same captured rows
    // against producer-reported ownership, then end this source-only capability.
    // Neither current names nor pending metadata can admit a declaration here.
    public static void CompleteSourceDeclarations(PendingSourceIndex pending,
        IReadOnlyList<BirFileDeclaration> actualRoots, AliasDeclarationTransitions aliases,
        SourceDeclarationProjection declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var (roots, occurrences, _) = pending.Consume();
        if (actualRoots.Count != roots.Length) throw ProjectionMismatch();
        aliases?.ValidateSourceContinuations(actualRoots);
        for (var file = 0; file < roots.Length; file++)
            if (!ReferenceEquals(actualRoots[file], roots[file].Declaration)
                || actualRoots[file].FileClass.OrDefault() != roots[file].FileClass) throw ProjectionMismatch();

        var associated = new HashSet<BirFunctionDeclaration>(ReferenceEqualityComparer.Instance);
        foreach (var occurrence in occurrences)
        {
            var sourceRoot = roots[occurrence.File];
            declarations.RequireOccurrence(sourceRoot.Declaration);
            BirFunctionDeclaration[] sourceMethods;
            BirGraphValue container;
            SourceDeclarationProjection.MethodPlacement placement = null;
            var retiredOwner = false;
            if (occurrence.Type < 0)
            {
                sourceMethods = sourceRoot.Declaration.Methods.OrEmpty();
                container = sourceRoot.Declaration;
                if (declarations.TryMethodPlacement(sourceRoot.Declaration, out var reported))
                {
                    placement = reported;
                    sourceMethods = reported.Before;
                }
            }
            else
            {
                var sourceTypes = sourceRoot.Declaration.Types.OrEmpty();
                var position = occurrence.Type;
                var aliasReported = aliases?.TryOwner(sourceRoot.Declaration, occurrence.Type, occurrence.Owner,
                    out position, out retiredOwner) == true;
                var reported = aliasReported;
                if (!reported)
                    reported = declarations.TryOwner(sourceRoot.Declaration, occurrence.Type, occurrence.Owner,
                        out position, out retiredOwner);
                if (occurrence.Owner.Name.OrDefault() != occurrence.Facts.Owner) throw ProjectionMismatch();
                if (!retiredOwner)
                {
                    if (position < 0 || position >= sourceTypes.Length || !ReferenceEquals(sourceTypes[position], occurrence.Owner))
                        throw ProjectionMismatch();
                }
                else if (!reported || sourceTypes.Any(type => ReferenceEquals(type, occurrence.Owner))) throw ProjectionMismatch();
                // An explicitly replaced alias keeps only its logical source
                // owner; each method still requires its own retired occurrence.
                if (!aliasReported || !retiredOwner || !aliases.IsReplacedLogicalOwner(occurrence.Owner))
                    declarations.RequireOccurrence((BirGraphValue)occurrence.Owner);
                sourceMethods = occurrence.Owner.Methods.OrEmpty();
                container = (BirGraphValue)occurrence.Owner;
            }
            var consumed = declarations.WasIdentityConsumed(occurrence.Declaration, occurrence.Identity);
            if (occurrence.Method >= occurrence.OriginalMethods.Length
                || !ReferenceEquals(occurrence.OriginalMethods[occurrence.Method], occurrence.Declaration)
                || !ReferenceEquals(occurrence.Facts.SourceMethod, occurrence.Declaration)
                || !(consumed ? !occurrence.Declaration.DeclarationId.IsPresent
                    : occurrence.Declaration.DeclarationId.OrDefault() == occurrence.Identity)
                || occurrence.Declaration.Static.OrDefault() != occurrence.Facts.Static
                || occurrence.Declaration.Ret.OrDefault() != occurrence.ExpectedPhysicalReturn) throw ProjectionMismatch();

            declarations.RequireOccurrence(occurrence.Declaration);
            if (declarations.IsRetired(occurrence.Declaration))
            {
                var stillOwned = retiredOwner
                    ? sourceRoot.Declaration.Methods.OrEmpty().Concat(sourceRoot.Declaration.Types.OrEmpty()
                        .SelectMany(type => type.Methods.OrEmpty())).Any(value => ReferenceEquals(value, occurrence.Declaration))
                    : sourceMethods.Any(value => ReferenceEquals(value, occurrence.Declaration));
                if (stillOwned) throw ProjectionMismatch();
            }
            else
            {
                if (retiredOwner) throw ProjectionMismatch();
                var position = occurrence.Method;
                for (var previous = 0; previous < occurrence.Method; previous++)
                    if (declarations.IsRetired(occurrence.OriginalMethods[previous]))
                    {
                        if (sourceMethods.Any(value => ReferenceEquals(value, occurrence.OriginalMethods[previous])))
                            throw ProjectionMismatch();
                        position--;
                    }
                if (position < 0 || position >= sourceMethods.Length || !ReferenceEquals(sourceMethods[position], occurrence.Declaration))
                    throw ProjectionMismatch();
                if (placement?.TryMove(occurrence.Declaration, out var move) == true)
                {
                    if (move.SourceOrdinal != position || move.DestinationOrdinal >= move.Destination.Methods.OrEmpty().Length
                        || !ReferenceEquals(move.Destination.Methods.Value[move.DestinationOrdinal], occurrence.Declaration))
                        throw ProjectionMismatch();
                    container = (BirGraphValue)move.Destination;
                    position = move.DestinationOrdinal;
                }
                else if (placement != null)
                {
                    position -= placement.Moves.Count(value => value.SourceOrdinal < position);
                    if (position < 0 || position >= placement.After.Length || !ReferenceEquals(placement.After[position], occurrence.Declaration))
                        throw ProjectionMismatch();
                }
                var methods = container switch {
                    BirFileDeclaration file => file.Methods.OrEmpty(),
                    IBirTypeDeclaration type => type.Methods.OrEmpty(),
                    _ => throw ProjectionMismatch(),
                };
                if (position >= methods.Length || !ReferenceEquals(methods[position], occurrence.Declaration)) throw ProjectionMismatch();
            }
            if (!associated.Add(occurrence.Declaration)) throw ProjectionMismatch();
        }
        // Retain Facts.Return as the original captured body-Tv. Only the
        // compiler stage's exact report could change ExpectedPhysicalReturn.
        foreach (var occurrence in occurrences) occurrence.Facts.SourceMethod = null;
    }

    static InvalidOperationException ProjectionMismatch() =>
        new("Unchecked declaration snapshot does not match its continuing occurrence");
}
