using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ForeignNullableGenericCrossing
{
    internal static void SelfTest()
    {
        var integer = new TypeNode.Fqn("System.Int32");
        var nullable = new TypeNode.Nullable(integer);
        var foreign = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { nullable });
        var unit = new TypeNode.Fqn("void");
        var reference = new BirMemberReference {
            Name = "Scalar", DeclaringType = new TypeNode.Fqn("Boundary"), GenericArity = 0,
            ParameterTypes = new TypeNode[] { nullable }, ReturnType = unit,
        };
        var scalar = new BirNativeStatic {
            Type = new TypeNode.Fqn("Boundary"), Method = "Scalar", MemberRef = reference, ResolvedMemberReturn = unit,
        };
        Check(scalar, "owning-boundary");
        if (scalar.ResolvedMemberReturn.IsPresent || !ReferenceEquals(scalar.MemberRef.Value, reference)
            || reference.ParameterTypes.Value[0] != nullable)
            throw new InvalidOperationException("Foreign validation changed an inhabitable scalar signature or kept its consumed return stamp");

        static void Refused(Action check, string position)
        {
            try { check(); }
            catch (InvalidOperationException error) when (error.Message.Contains(position, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Owning foreign validation did not check its selected declaration's " + position);
        }
        var parameterCrossing = new BirNativeStatic {
            Type = new TypeNode.Fqn("Boundary"), Method = "Parameter", ResolvedMemberReturn = unit,
            MemberRef = new BirMemberReference { ParameterTypes = new TypeNode[] { foreign }, ReturnType = unit },
        };
        Refused(() => Check(parameterCrossing, "owning-boundary"), "parameter 0");
        var returnCrossing = new BirNativeStatic {
            Type = new TypeNode.Fqn("Boundary"), Method = "Return", Ret = new TypeNode.Fqn("object"),
            ResolvedMemberReturn = foreign,
            MemberRef = new BirMemberReference { ParameterTypes = Array.Empty<TypeNode>(), ReturnType = foreign },
        };
        Refused(() => Check(returnCrossing, "owning-boundary"), "return");

        var bytes = BirCarrier.EncodeBody<TypeNode>(BirCarrier.JsonV1, foreign, TypeNode.WriteJson);
        var physical = NullableGenericErasure.ErasedLoweredSlot(foreign);
        var parameter = new BirParameter { Name = "value", Type = physical, NullableGeneric = foreign };
        var stamped = new BirParameter { Name = "value", Type = parameter.Type, Attrs = new[] {
            new BirAttribute { Attr = new TypeNode.Fqn(RoundtripMetadata.AKNullableGen), Args = new IBirAttributeValue[] {
                new BirAttributeLiteral { Value = new BirLiteral.TextLiteral(BirCarrier.JsonV1) }, new BirOpaqueBytes { Bytes = bytes },
            } },
        } };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var slot = new Slot { Name = "Take", Arity = 0, Params = new TypeNode[] { foreign }, Ret = foreign };
        BirFunctionDeclaration Method(BirParameter value) => new() {
            Name = "Take", Params = new[] { value }, Ret = physical, NullableGenericRet = foreign,
            Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = value.Name.Value } } },
        };
        var method = Method(parameter);
        var stampedMethod = Method(stamped);
        if (!Declares(new[] { method }, slot, refs) || !Declares(new[] { stampedMethod }, slot, refs)
            || PreErasureTypeOf(stamped) != foreign
            || Declares(new[] { Method(new BirParameter { Name = "sibling", Type = parameter.Type }) }, slot, refs))
            throw new InvalidOperationException("Owning slot validation lost raw/stamped source correspondence or confused an erased sibling");

        var field = new BirFieldDeclaration { Name = "value", Type = physical, NullableGeneric = foreign };
        var property = new BirPropertyDeclaration { Name = "Value", Type = physical, NullableGeneric = foreign };
        stampedMethod.Name = "StampedTake";
        var owner = new BirClassDefinition { Name = "BoundaryOwner", Kind = "class", Fields = new[] { field },
            Properties = new[] { property }, Methods = new[] { method, stampedMethod } };
        var root = new BirFileDeclaration { FileClass = "BoundaryFile", Types = new IBirTypeDeclaration[] { owner } };
        CheckImplementedSlots(new[] { (root, "owning-boundary") }, refs);
        var capture = new BirCaptureDescriptor { Name = "captured", Type = physical, NullableGeneric = foreign };
        DropSlotRecords(capture);
        if (parameter.NullableGeneric.IsPresent || field.NullableGeneric.IsPresent || property.NullableGeneric.IsPresent
            || method.NullableGenericRet.IsPresent || stampedMethod.NullableGenericRet.IsPresent || capture.NullableGeneric.IsPresent
            || !ReferenceEquals(((BirOpaqueBytes)stamped.Attrs.Value[0].Args.Value[1]).Bytes.Value, bytes))
            throw new InvalidOperationException("Owning slot validation did not consume actual source records or modified opaque metadata");
        Console.WriteLine("[owning foreign boundary] self-test OK (selected parameter/return, source-slot correspondence, record consumption)");
    }
}
