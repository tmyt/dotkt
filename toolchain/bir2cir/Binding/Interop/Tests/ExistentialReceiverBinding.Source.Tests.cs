using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExistentialReceiverBinding
{
    internal static void SourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Source existential receiver: " + message); }
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var any = new TypeNode.Fqn("kotlin.Any"); var text = new TypeNode.Fqn("kotlin.String");
        var parentType = new TypeNode.Fqn("ParentCarrier"); var childType = new TypeNode.Fqn("ChildCarrier");
        var identity = FBoundStarProjectionErasure.ExistentialSlotIdentity("source-id", "SourceOwner");
        BirFunctionDeclaration Slot(string name, string role, TypeNode[] parameters, TypeNode returned) => new() {
            Name = name, ExistentialSourceMember = role == null ? "sourceMember" : default,
            DeclarationId = role == null ? OptionalField<string>.Of(identity) : default,
            PropertyName = role == null ? default : OptionalField<string>.Of("item"),
            PropertyAccessor = role == null ? default : OptionalField<string>.Of(role),
            Params = parameters.Select((type, at) => new BirParameter { Name = "value" + at, Type = type }).ToArray(),
            Ret = returned, Abstract = true,
        };
        var member = Slot("$star$selected", null, new TypeNode[] { any }, any);
        var getter = Slot("$star$read", "get", Array.Empty<TypeNode>(), any);
        var setter = Slot("$star$write", "set", new TypeNode[] { any }, new TypeNode.Fqn("void"));
        var parent = new BirInterfaceDefinition { Name = "ParentCarrier", Kind = "interface", Generated = true,
            KotlinType = new TypeNode.Fqn("SourceOwner", new TypeNode[] { new TypeNode.Star() }),
            Methods = new[] { member, getter, setter } };
        var child = new BirInterfaceDefinition { Name = "ChildCarrier", Kind = "interface", Generated = true,
            KotlinType = new TypeNode.Fqn("ChildOwner", new TypeNode[] { new TypeNode.Star() }),
            Interfaces = new TypeNode[] { parentType }, Methods = Array.Empty<BirFunctionDeclaration>() };
        var declaration = new BirFunctionDeclaration { Name = "nested", DeclarationId = "nested-setter",
            Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
        var nested = new BirLocalFunction { Id = "nested-setter", Decl = declaration };
        var operand = new BirValueBlock { Type = text, Stmts = new IBirStatement[] { nested },
            Result = E(new BirLocal { Name = "value", Sty = text }) };
        var setReceiver = new BirLocal { Name = "receiver" };
        var set = new BirPropertySet { Type = new TypeNode.Fqn("SourceOwner"), Name = "item",
            PropertyName = "item", PropertyAccessor = "set", Recv = E(setReceiver), Value = E(operand) };
        var getReceiver = new BirLocal { Name = "receiver" };
        var get = new BirPropertyGet { Type = new TypeNode.Fqn("SourceOwner"), Name = "item",
            PropertyName = "item", PropertyAccessor = "get", Recv = E(getReceiver) };
        var argument = new BirLocal { Name = "value" };
        var directReceiver = new BirLocal { Name = "receiver", Sty = new TypeNode.Fqn("SourceOwner") };
        var direct = new BirCallInstance { OwnerType = childType, Method = "sourceNameAlreadyChanged",
            DeclarationId = "source-id", Sig = new TypeNode[] { text }, Args = new IBirExpression[] { argument },
            Recv = E(directReceiver), Ret = text, MemberVisibility = "public",
            MemberOwnerTypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var method = new BirFunctionDeclaration { Name = "use", Ret = text,
            Params = new[] { new BirParameter { Name = "receiver", Type = childType }, new BirParameter { Name = "value", Type = text } },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = E(set) },
                new BirExpressionStatement { Expr = E(direct) }, new BirReturn { Value = E(new BirValueBlock {
                    Type = text, Result = E(new BirConditional { Type = text,
                        Cond = E(new BirConstant { Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(true) }),
                        Then = E(get), Else = E(new BirLocal { Name = "value", Sty = text }) }),
                }) } }, InlineBir = new byte[] { 0x80 } };
        var fieldType = new TypeNode.Fqn("Holder", new TypeNode[] { childType });
        var holderField = new BirFieldDeclaration { Name = "storage", Type = new TypeNode.Tv("type", 0) };
        var holder = new BirClassDefinition { Name = "Holder", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Fields = new[] { holderField } };
        var file = new BirFileDeclaration { FileClass = "CarrierUses", Types = new IBirTypeDeclaration[] { parent, child, holder },
            Methods = new[] { method } };
        var sourceMap = new Dictionary<string, string> { [identity] = "source-id" };
        var index = Collect(new[] { file }, sourceMap);
        Need(ReferenceEquals(index.SourceDeclarations, sourceMap) && index.Members["ParentCarrier"][0].SourceDeclarationId == "source-id"
            && SourceReceiverType(new BirFieldGet { OwnerType = fieldType, Name = "storage" },
                new Dictionary<string, TypeNode>(), index) == childType, "snapshot relation/closed field receiver changed");
        member.Params.Value[0].Type = text;
        Need(index.Members["ParentCarrier"][0].Parameters[0] == any, "current method mutation changed the earlier scalar slot snapshot");
        Apply(file, index, refs);
        Need(method.Body.Value[0] is BirExpressionStatement { Expr.Value: BirCallInstance write }
            && write.OwnerType.Value == parentType && write.Method.Value == "$star$write"
            && ReferenceEquals(write.Recv.Value, setReceiver) && ReferenceEquals(write.Args.Value.Single(), operand)
            && ReferenceEquals(operand.Stmts.Value.Single(), nested) && ReferenceEquals(nested.Decl.Value, declaration)
            && !set.Recv.IsPresent && !set.Value.IsPresent,
            "inherited property setter lost its exact slot or cloned its owning receiver/value forest");
        Need(direct.OwnerType.Value == parentType && direct.Method.Value == "$star$selected" && direct.DeclarationId.Value == identity
            && direct.Sig.Value.Single() == any && direct.CalleeParams.Value.Single() == any && direct.Virtual.Value
            && !direct.MemberVisibility.IsPresent && !direct.MemberOwnerTypeParams.IsPresent
            && ReferenceEquals(direct.Recv.Value, directReceiver) && ReferenceEquals(direct.Args.Value.Single(), argument)
            && method.Body.Value[1] is BirExpressionStatement { Expr.Value: BirCast { E.Value: BirCallInstance retained } }
            && ReferenceEquals(retained, direct), "selected ID did not outrank stale name/signature or original call was cloned");
        var result = (BirValueBlock)((BirReturn)method.Body.Value[2]).Value.Value;
        var branch = (BirConditional)result.Result.Value;
        Need(branch.Then.Value is BirCast { Type.Value: TypeNode.Fqn { Name: "kotlin.String" }, E.Value: BirCallInstance read }
            && read.Method.Value == "$star$read" && read.Ret.Value == any && read.Sty.Value == any
            && read.PropertyName.Value == "item" && read.PropertyAccessor.Value == "get"
            && ReferenceEquals(read.Recv.Value, getReceiver) && !get.Recv.IsPresent,
            "return/valueBlock/conditional expected result or physical getter association changed");
        var ambiguous = new Index();
        ambiguous.Members["Ambiguous"] = new() {
            new("first", "same", null, Array.Empty<TypeNode>(), any, 0),
            new("second", "same", null, Array.Empty<TypeNode>(), text, 0),
        };
        Need(SelectSlot(ambiguous, refs, new TypeNode.Fqn("Ambiguous"), null, "same", null, 0, 0, null, null) == null,
            "ambiguous physical contracts were guessed or collapsed by return-insensitive identity");
        var once = file.DeepClone(); Apply(file, index, refs);
        Need(file.StructurallyEquals(once) && ReferenceEquals(file.Methods.Value.Single(), method), "physical slot use was rebound repeatedly");
        Console.WriteLine("[source existential receiver] self-test OK (same scalar snapshot, exact ID/inheritance/property slots, MOVE/result contexts, field frames and ambiguity)");
    }
}
