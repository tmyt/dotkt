using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationRename
{
    // Run after alias helper placement and exact referenced declaration binding,
    // before member-call substitution. Names change on the same owning records;
    // source IDs and the authored override closure remain declaration authority.
    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        Walk(root, false, null);

        void Walk(BirGraphValue node, bool inInterface, NullableRepresentationFrame ownerFrame)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (node is IBirTypeDeclaration type && type.Kind.HasValue)
            {
                inInterface = type.Kind.Value == "interface";
                if (type.Kind.Value is "class" or "interface" or "struct" or "enum")
                    ownerFrame = NullableRepresentationViews.TypeFacts(node)?.NullableFrame;
            }
            switch (node)
            {
                case BirCallInstance call when call.Overrides.HasValue:
                    // A semantic property call keeps its exact association for
                    // the later property/MethodImpl binder, never a CLR name guess.
                    if (!KotlinPropertyAccessors.TryCallIdentity(call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(),
                            call.Method.OrDefault(), call.Prop.OrDefault(), out _, out _)
                        && ResolveSlot(call, refs) is { } callSlot && !IsRule3Alias(call, refs))
                        call.Method = callSlot;
                    break;
                case BirFunctionDeclaration method when method.Overrides.HasValue && method.Name.IsPresent:
                    var property = KotlinPropertyAccessors.TryIdentity(method, out _, out _);
                    if (ResolveSlot(method, refs, ownerFrame) is not { } slot) break;
                    if (!property)
                    {
                        if (method.Name.HasValue && method.Name.Value != slot)
                        {
                            if (!method.KotlinSourceMember.HasValue) method.KotlinSourceMember = method.Name;
                            RoundtripMetadata.AddSourceMethodIdentity(method, method.Name.Value);
                        }
                        method.Name = slot;
                    }
                    // Interface declarations retain their frontend override
                    // flags; implementing an interface is not overriding a CLR
                    // class slot. Only classes need the physical adjustment.
                    if (!inInterface)
                    {
                        var nativeSlot = ResolveNetClassSlot(Overrides(method.Overrides.Value), Callable(method, ownerFrame), refs);
                        if (nativeSlot == null && method.Vis.IsPresent) method.Vis = "public";
                        // A virtual class accessor needs the exact base operand
                        // for MethodImpl binding, not implicit interface naming.
                        if (nativeSlot is { } target)
                        {
                            if (method.Override.IsPresent) method.Override = true;
                            if (!method.PendingOverrideOwner.HasValue) method.PendingOverrideOwner = target.Owner;
                            if (!method.PendingOverrideReturn.HasValue) method.PendingOverrideReturn = target.Return;
                            if (method.PendingOverrideMember.HasValue && method.PendingOverrideMember.Value != target.Accessor)
                                throw new InvalidOperationException(
                                    $"conflicting CLR base accessor slots '{method.PendingOverrideMember.Value}' and '{target.Accessor}'");
                            method.PendingOverrideMember = target.Accessor;
                        }
                    }
                    break;
            }
            // Property rows have kotlinAccessors, inherited implementation facts
            // have no Name, and event reads carry property markers but no callable
            // signature. None owns an additional rename in the current vocabulary.
            node.VisitChildren(child => Walk(child, inInterface, ownerFrame));
        }
    }

    static bool IsRule3Alias(BirCallInstance call, ReferenceMetadataIndex refs)
    {
        // Match the existing rule-3 authority exactly. In particular an aliased
        // interface must not suppress the physical CLR slot rename.
        var owner = (call.OwnerType.OrDefault() as TypeNode.Fqn)?.Name;
        var member = call.Method.OrDefault();
        return owner != null && member != null
            && refs.TryResolveClrOwner(owner, out _, out var kind) && kind != "interface"
            && refs.IsRule3Member(ReferenceMetadataIndex.BareOwnerFqn(owner), member);
    }

    // The nearest Kotlin accessor can have a different CLR name from the native
    // virtual it implements. The MethodImpl operand belongs to one override edge.
    static (TypeNode.Fqn Owner, string Accessor, TypeNode Return)? ResolveNetClassSlot(
        SlotOverride[] overrides, CallableSlot callable, ReferenceMetadataIndex refs)
    {
        foreach (var marker in overrides)
        {
            var owner = marker.Owner;
            var reflected = ReferenceMetadataIndex.ReflectedOwnerFqn(owner.Name);
            if (refs.ResolveNetType(reflected, owner.Args?.Length ?? 0) is not { IsClass: true } native) continue;
            if (marker.Kind == "method" && callable.Signature != null
                && refs.TryProjectedIndexerSlot(owner, marker.Member, callable.MethodArity, callable.Signature,
                    out var indexer, out var indexerReturn)) return (new TypeNode.Fqn(reflected, owner.Args), indexer, indexerReturn);
            if (!callable.Declaration || callable.Signature == null
                || !TryExactPropertySlot(callable.Signature, callable.MethodArity, refs, owner, marker.Member, marker.Kind,
                    out _, out var property, out var accessor, out var result)
                || !HasOverridableAccessor(native, property, marker.Kind)) continue;
            return (new TypeNode.Fqn(reflected, owner.Args), accessor, result);
        }
        // A Kotlin alias class reaches its actual BCL base through the same
        // exact projected property identity, not an accessor-name convention.
        foreach (var marker in overrides)
        {
            var owner = marker.Owner;
            if (refs.TryResolveClrOwner(owner.Name, out var bcl, out var kind) && kind == "class"
                && callable.Declaration && callable.Signature != null
                && TryExactPropertySlot(callable.Signature, callable.MethodArity, refs, owner, marker.Member, marker.Kind,
                    out _, out var property, out var accessor, out var result)
                && refs.ResolveNetType(bcl) is { } native && HasOverridableAccessor(native, property, marker.Kind))
            {
                return (new TypeNode.Fqn(bcl, owner.Args), accessor, result);
            }
        }
        return null;
    }
}
