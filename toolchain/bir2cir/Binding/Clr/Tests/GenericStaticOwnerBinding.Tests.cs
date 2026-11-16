using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericStaticOwnerBinding
{
    internal static void SelfTestPlacement()
    {
        var create = new BirNewDelegate { Method = "lifted", CalleeOwner = new TypeNode.Fqn("Generic"), DeclarationId = "helper-id" };
        var field = new BirFieldDeclaration {
            Name = "callback", Type = new TypeNode.Fqn("kotlin.Any"), Static = true, KotlinStatic = true,
            Init = OptionalField<IBirExpression>.Of(create), Vis = "private",
        };
        var argument = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(7) };
        var read = new BirEventGet { Type = new TypeNode.Fqn("Generic"), Name = "Changed", Static = true };
        var raise = new BirEventRaise { Type = new TypeNode.Fqn("Generic"), Event = "Changed", Static = true,
            Args = new IBirExpression[] { argument } };
        // The target owner's F-bound is declaration metadata, not a capture of this member's lexical frame.
        var access = new BirCallStatic { OwnerType = new TypeNode.Fqn("Generic"), Method = "source",
            MemberOwnerTypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] {
                new TypeNode.Fqn("Comparable", new TypeNode[] { new TypeNode.Tv("type", 0) }) } } } };
        if (ContainsOwnerTypeVariable(access) || !ContainsOwnerTypeVariable(new BirFunctionDeclaration {
            TypeParams = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { new TypeNode.Tv("type", 0) } } },
        })) throw new InvalidOperationException("Generic static capture check confused a declaration descriptor with lexical bounds");
        var source = new BirFunctionDeclaration { Name = "source", Static = true, KotlinStatic = true, DeclarationId = "source-id",
            Body = new IBirStatement[] { Expr(read), Expr(raise), Expr(access) } };
        var helper = new BirFunctionDeclaration { Name = "lifted", Generated = true, SemanticOwner = "Generic", DeclarationId = "helper-id",
            TypeParams = new[] { new BirTypeParameter { Name = "M" } }, Ret = new TypeNode.Tv("method", 0) };
        var property = new BirPropertyDeclaration { Name = "Property", KotlinStatic = true };
        var @event = new BirClrEventBacking { Name = "Changed", Static = true };
        var owner = new BirClassDefinition { Kind = "class", Name = "Generic", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Fields = new[] { field }, Methods = new[] { source }, Properties = new[] { property }, ClrEvents = new[] { @event } };
        var privateNested = new BirClassDefinition { Kind = "class", Name = "Generic.Private", SemanticOwner = "Generic", Vis = "private" };
        var protectedNested = new BirClassDefinition { Kind = "class", Name = "Generic.Protected", SemanticOwner = "Generic", Vis = "protected" };
        var implementation = new BirClassDefinition { Kind = "class", Name = "Implementation", Generated = true,
            SemanticOwner = "Generic", StaticSemanticOwner = "Generic" };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner, privateNested, protectedNested } };
        var sibling = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { implementation }, Methods = new[] { helper } };
        var roots = new[] { root, sibling };
        Materialize(roots);
        var carrier = root.Types.Value.Single(type => type.StaticCarrier.HasValue);
        var carrierName = "Generic" + Marker;
        if (carrier.Name.OrDefault() != carrierName || carrier.StaticCarrier.Value.Owner.OrDefault() != "Generic"
            || !carrier.StaticCarrier.Value.ProtectedNested.Value.SequenceEqual(new[] { "Protected" })
            || TypeParameterFrame.Count(carrier) != 0 || carrier.Fields.Value.Length != 2
            || !ReferenceEquals(carrier.Fields.Value[0], field) || field.Vis.OrDefault() != "private"
            || carrier.Fields.Value[1].Name.OrDefault() != CarrierInitMarker
            || !ReferenceEquals(carrier.Methods.Value[0], source) || !ReferenceEquals(carrier.Methods.Value[1], helper)
            || !ReferenceEquals(carrier.Properties.Value.Single(), property) || !ReferenceEquals(carrier.ClrEvents.Value.Single(), @event)
            || source.DeclarationId.OrDefault() != "source-id" || helper.DeclarationId.OrDefault() != "helper-id"
            || source.KotlinStatic.IsPresent || helper.KotlinStatic.IsPresent || field.KotlinStatic.IsPresent || property.KotlinStatic.IsPresent
            || sibling.Methods.Value.Length != 0 || helper.SemanticOwner.IsPresent
            || create.CalleeOwner.Value != new TypeNode.Fqn(carrierName) || create.DeclarationId.OrDefault() != "helper-id"
            || owner.Methods.Value.Length != 0 || owner.Properties.Value.Length != 0 || owner.ClrEvents.Value.Length != 0
            || owner.Fields.Value.Single().Name.OrDefault() != OwnerInitTrigger
            || owner.Fields.Value.Single().Init.Value is not BirStaticFieldGet { OwnerType.Value: TypeNode.Fqn { Name: var sentinelOwner } } || sentinelOwner != carrierName
            || privateNested.Vis.OrDefault() != "internal" || privateNested.NestedIn.OrDefault() != carrierName
            || protectedNested.Vis.OrDefault() != "public" || protectedNested.NestedIn.OrDefault() != carrierName
            || implementation.SemanticOwner.OrDefault() != carrierName || implementation.StaticSemanticOwner.IsPresent)
            throw new InvalidOperationException("Generic static placement lost declaration identity, source frames, or initialization ownership");
        ApplyEventOwnersAll(roots, null);
        if (read.Type.Value != new TypeNode.Fqn(carrierName) || raise.Type.Value != new TypeNode.Fqn(carrierName)
            || !ReferenceEquals(raise.Args.Value.Single(), argument) || !ReferenceEquals(((BirExpressionStatement)source.Body.Value[0]).Expr.Value, read))
            throw new InvalidOperationException("Generic static event binding changed expression identity or arguments");

        static BirExpressionStatement Expr(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
    }
}
