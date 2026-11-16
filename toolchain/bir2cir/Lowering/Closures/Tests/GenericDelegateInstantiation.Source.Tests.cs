using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericDelegateInstantiation
{
    internal static void SourceSelfTest()
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Owning generic delegates: " + message);
        }
        TypeNode Text() => new TypeNode.Fqn("kotlin.String");
        TypeNode Number() => new TypeNode.Fqn("kotlin.Int");
        TypeNode Delegate(TypeNode argument) => new TypeNode.Fqn("D", new[] { argument });
        BirExpressionStatement Expr(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        BirCallInstance Call(IBirExpression recv, string name = "getValue", TypeNode owner = null) => new() {
            OwnerType = owner ?? new TypeNode.Fqn("D"), Method = name, Recv = OptionalField<IBirExpression>.Of(recv),
            Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(),
        };
        BirLocal Local(string name) => new() { Name = name };
        var ownerApplication = new TypeNode.Fqn("Container", new[] { Text() });
        var fieldReceiver = Local("container");
        var field = new BirFieldGet { OwnerType = new TypeNode.Fqn("Container"), Name = "delegate", Recv = fieldReceiver };
        var member = Call(field);
        var topField = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("DelegateFile"), Name = "topDelegate" };
        var top = Call(topField, "setValue");
        var effect = new BirCallStatic { Owner = new TypeNode.Fqn("DelegateFile"), Method = "effect",
            Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(), Ret = Number() };
        top.Args = new IBirExpression[] { effect };
        var topArguments = top.Args.Value;
        var beforeLocal = Call(Local("localDelegate"));
        var initializer = Call(Local("localDelegate"));
        initializer.Ret = Text();
        var afterLocal = Call(Local("localDelegate"));
        var fromParameter = Call(Local("parameter"), "provideDelegate");
        var inside = Call(Local("parameter"));
        var result = Call(Local("parameter"));
        var block = new BirValueBlock { Type = Text(), Stmts = new IBirStatement[] {
            new BirVariable { Name = "parameter", Type = Delegate(Number()), Init = new BirNew {
                Type = Delegate(Number()), Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>(),
            } }, Expr(inside),
        }, Result = result };
        var sibling = Call(Local("parameter"));
        var unrelated = Call(new BirLocal { Name = "unrelated", Sty = new TypeNode.Fqn("Other", new[] { Text() }) });
        var ordinary = Call(new BirLocal { Name = "parameter" }, "ordinaryMember");
        var constructedOwner = Delegate(Number());
        var constructed = Call(Local("parameter"), owner: constructedOwner);
        var unstampedField = Call(new BirFieldGet { OwnerType = new TypeNode.Fqn("Container"), Name = "delegate", Recv = new BirThis() });
        var method = new BirFunctionDeclaration { Name = "use", Params = new[] {
            new BirParameter { Name = "container", Type = ownerApplication },
            new BirParameter { Name = "parameter", Type = Delegate(Text()) },
            new BirParameter { Name = "localDelegate", Type = Delegate(Text()) },
        }, Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] {
            Expr(member), Expr(top), Expr(beforeLocal),
            new BirVariable { Name = "localDelegate", Type = Delegate(Number()), Init = new BirNew {
                Type = Delegate(Number()), Args = new IBirExpression[] { initializer }, ArgTypes = new[] { Text() },
            } },
            Expr(afterLocal), Expr(fromParameter), Expr(block), Expr(sibling), Expr(unrelated), Expr(ordinary), Expr(constructed),
        } };
        var sourceFieldType = Delegate(new TypeNode.Tv("type", 0));
        var type = new BirClassDefinition { Kind = "class", Name = "Container", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Fields = new[] { new BirFieldDeclaration { Name = "delegate", Type = sourceFieldType } },
            Methods = new[] { new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(), Ret = Text(),
                Body = new IBirStatement[] { Expr(unstampedField) } } } };
        var file = new BirFileDeclaration { FileClass = "DelegateFile", Methods = new[] { method }, Types = new IBirTypeDeclaration[] { type },
            Fields = new[] { new BirFieldDeclaration { Name = "topDelegate", Type = Delegate(Number()), Static = true } } };
        var body = method.Body.Value; var types = file.Types.Value;
        ApplyAll(new[] { file });
        Require(member.OwnerType.Value.Equals(Delegate(Text())) && field.OwnerType.Value.Equals(ownerApplication)
            && top.OwnerType.Value.Equals(Delegate(Number())) && fromParameter.OwnerType.Value.Equals(Delegate(Text())),
            "direct receiver owner, declared instance/static field type or parameter instantiation changed");
        Require(beforeLocal.OwnerType.Value.Equals(Delegate(Text())) && initializer.OwnerType.Value.Equals(Delegate(Text()))
            && afterLocal.OwnerType.Value.Equals(Delegate(Number())) && inside.OwnerType.Value.Equals(Delegate(Number()))
            && result.OwnerType.Value.Equals(Delegate(Text())) && sibling.OwnerType.Value.Equals(Delegate(Text())),
            "preorder/preceding-local, incoming result scope or sibling isolation changed");
        Require(unstampedField.OwnerType.Value.Equals(sourceFieldType)
            && unrelated.OwnerType.Value.Equals(new TypeNode.Fqn("D")) && ordinary.OwnerType.Value.Equals(new TypeNode.Fqn("D"))
            && ReferenceEquals(constructed.OwnerType.Value, constructedOwner)
            && ReferenceEquals(file.Types.Value, types) && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(member.Recv.Value, field) && ReferenceEquals(field.Recv.Value, fieldReceiver)
            && ReferenceEquals(top.Recv.Value, topField) && ReferenceEquals(top.Args.Value, topArguments)
            && ReferenceEquals(top.Args.Value[0], effect) && !member.DeclarationId.IsPresent && !member.Sty.IsPresent,
            "foreign/preconstructed owner, unknown this frame, declaration facts or operand identity changed");
        Require(!ReferenceEquals(((TypeNode.Fqn)member.OwnerType.Value).Args, ((TypeNode.Fqn)sourceFieldType).Args)
            && sourceFieldType.Equals(Delegate(new TypeNode.Tv("type", 0))), "constructed owner mutated or borrowed declared field arguments");

        // The declaration table's explicit last writer is independent of source-method selection catalogs.
        var fields = new FieldTypes();
        fields.Add("Shared", "delegate", Delegate(Number()));
        fields.Add("Shared", "delegate", Delegate(Text()));
        Require(fields.Lookup("Shared", "delegate").Equals(Delegate(Text())), "declared field table lost last-writer precedence");
        var exact = member.OwnerType.Value;
        ApplyAll(new[] { file });
        Require(ReferenceEquals(member.OwnerType.Value, exact), "already instantiated delegate owner was replaced");
        Console.WriteLine("[owning generic delegates] self-test OK (declared fields, preceding scope, direct receiver first, exact shapes and identity)");
    }
}
