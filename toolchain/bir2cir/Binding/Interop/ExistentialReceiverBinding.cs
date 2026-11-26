using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// FBoundStarProjectionErasure gives Kotlin's G<*> / erased G<T> cast a physical non-generic
// compiler-generated existential interface. Calls through a local stored in that representation must target its exact interface
// slot, including its deterministic owner-T-dependent bridge name. Leaving the original G<T>
// MemberRef on that existential receiver is invalid CLR IL.
//
// This is an explicit physical-slot binding pass: it consumes the synthesized interface's actual method
// table (or reference metadata), and ilemit merely emits the owner/member recorded here.
// Inherited members and substituted CLR property nodes retain their frontend-selected identity;
// the physical interface graph supplies reachable declarations, not Kotlin override precedence.
static partial class ExistentialReceiverBinding
{
    public sealed class Index
    {
        internal readonly Dictionary<string, List<Member>> Members = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, TypeNode.Fqn> SemanticOwnerByPhysical = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string[]> Bases = new(StringComparer.Ordinal);
        internal readonly Dictionary<(string Owner, string Name), TypeNode> Fields = new();
        internal IReadOnlyDictionary<string, string> SourceDeclarations = new Dictionary<string, string>();
        internal IReadOnlyDictionary<string, SupertypeGraph.Def> SourceHierarchy;
    }

    internal sealed record Member(string Name, string SourceName, string AccessorKind,
        TypeNode[] Parameters, TypeNode Return, int GenericArity, string DeclarationId = null,
        TypeNode[] SignatureParameters = null, string SourceDeclarationId = null)
    {
        public int ParamCount => Parameters.Length;
    }

    sealed record SlotSelection(TypeNode.Fqn Owner, string Method, string Identity,
        TypeNode[] Parameters, TypeNode[] Signature, TypeNode Result);

