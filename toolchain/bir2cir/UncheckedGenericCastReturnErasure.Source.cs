using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UncheckedGenericCastReturnErasure
{
    internal sealed record SourceRoot(BirFileDeclaration Declaration, string FileClass);
    internal sealed record SourceOccurrence(int File, int Type, int Method, string Identity, Decl Facts,
        BirFunctionDeclaration Declaration, IBirTypeDeclaration Owner, BirFunctionDeclaration[] OriginalMethods);

    public sealed class PendingSourceIndex
    {
        SourceRoot[] roots;
        SourceOccurrence[] occurrences;

        internal PendingSourceIndex(SourceRoot[] roots, SourceOccurrence[] occurrences)
        { this.roots = roots; this.occurrences = occurrences; }

        internal (SourceRoot[] Roots, SourceOccurrence[] Occurrences) Consume()
        {
            if (roots == null) throw new InvalidOperationException("Unchecked declaration snapshot was already bound");
            var result = (roots, occurrences);
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
                        Return = (TypeNode.Tv)BirGraphValue.CopyType(method.Ret.Value) };
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
        IReadOnlyDictionary<BirFunctionDeclaration, JsonObject> retiredDeclarations = null)
    {
        var (roots, occurrences) = pending.Consume();
        if (actualRoots.Count != roots.Length) throw ProjectionMismatch();
        var index = new DeclIndex();
        var associated = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var occurrence in occurrences)
        {
            var sourceRoot = roots[occurrence.File];
            var root = actualRoots[occurrence.File];
            if (sourceRoot.Declaration.FileClass.OrDefault() != sourceRoot.FileClass
                || Str(root["fileClass"]) != sourceRoot.FileClass) throw ProjectionMismatch();
            BirFunctionDeclaration[] sourceMethods;
            JsonObject container;
            if (occurrence.Type < 0)
            {
                sourceMethods = sourceRoot.Declaration.Methods.OrEmpty();
                container = root;
            }
            else
            {
                var sourceTypes = sourceRoot.Declaration.Types.OrEmpty();
                if (occurrence.Type >= sourceTypes.Length || !ReferenceEquals(sourceTypes[occurrence.Type], occurrence.Owner)
                    || occurrence.Owner.Name.OrDefault() != occurrence.Facts.Owner
                    || root["types"] is not JsonArray types || occurrence.Type >= types.Count
                    || types[occurrence.Type] is not JsonObject owner || Str(owner["name"]) != occurrence.Facts.Owner)
                    throw ProjectionMismatch();
                sourceMethods = occurrence.Owner.Methods.OrEmpty();
                container = owner;
            }
            if (occurrence.Method >= occurrence.OriginalMethods.Length
                || !ReferenceEquals(occurrence.OriginalMethods[occurrence.Method], occurrence.Declaration)
                || occurrence.Declaration.DeclarationId.OrDefault() != occurrence.Identity
                || occurrence.Declaration.Static.OrDefault() != occurrence.Facts.Static
                || occurrence.Declaration.Ret.OrDefault() != occurrence.Facts.Return) throw ProjectionMismatch();
            JsonObject method;
            if (retiredDeclarations != null && retiredDeclarations.TryGetValue(occurrence.Declaration, out var retired))
            {
                if (retired == null || retired.Parent != null || sourceMethods.Any(value => ReferenceEquals(value, occurrence.Declaration)))
                    throw ProjectionMismatch();
                method = retired;
            }
            else
            {
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
                || TypeJson.Read(method["ret"]) != occurrence.Facts.Return
                || !associated.Add(method)) throw ProjectionMismatch();
            index.ByIdentity.Add(occurrence.Identity, new Decl {
                Owner = occurrence.Facts.Owner, TopLevel = occurrence.Facts.TopLevel, Static = occurrence.Facts.Static,
                Return = occurrence.Facts.Return, Method = method,
            });
        }
        return index;
    }

    static InvalidOperationException ProjectionMismatch() =>
        new("Unchecked declaration snapshot does not match its projected occurrence");
}
