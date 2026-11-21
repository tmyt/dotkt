using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    // The exact declaration still implements INative<A>. On its split
    // sibling, expose the same A that a Kotlin value supplies to a CLR
    // generic argument, not the nominal class witness used for base access.
    static void ProjectNativeInterfaceStorage(BirClassDefinition storage, ProjectedStorageOwner plan, ReferenceMetadataIndex refs)
    {
        var start = TypeParameterFrame.Declarations(storage).Count() - plan.Slots.Length;
        var mappings = plan.Slots.Select((s, i) => (s, i)).Where(row => row.s.NativeArgument)
            .GroupBy(row => row.s.OwnerIndex).ToDictionary(g => g.Key, g => new TypeNode.Tv("type", start + g.First().i));
        if (mappings.Count == 0) return;
        TypeNode Map(TypeNode t) => CapturedGenericInvocation.Map(t, tv =>
            tv.Scope == "type" && mappings.TryGetValue(tv.I, out var value) ? value : tv);
        bool Native(TypeNode t) => t is TypeNode.Fqn { Args.Length: > 0 } f && !refs.HasDotKtOwner(f.Name) && refs.IsInterfaceType(f);
        TypeNode Interface(TypeNode t)
        {
            if (!Native(t) || t is not TypeNode.Fqn face) return t;
            var formals = refs.PhysicalOwnerTypeParametersInPhysicalFrame(face.Name);
            return face with { Args = face.Args.Select((a, i) =>
                formals[i].Constraints.OrEmpty().OfType<TypeNode.Fqn>().Any(b => !IsObjectish(b) && refs.IsClassType(b))
                    ? a : Map(a)).ToArray() };
        }
        storage.Interfaces = storage.Interfaces.OrEmpty().Select(Interface).ToArray();
        foreach (var method in storage.Methods.OrEmpty())
        {
            var slots = method.ClrInterfaceImpls.OrEmpty();
            if (!slots.Any(s => Native(s.Owner.OrDefault()) && Interface(s.Owner.Value) != s.Owner.Value)) continue;
            // Slot bridge signatures and their boundary conversions are in
            // the implementing frame. A selected MemberRef's signature, on
            // the other hand, is in the referenced declaration's own frame.
            method.Visit(n => n.RewriteOwnTypeFieldsInPlace((field, _, t) => {
                if (n is BirMemberReference && field != "declaringType") return t;
                if (n is BirCallInstance call && call.Recv.OrDefault() is BirThis ||
                    n is BirFieldGet get && get.Recv.OrDefault() is BirThis ||
                    n is BirSetField set && set.Recv.OrDefault() is BirThis) return t;
                return Map(t);
            }));
        }
    }
}
