using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    internal void RecordSourceTypes(BirFileDeclaration root)
    {
        if (referenceBuild || bindings.Count == 0) return;
        OptionalField<TypeNode> Slot(OptionalField<TypeNode> physical, OptionalField<TypeNode> previous)
            => !previous.HasValue && physical.HasValue && !physical.Value.Equals(AliasFace(physical.Value))
                ? BirGraphValue.CopyType(physical.Value) : previous;
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            switch (node)
            {
                case BirFunctionDeclaration value: value.RetKotlinType = Slot(value.Ret, value.RetKotlinType); break;
                case BirInheritedClassMethod value: value.RetKotlinType = Slot(value.Ret, value.RetKotlinType); break;
                case BirParameter value: value.KotlinType = Slot(value.Type, value.KotlinType); break;
                case BirLambdaParameter value: value.KotlinType = Slot(value.Type, value.KotlinType); break;
                case BirCaptureDescriptor value: value.KotlinType = Slot(value.Type, value.KotlinType); break;
                case BirFieldDeclaration value: value.KotlinType = Slot(value.Type, value.KotlinType); break;
                case BirPropertyDeclaration value: value.KotlinType = Slot(value.Type, value.KotlinType); break;
            }
            if (node is IBirTypeDeclaration owner && owner.Kind.HasValue)
            {
                var parent = owner.Base.OrDefault();
                if (parent == null || parent.Equals(AliasFace(parent))) parent = null;
                var interfaces = owner.Interfaces.OrEmpty().Where(type => !type.Equals(AliasFace(type)))
                    .Select(BirGraphValue.CopyType).ToArray();
                var before = NullableRepresentationViews.TypeFacts(node);
                var after = (before ?? SourceTypeFacts.Empty).Merge(BirGraphValue.CopyType(parent), interfaces, null);
                if (!ReferenceEquals(after, before ?? SourceTypeFacts.Empty)) NullableRepresentationViews.SetTypeFacts(node, after);
            }
            node.VisitOwnChildSlots((field, role, slot) => {
                // Exact foreign descriptors are not Kotlin declaration slots.
                // Scalar transport markers are not generated graph children.
                if (NullableRepresentationViews.Frozen(role)
                    || field is "attrs" or "overrides" or "memberRef" or "clrInterfaceImpls" or "clrBaseImpls"
                    or DeclarationIdentityBinding.SemanticSignatureKey) return;
                Walk(slot.Value);
            });
        }
        Walk(root);
    }
}