    static SlotSelection SelectSlot(Index index, ReferenceMetadataIndex refs, TypeNode.Fqn receiverType,
        TypeNode.Fqn authoredOwner, string sourceMethod, string accessorKind, int pc, int ga,
        TypeNode[] authoredSignature, string selectedId)
    {
        string physicalMethod = null;
        string physicalDeclarationId = null;
        TypeNode[] physicalParameters = null;
        TypeNode[] physicalSignature = null;
        TypeNode physicalResult = null;

        var physicalOwner = receiverType;

        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new List<string> { receiverType.Name };
            var candidates = new List<(string Owner, Member Slot)>();
            while (frontier.Count != 0 && candidates.Count == 0)
            {
                var next = new List<string>();
                foreach (var owner in frontier)
                {
                    if (!visited.Add(owner)) continue;
                    if (index.Members.TryGetValue(owner, out var members))
                        candidates.AddRange(members
                            .Where(m => m.ParamCount == pc && m.GenericArity == ga
                                && (selectedId != null || m.SourceName == sourceMethod) && m.AccessorKind == accessorKind
                                && (selectedId == null || m.DeclarationId == selectedId
                                    || m.SourceDeclarationId == selectedId)
                                && (selectedId != null || SignatureMatches(m.Parameters, authoredSignature)))
                            .Select(member => (owner, member)));
                    else if (TryReferenceSlot(new TypeNode.Fqn(owner), out var referencedOwner, out var referencedSlot))
                        candidates.Add((referencedOwner, referencedSlot));
                    if (index.Bases.TryGetValue(owner, out var parents)) next.AddRange(parents);
                    else if (refs.TryReferenceTypeShape(new TypeNode.Fqn(owner), out _, out _, out var baseType, out var interfaces))
                    {
                        if (baseType != null) next.Add(baseType.Name);
                        next.AddRange(interfaces.Select(parent => parent.Name));
                    }
                }
                frontier = next;
            }
            // Duplicate roots may describe the same physical slot. Coalesce only a structurally identical CLR
            // descriptor. TypeNode.ToString() is not an identity for nested generic arguments, and a MethodDef's
            // return participates in its signature, so either shortcut could hide a genuinely ambiguous slot here.
            for (var i = candidates.Count - 1; i >= 0; i--)
                if (candidates.Take(i).Any(existing => existing.Owner == candidates[i].Owner
                        && SamePhysicalSlot(existing.Slot, candidates[i].Slot)))
                    candidates.RemoveAt(i);
            if (candidates.Count == 1)
            {
                physicalOwner = new TypeNode.Fqn(candidates[0].Owner);
                physicalMethod = candidates[0].Slot.Name;
                physicalDeclarationId = candidates[0].Slot.DeclarationId;
                physicalParameters = candidates[0].Slot.Parameters;
                physicalSignature = candidates[0].Slot.SignatureParameters ?? physicalParameters;
                physicalResult = candidates[0].Slot.Return;
            }
        }
        bool TryReferenceSlot(TypeNode.Fqn referencedReceiver, out string declaringOwner, out Member slot)
        {
            declaringOwner = null;
            slot = null;
            if (!refs.TryExistentialSemanticOwner(referencedReceiver.Name, out var sourceOwner)) return false;
            var semanticOwner = authoredOwner != null
                && (authoredOwner.Name == sourceOwner
                    || refs.TryExistentialPhysicalOwner(authoredOwner.Name, out var authoredCarrier)
                        && authoredCarrier == referencedReceiver.Name)
                ? new TypeNode.Fqn(sourceOwner, authoredOwner.Args) : new TypeNode.Fqn(sourceOwner);
            // Splicing can retain a base member's declaration identity while projecting its receiver owner.
            // That selected declaration, not the receiver classifier, owns the existential slot.
            if (selectedId != null && refs.TryDeclarationIdentity(selectedId, out _, out var selectedOwner, out _, out _))
                semanticOwner = new TypeNode.Fqn(selectedOwner);
            if (refs.TryStarProjectionMember(semanticOwner, sourceMethod, accessorKind,
                    ga, authoredSignature, pc, selectedId,
                    out var erasedOwner, out var erasedMethod, out var erasedSignature, out _,
                    out var erasedResult, out _)
                && IsReachableReferenceCarrier(referencedReceiver, erasedOwner, refs))
            {
                declaringOwner = erasedOwner;
                slot = new Member(erasedMethod, sourceMethod, accessorKind,
                    erasedSignature.Select(SignatureValueTypes.Of).ToArray(), erasedResult, ga,
                    SignatureParameters: erasedSignature);
                return true;
            }
            // A non-generic base has no carrier of its own. Its declaration
            // can still have a published slot on the receiver's carrier.
            if (selectedId != null && semanticOwner.Name != sourceOwner &&
                refs.TryInheritedStarSlot(referencedReceiver.Name, selectedId, ga, pc, out declaringOwner, out var inherited, out _))
            {
                slot = new Member(inherited.Name, sourceMethod, accessorKind, inherited.ParamTypeNodes,
                    inherited.SuspendReturnType ?? inherited.ReturnTypeNode, ga, inherited.DeclarationId,
                    SignatureParameters: inherited.SignatureParameters);
                return true;
            }
            return false;
        }

        return physicalMethod == null ? null : new(physicalOwner, physicalMethod, physicalDeclarationId,
            physicalParameters, physicalSignature, physicalResult);
    }

    static bool SignatureMatches(IReadOnlyList<TypeNode> declaration, IReadOnlyList<TypeNode> call)
    {
        if (call == null) return true;
        return declaration.Count == call.Count
            && declaration.Select((parameter, index) =>
                    ReferenceMetadataIndex.AccessorDeclarationDescribesCall(parameter, call[index]))
                .All(match => match);
    }

    static bool SamePhysicalSlot(Member left, Member right) =>
        left.Name == right.Name
        && left.GenericArity == right.GenericArity
        && Equals(left.Return, right.Return)
        && left.Parameters.SequenceEqual(right.Parameters)
        && (left.SignatureParameters ?? left.Parameters).SequenceEqual(right.SignatureParameters ?? right.Parameters);

    static bool IsReachableReferenceCarrier(TypeNode.Fqn receiver, string declaration, ReferenceMetadataIndex refs)
    {
        var pending = new Stack<TypeNode.Fqn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(receiver);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current.Name)) continue;
            if (current.Name == declaration) return true;
            if (!refs.TryReferenceTypeShape(current, out _, out _, out var baseType, out var interfaces)) continue;
            if (baseType != null) pending.Push(baseType);
            foreach (var inherited in interfaces) pending.Push(inherited);
        }
        return false;
    }

}
