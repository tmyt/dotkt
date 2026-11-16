using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    internal static void SourceTypesSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning value source metadata: " + message); }
        BirAttribute Attribute(string name, params string[] values) => new() {
            Attr = new TypeNode.Fqn(name), Args = values.Select(value => (IBirAttributeValue)new BirConstant {
                Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral(value) }).ToArray() };
        var union = new TypeNode.Fqn("Metadata.Union");
        var earlier = new TypeNode.Nullable(union);
        var array = new TypeNode.Array(union);
        var coalesced = new BirParameter { Name = "coalesced", Type = union, KotlinType = OptionalField<TypeNode>.Null, NullableGeneric = union };
        var retained = new BirParameter { Name = "retained", Type = union, KotlinType = earlier };
        var unchanged = new BirParameter { Name = "unchanged", Type = new TypeNode.Fqn("kotlin.String"), KotlinType = OptionalField<TypeNode>.Null };
        var lambdaParameter = new BirLambdaParameter { Name = "value", Type = union };
        var capture = new BirCaptureDescriptor { Name = "captured", Type = union };
        var lambda = new BirNewSuspendLambda { Arity = 1, Params = new[] { lambdaParameter }, Captures = new[] { capture },
            CapValues = new IBirExpression[] { new BirLocal { Name = "coalesced", Sty = union } }, SuspendRet = union, Body = Array.Empty<IBirStatement>() };
        var descriptor = new BirMethodImpl { Owner = new TypeNode.Fqn("Metadata.PhysicalSlot"), Member = "read", Arity = 0,
            Params = new TypeNode[] { union }, Ret = union };
        var descriptorBefore = descriptor.DeepClone();
        var member = new BirMemberReference { Kind = "method", Assembly = "Metadata", Name = "read", GenericArity = 0,
            DeclaringType = new TypeNode.Fqn("Metadata.PhysicalSlot"), ParameterTypes = new TypeNode[] { union }, ReturnType = union };
        var memberBefore = member.DeepClone();
        var method = new BirFunctionDeclaration { Name = "read", Params = new[] { coalesced, retained, unchanged }, Ret = array,
            ClrInterfaceImpls = new[] { descriptor }, ClrBaseImpls = new[] { (BirMethodImpl)descriptor.DeepClone() },
            Overrides = new[] { new BirOverride { Owner = union } }, Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = lambda }, new BirExpressionStatement { Expr = new BirCallStatic { MemberRef = member } } } };
        var field = new BirFieldDeclaration { Name = "field", Type = union, _nativeLocation = true };
        var property = new BirPropertyDeclaration { Name = "property", Type = union };
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>());
        var previous = SourceTypeFacts.Empty.Merge(new TypeNode.Fqn("Metadata.EarlierBase"),
            new TypeNode[] { new TypeNode.Fqn("Metadata.Wrapper", new TypeNode[] { earlier }) },
            new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { earlier } }).MergeNullableFrame(frame);
        var owner = new BirClassDefinition { Name = "Metadata.Owner", Kind = "class", Base = union,
            Interfaces = new TypeNode[] { new TypeNode.Fqn("Metadata.Wrapper", new TypeNode[] { union }), union },
            Methods = new[] { method }, Fields = new[] { field }, Properties = new[] { property }, KotlinSupertypesPre = previous };
        var binding = new BirInterfaceDefinition { Name = "Metadata.Union", Kind = "interface", Attrs = new[] {
            Attribute("kotlin.clr.ClrTypeAlias", "System.Object"),
            Attribute(TypeAttribute, "Metadata.Helpers.matches", "Metadata.Helpers.classifier", "Metadata.Helpers.component", "", "") } };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { binding, owner } };
        var roots = new[] { root };
        var policy = CreateSource(roots, null, false, new ValueRepresentationSourcePolicy(roots, null, false), CaptureSource(roots), null);
        var methods = owner.Methods.Value; var body = method.Body.Value; var parameters = method.Params.Value;
        policy.RecordSourceTypes(root);
        Check(coalesced.KotlinType.HasValue && TypeNodeDataEquality.Equal(coalesced.KotlinType.Value, union)
            && ReferenceEquals(coalesced.NullableGeneric.Value, union) && ReferenceEquals(retained.KotlinType.Value, earlier)
            && unchanged.KotlinType.IsNull, "null-coalescing or independent nullable source channel changed");
        Check(TypeNodeDataEquality.Equal(method.RetKotlinType.Value, array) && field.KotlinType.HasValue && property.KotlinType.HasValue
            && lambdaParameter.KotlinType.HasValue && capture.KotlinType.HasValue,
            "actual method/parameter/field/property/capture metadata carriers were missed");
        var facts = owner.KotlinSupertypesPre.Value;
        Check(facts.Base is TypeNode.Fqn { Name: "Metadata.EarlierBase" } && facts.Interfaces.Count == 2
            && ((TypeNode.Fqn)facts.Interfaces[0]).Args[0] is TypeNode.Nullable && facts.Bounds[0][0] == earlier
            && ReferenceEquals(facts.NullableFrame, frame), "source-edge first writer, head deduplication, bounds or nullable frame changed");
        Check(descriptor.StructurallyEquals(descriptorBefore) && member.StructurallyEquals(memberBefore)
            && ReferenceEquals(owner.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(method.Params.Value, parameters) && ReferenceEquals(((BirExpressionStatement)body[0]).Expr.Value, lambda)
            && field._nativeLocation.Value, "foreign descriptors, operand identity or native transport anchors changed");
        var once = facts.FrozenText;
        policy.RecordSourceTypes(root);
        Check(owner.KotlinSupertypesPre.Value.FrozenText == once, "source metadata recording duplicated an edge");
        var referenceParameter = new BirParameter { Name = "value", Type = union };
        var referenceRoot = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { binding },
            Methods = new[] { new BirFunctionDeclaration { Params = new[] { referenceParameter }, Ret = union } } };
        var referenceRoots = new[] { referenceRoot };
        var referencePolicy = CreateSource(referenceRoots, null, true, new ValueRepresentationSourcePolicy(referenceRoots, null, true), CaptureSource(referenceRoots), null);
        referencePolicy.RecordSourceTypes(referenceRoot);
        Check(!referenceParameter.KotlinType.IsPresent && !referenceRoot.Methods.Value[0].RetKotlinType.IsPresent,
            "reference mode published runtime alias source metadata");
        Console.WriteLine("[owning value source metadata] self-test OK (source precedence, exact domain, edge merge and unchanged ownership)");
    }
}
