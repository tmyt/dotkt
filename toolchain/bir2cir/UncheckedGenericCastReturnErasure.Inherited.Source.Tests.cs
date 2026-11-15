using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UncheckedGenericCastReturnErasure
{
    internal static void SelfTestInheritedSource()
    {
        static void Need(bool value, string message)
        { if (!value) throw new InvalidOperationException("Unchecked inherited source: " + message); }
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        static BirExpressionStatement Statement(IBirExpression value) => new() { Expr = E(value) };
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var erased = new TypeNode.Fqn("object");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var text = new TypeNode.Fqn("kotlin.String");
        var anyNullable = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        BirConstant Null() => new() { Type = anyNullable, Value = new BirLiteral.NullLiteral() };
        var sourceCast = new BirCast { Type = ownerVariable, ReifiedTypeOperand = false, E = E(Null()) };
        var declaration = new BirFunctionDeclaration { Name = "read", DeclarationId = "inherited-unchecked",
            Params = Array.Empty<BirParameter>(), Ret = ownerVariable,
            Body = new IBirStatement[] { new BirReturn { Value = E(sourceCast) } } };
        var parent = new BirClassDefinition { Name = "UncheckedBase", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { declaration },
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = ownerVariable } } } } };
        var derived = new BirClassDefinition { Name = "UncheckedDerived", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Base = new TypeNode.Fqn("UncheckedBase", new TypeNode[] { ownerVariable }),
            Methods = Array.Empty<BirFunctionDeclaration>(),
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = ownerVariable } },
                BaseArgs = new IBirExpression[] { new BirLocal { Name = "value", Sty = ownerVariable } } } } };
        var calls = new List<(BirCallInstance Call, IBirExpression Receiver, TypeNode Argument)>();
        var locals = new List<(BirVariable Local, BirCallStatic Sink, TypeNode Logical, bool Deferred)>();
        BirCallInstance Read(TypeNode type, IBirExpression receiver)
        {
            var call = new BirCallInstance { OwnerType = new TypeNode.Fqn("UncheckedDerived", new[] { type }),
                Method = "read", DeclarationId = "inherited-unchecked", Recv = E(receiver),
                Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(), Ret = type, Sty = type };
            calls.Add((call, receiver, type));
            return call;
        }
        BirValueBlock Block(string name, TypeNode type, Func<IBirExpression> receiver,
            string result = "unit", bool deferred = true)
        {
            var local = new BirVariable { Name = name, Type = type, Init = E(deferred
                ? Read(type, receiver()) : new BirCast { Type = type, ReifiedTypeOperand = false, E = E(Null()) }) };
            var sink = new BirCallStatic { Owner = new TypeNode.Fqn("UncheckedUses"), Method = "consume",
                TypeArgs = new[] { type }, Sig = new TypeNode[] { methodVariable },
                Ret = unit, Args = new IBirExpression[] { new BirLocal { Name = name, Sty = type } } };
            locals.Add((local, sink, type, deferred));
            IBirExpression output = result == "receiver" ? receiver() : result == "value"
                ? new BirCast { Type = type, ReifiedTypeOperand = false, E = E(new BirLocal { Name = name, Sty = type }) }
                : new BirStaticFieldGet { OwnerType = unit, Name = "INSTANCE" };
            return new BirValueBlock { Type = result == "receiver" ? new TypeNode.Fqn("UncheckedDerived", new[] { type })
                    : result == "value" ? type : unit,
                Stmts = new IBirStatement[] { local, Statement(sink) }, Result = E(output) };
        }
        IBirExpression OwnerReceiver() => new BirLocal { Name = "source",
            Sty = new TypeNode.Fqn("UncheckedDerived", new TypeNode[] { ownerVariable }) };
        IBirExpression MethodReceiver() => new BirLocal { Name = "source",
            Sty = new TypeNode.Fqn("UncheckedDerived", new TypeNode[] { methodVariable }) };
        var primary = new BirConstructor { Params = new[] { new BirParameter { Name = "source",
                Type = new TypeNode.Fqn("UncheckedDerived", new TypeNode[] { ownerVariable }) } },
            BaseArgs = new IBirExpression[] { Block("baseValue", ownerVariable, OwnerReceiver, result: "value") },
            Body = Array.Empty<IBirStatement>() };
        var secondary = new BirConstructor { Params = new[] {
                new BirParameter { Name = "source", Type = new TypeNode.Fqn("UncheckedDerived", new TypeNode[] { ownerVariable }) },
                new BirParameter { Name = "tag", Type = new TypeNode.Fqn("kotlin.Int") } },
            PreStmts = new IBirStatement[] { Statement(Block("preValue", ownerVariable, OwnerReceiver)) },
            ThisArgs = new IBirExpression[] { Block("thisValue", ownerVariable, OwnerReceiver, result: "receiver") },
            Body = new IBirStatement[] { Statement(Block("bodyValue", ownerVariable, OwnerReceiver)) } };
        // Equal local spellings are deliberate independent initializer scopes.
        var firstField = new BirFieldDeclaration { Name = "first", Type = unit,
            Init = E(Block("shared", ownerVariable, OwnerReceiver)) };
        var secondField = new BirFieldDeclaration { Name = "second", Type = unit,
            Init = E(Block("shared", ownerVariable, OwnerReceiver, deferred: false)) };
        var consumer = new BirClassDefinition { Name = "UncheckedConsumer", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Base = new TypeNode.Fqn("UncheckedBase", new TypeNode[] { ownerVariable }),
            Ctors = new[] { primary, secondary }, Fields = new[] { firstField, secondField } };
        var caller = new BirFunctionDeclaration { Name = "use", Ret = unit,
            TypeParams = new[] { new BirTypeParameter { Name = "R" } },
            Params = new[] { new BirParameter { Name = "source",
                Type = new TypeNode.Fqn("UncheckedDerived", new TypeNode[] { methodVariable }) } },
            Body = new IBirStatement[] { Statement(Block("methodValue", methodVariable, MethodReceiver)) } };
        var sinkDeclaration = new BirFunctionDeclaration { Name = "consume", Static = true, Ret = unit,
            TypeParams = new[] { new BirTypeParameter { Name = "V" } },
            Params = new[] { new BirParameter { Name = "value", Type = methodVariable } },
            Body = Array.Empty<IBirStatement>() };
        var fileReceiver = new BirNew { Type = new TypeNode.Fqn("UncheckedDerived", new[] { text }),
            Args = new IBirExpression[] { new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("seed") } } };
        var fileRead = Read(text, fileReceiver);
        fileRead.Ret = default; // Already selected frontend String view, no result hint yet.
        var fileField = new BirFieldDeclaration { Name = "stored", Type = text, Init = E(fileRead) };
        var file = new BirFileDeclaration { FileClass = "UncheckedUses", Methods = new[] { sinkDeclaration, caller },
            Fields = new[] { fileField }, Types = new IBirTypeDeclaration[] { parent, derived, consumer } };
        var pending = CaptureSource(new[] { file });
        var index = pending.SourceDeclarations;
        var row = index.ByIdentity["inherited-unchecked"];
        Apply(file, pending); // Actual initial declaration return erasure, BEFORE final owner binding.
        Need(declaration.Ret.Value == erased && row.Return == ownerVariable
            && locals.All(item => item.Local.Type.Value == item.Logical),
            "initial flow guessed the declaration through a derived owner");
        var beforeDeclaration = declaration.DeepClone();
        var beforeReturn = row.Return;
        var beforeCount = index.ByIdentity.Count;
        InheritedMemberOwnerBinding.ApplyAll(new[] { file }, refs);
        Need(calls.All(item => item.Call.OwnerType.Value == new TypeNode.Fqn("UncheckedBase", new[] { item.Argument })
            && ReferenceEquals(item.Call.Recv.Value, item.Receiver)), "actual inherited owner transition lost exact frames/receivers");
        ApplyAfterInheritedOwnerBinding(file, pending);
        Need(ReferenceEquals(index, pending.SourceDeclarations) && index.ByIdentity.Count == beforeCount
            && ReferenceEquals(index.ByIdentity["inherited-unchecked"], row) && ReferenceEquals(row.SourceMethod, declaration)
            && row.Return == beforeReturn && declaration.StructurallyEquals(beforeDeclaration),
            "calls-only continuation replaced its catalog/row or reran declaration/body erasure");
        foreach (var item in locals)
            Need(item.Local.Type.Value == (item.Deferred ? erased : item.Logical)
                && (item.Deferred
                    ? item.Sink.Args.Value[0] is BirCast cast && cast.Type.Value == item.Logical
                        && cast.E.Value is BirLocal local && local.Name.Value == item.Local.Name.Value
                    : item.Sink.Args.Value[0] is BirLocal),
                "method/constructor/field use or independent initializer deferred-local scope was lost");
        Need(fileRead.Ret.Value == text && ReferenceEquals(fileField.Init.Value, fileRead)
            && secondary.ThisArgs.HasValue && !secondary.BaseArgs.IsPresent
            && !primary.ThisArgs.IsPresent && primary.BaseArgs.HasValue,
            "file field or constructor delegation presence was not continued");
        var once = file.DeepClone();
        ApplyAfterInheritedOwnerBinding(file, pending);
        Need(file.StructurallyEquals(once) && ReferenceEquals(row.SourceMethod, declaration),
            "calls-only continuation is not idempotent");
        Console.WriteLine("[unchecked inherited source] self-test OK (same captured rows, actual inherited owner, deferred locals, ctor regions, field scopes and unchanged declarations)");
    }
}
