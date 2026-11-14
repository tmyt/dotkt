using System.Text.Json.Nodes;
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

    // The only graph handoff is an association with the actual continuing DOM
    // occurrence produced by the single module projection. No candidate search,
    // name matching, body snapshot, or later replacement chasing occurs here.
    public static DeclIndex BindDomDeclarations(PendingSourceIndex pending, IReadOnlyList<JsonObject> actualRoots,
        IReadOnlyDictionary<BirFunctionDeclaration, JsonObject> retiredDeclarations = null,
        AliasDeclarationTransitions aliases = null)
    {
        var (roots, occurrences, index) = pending.Consume();
        if (actualRoots.Count != roots.Length) throw ProjectionMismatch();
        aliases?.ValidateProjection(actualRoots);
        var associated = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var occurrence in occurrences)
        {
            var sourceRoot = roots[occurrence.File];
            var root = actualRoots[occurrence.File];
            if (sourceRoot.Declaration.FileClass.OrDefault() != sourceRoot.FileClass
                || Str(root["fileClass"]) != sourceRoot.FileClass) throw ProjectionMismatch();
            BirFunctionDeclaration[] sourceMethods;
            JsonObject container;
            var retiredOwner = false;
            if (occurrence.Type < 0)
            {
                sourceMethods = sourceRoot.Declaration.Methods.OrEmpty();
                container = root;
            }
            else
            {
                var sourceTypes = sourceRoot.Declaration.Types.OrEmpty();
                var position = occurrence.Type;
                var reported = aliases?.TryOwner(sourceRoot.Declaration, occurrence.Type, occurrence.Owner,
                    out position, out retiredOwner) == true;
                if (occurrence.Owner.Name.OrDefault() != occurrence.Facts.Owner) throw ProjectionMismatch();
                JsonObject owner = null;
                if (!retiredOwner)
                {
                    if (position < 0 || position >= sourceTypes.Length || !ReferenceEquals(sourceTypes[position], occurrence.Owner)
                        || root["types"] is not JsonArray types || position >= types.Count
                        || types[position] is not JsonObject projectedOwner || Str(projectedOwner["name"]) != occurrence.Facts.Owner)
                        throw ProjectionMismatch();
                    owner = projectedOwner;
                }
                else if (!reported || sourceTypes.Any(type => ReferenceEquals(type, occurrence.Owner))) throw ProjectionMismatch();
                sourceMethods = occurrence.Owner.Methods.OrEmpty();
                container = owner;
            }
            if (occurrence.Method >= occurrence.OriginalMethods.Length
                || !ReferenceEquals(occurrence.OriginalMethods[occurrence.Method], occurrence.Declaration)
                || occurrence.Declaration.DeclarationId.OrDefault() != occurrence.Identity
                || occurrence.Declaration.Static.OrDefault() != occurrence.Facts.Static
                || occurrence.Declaration.Ret.OrDefault() != occurrence.ExpectedPhysicalReturn) throw ProjectionMismatch();
            JsonObject method;
            if (retiredDeclarations != null && retiredDeclarations.TryGetValue(occurrence.Declaration, out var retired))
            {
                var stillOwned = retiredOwner
                    ? sourceRoot.Declaration.Methods.OrEmpty().Concat(sourceRoot.Declaration.Types.OrEmpty()
                        .SelectMany(type => type.Methods.OrEmpty())).Any(value => ReferenceEquals(value, occurrence.Declaration))
                    : sourceMethods.Any(value => ReferenceEquals(value, occurrence.Declaration));
                if (retired == null || retired.Parent != null || stillOwned)
                    throw ProjectionMismatch();
                method = retired;
            }
            else
            {
                if (retiredOwner) throw ProjectionMismatch();
                // Only explicit retirements in this captured owning vector can move this exact occurrence.
                var position = occurrence.Method;
                if (retiredDeclarations != null)
                    for (var previous = 0; previous < occurrence.Method; previous++)
                        if (retiredDeclarations.ContainsKey(occurrence.OriginalMethods[previous]))
                        {
                            if (sourceMethods.Any(value => ReferenceEquals(value, occurrence.OriginalMethods[previous])))
                                throw ProjectionMismatch();
                            position--;
                        }
                if (position >= sourceMethods.Length || !ReferenceEquals(sourceMethods[position], occurrence.Declaration)
                    || container["methods"] is not JsonArray methods || position >= methods.Count
                    || methods[position] is not JsonObject continuing) throw ProjectionMismatch();
                method = continuing;
            }
            if (Str(method[DeclarationIdentityBinding.Key]) != occurrence.Identity
                || Bool(method["static"]) != occurrence.Facts.Static
                || TypeJson.Read(method["ret"]) != occurrence.ExpectedPhysicalReturn
                || !associated.Add(method)) throw ProjectionMismatch();
            occurrence.Facts.Method = method;
            occurrence.Facts.SourceMethod = null;
        }
        return index;
    }

    static InvalidOperationException ProjectionMismatch() =>
        new("Unchecked declaration snapshot does not match its projected occurrence");
}
