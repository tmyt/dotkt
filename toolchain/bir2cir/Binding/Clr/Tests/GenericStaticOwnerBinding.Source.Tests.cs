using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericStaticOwnerBinding
{
    internal static void SourceSelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var carrierName = "Owner" + Marker;
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "O" } },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var carrier = new BirClassDefinition { Name = carrierName, Kind = "class",
            StaticCarrier = new BirStaticCarrier { Owner = "Owner" } };
        var descriptor = new[] { new BirTypeParameter { Name = "O" }, new BirTypeParameter { Name = "T" } };
        var read = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("Owner"), Name = "state",
            MemberOwnerTypeParams = descriptor, MemberType = new TypeNode.Fqn("kotlin.Int"), Volatile = true };
        var write = new BirStaticFieldSet { OwnerType = new TypeNode.Fqn("Owner"), Name = "state",
            MemberOwnerTypeParams = OptionalField<BirTypeParameter[]>.Null, Value = ExprValue(read) };
        var exception = new BirConstant { Type = new TypeNode.Fqn("kotlin.Any"), Value = new BirLiteral.NullLiteral() };
        var late = new BirLateinitGet { Static = true, OwnerType = new TypeNode.Fqn("Owner"), Name = "late",
            MemberOwnerTypeParams = descriptor, Exception = ExprValue(exception) };
        var instanceLate = new BirLateinitGet { Static = false, OwnerType = new TypeNode.Fqn("Owner"), Name = "instance",
            MemberOwnerTypeParams = descriptor };
        var eventRead = new BirEventGet { Static = true, Type = new TypeNode.Fqn("Owner"), Name = "Changed" };
        var argument = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(7) };
        var eventRaise = new BirEventRaise { Static = true, Type = new TypeNode.Fqn("Owner"), Event = "Changed",
            Args = new IBirExpression[] { argument } };
        var instanceEvent = new BirEventGet { Static = false, Type = new TypeNode.Fqn("Owner"), Name = "InstanceChanged" };
        var call = new BirCallStatic { OwnerType = new TypeNode.Fqn("Owner"), Method = "read",
            DeclarationId = "read-id", MemberOwnerTypeParams = OptionalField<BirTypeParameter[]>.Null,
            MemberMethodTypeParams = descriptor, CalleeOwner = new TypeNode.Fqn("SourceFacade"),
            Args = new IBirExpression[] { eventRead }, Ret = new TypeNode.Fqn("kotlin.Int"),
            Sty = new TypeNode.Fqn("kotlin.Int") };
        var alreadyPhysical = new BirCallStatic { Owner = new TypeNode.Fqn(carrierName), Method = "read" };
        var emptyArgs = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("Owner", Array.Empty<TypeNode>()), Name = "state" };
        var constructed = new TypeNode.Fqn("Owner", new TypeNode[] { new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1) });
        var closedCall = new BirCallStatic { Owner = constructed, Method = "closed", MemberOwnerTypeParams = descriptor };
        var absentOwner = new BirCallStatic { Owner = OptionalField<TypeNode>.Null, Method = "unowned",
            MemberOwnerTypeParams = descriptor };
        var body = new IBirStatement[] { Statement(write), Statement(late), Statement(instanceLate), Statement(eventRaise),
            Statement(instanceEvent), Statement(call), Statement(alreadyPhysical), Statement(emptyArgs),
            Statement(closedCall), Statement(absentOwner) };
        var method = new BirFunctionDeclaration { Name = "probe", Body = body };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner }, Methods = new[] { method } };
        var sibling = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { carrier } };
        ApplyAll(new[] { root, sibling }, refs);
        var physical = new TypeNode.Fqn(carrierName);
        if (read.OwnerType.Value != physical || write.OwnerType.Value != physical || late.OwnerType.Value != physical
            || eventRead.Type.Value != physical || eventRaise.Type.Value != physical || emptyArgs.OwnerType.Value != physical)
            throw new InvalidOperationException("Typed generic-static binding did not select the cross-file carrier for every supported static form");
        if (!read.MemberOwnerTypeParams.HasValue || read.MemberOwnerTypeParams.Value.Length != 0
            || !write.MemberOwnerTypeParams.HasValue || write.MemberOwnerTypeParams.Value.Length != 0
            || !late.MemberOwnerTypeParams.HasValue || late.MemberOwnerTypeParams.Value.Length != 0
            || !call.MemberOwnerTypeParams.HasValue || call.MemberOwnerTypeParams.Value.Length != 0
            || emptyArgs.MemberOwnerTypeParams.IsPresent || alreadyPhysical.MemberOwnerTypeParams.IsPresent)
            throw new InvalidOperationException("Typed generic-static binding changed absent/null/present descriptor semantics");
        if (call.Owner.Value != physical || call.OwnerType.IsPresent || call.DeclarationId.Value != "read-id"
            || call.CalleeOwner.Value != new TypeNode.Fqn("SourceFacade") || call.MemberMethodTypeParams.Value != descriptor
            || call.Ret.Value != new TypeNode.Fqn("kotlin.Int") || call.Sty.Value != call.Ret.Value
            || alreadyPhysical.Owner.Value != physical || alreadyPhysical.OwnerType.IsPresent)
            throw new InvalidOperationException("Typed generic-static binding lost the selected dispatch axis or unrelated signature facts");
        if (instanceLate.OwnerType.Value != new TypeNode.Fqn("Owner") || instanceLate.MemberOwnerTypeParams.Value != descriptor
            || instanceEvent.Type.Value != new TypeNode.Fqn("Owner") || closedCall.Owner.Value != constructed
            || closedCall.MemberOwnerTypeParams.Value != descriptor || closedCall.OwnerType.IsPresent
            || !absentOwner.Owner.IsNull || absentOwner.OwnerType.IsPresent || absentOwner.MemberOwnerTypeParams.Value != descriptor)
            throw new InvalidOperationException("Typed generic-static binding reinterpreted a nonstatic, constructed or absent owner");
        if (!ReferenceEquals(root.Methods.Value[0], method) || !ReferenceEquals(method.Body.Value, body)
            || !ReferenceEquals(root.Types.Value[0], owner) || !ReferenceEquals(sibling.Types.Value[0], carrier)
            || !ReferenceEquals(write.Value.Value, read) || !ReferenceEquals(late.Exception.Value, exception)
            || !ReferenceEquals(eventRaise.Args.Value[0], argument) || !ReferenceEquals(call.Args.Value[0], eventRead)
            || !read.Volatile.OrDefault() || TypeParameterFrame.Count(owner) != 2 || owner.TypeParams.Value.Length != 1)
            throw new InvalidOperationException("Typed generic-static binding replaced an owning occurrence or changed the complete declaration frame");

        // Placement can leave a type with captured parameters only. Its CLR
        // frame is still generic even though its own Kotlin arity is zero.
        var capturedOnly = new BirClassDefinition { Name = "CapturedOnly", Kind = "class",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "O" } }, TypeParams = Array.Empty<BirTypeParameter>() };
        var missing = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { capturedOnly },
            Methods = new[] { new BirFunctionDeclaration { Body = new IBirStatement[] {
                Statement(new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("CapturedOnly"), Name = "missing" }) } } } };
        bool rejected = false;
        try { ApplyAll(new[] { missing }, refs); }
        catch (InvalidOperationException error) when (error.Message ==
            "static access to generic owner 'CapturedOnly' has no constructed type or explicit non-generic carrier") { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Typed generic-static binding ignored a captured-only physical frame");
        var duplicate = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "DuplicateCarrier", StaticCarrier = new BirStaticCarrier { Owner = "Owner" } } } };
        rejected = false;
        try { ApplyAll(new[] { root, sibling, duplicate }, refs); }
        catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Typed generic-static binding silently selected among duplicate carriers");
        Console.WriteLine("[generic static source uses] self-test OK (current captured+own frames, exact presence, cross-file carrier and owning identities)");

        static OptionalField<IBirExpression> ExprValue(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        static BirExpressionStatement Statement(IBirExpression value) => new() { Expr = ExprValue(value) };
    }
}
