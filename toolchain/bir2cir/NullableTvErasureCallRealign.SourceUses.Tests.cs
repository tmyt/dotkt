using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    internal static void SelfTestSourceUses()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var erased = new TypeNode.Fqn("object");
        var nullable = new TypeNode.Nullable(integer);
        var methodVariable = new TypeNode.Tv("method", 0);
        var ownerVariable = new TypeNode.Tv("type", 0);
        static BirParameter Parameter(string name, TypeNode type) => new() { Name = name, Type = type };
        static BirFunctionDeclaration Method(string name, string id, TypeNode result, params BirParameter[] parameters)
            => new() { Name = name, DeclarationId = id, Ret = result, Params = parameters, Static = true };
        static BirVariable Local(string name, TypeNode type, IBirExpression value)
            => new() { Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(value) };
        static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Typed nullable source-use flow: " + reason); }
        var settleDeclaration = Method("settle", "settle-id", methodVariable,
            Parameter("value", methodVariable), Parameter("values", new TypeNode.Array(methodVariable)));
        settleDeclaration.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        var holderType = new TypeNode.Fqn("Holder", new TypeNode[] { new TypeNode.Nullable(methodVariable) });
        var accept = Method("accept", "accept-id", new TypeNode.Fqn("void"), Parameter("holder", holderType));
        accept.TypeParams = settleDeclaration.TypeParams;
        var box = new BirClassDefinition { Name = "Box", Kind = "class", TypeParams = settleDeclaration.TypeParams,
            Fields = new[] { new BirFieldDeclaration { Name = "value", Type = new TypeNode.Nullable(ownerVariable) } },
            Methods = new[] { Method("array", "array-id", new TypeNode.Array(new TypeNode.Nullable(ownerVariable))) } };
        box.Methods.Value[0].Static = false;
        var holder = new BirClassDefinition { Name = "Holder", Kind = "class", TypeParams = settleDeclaration.TypeParams,
            Ctors = new[] { new BirConstructor { Params = new[] { Parameter("value", ownerVariable) } } } };
        var sourceArray = new TypeNode.Array(nullable);
        var settle = new BirCallStatic { Method = "settle", DeclarationId = "settle-id", OwnerType = new TypeNode.Fqn("Facade"),
            TypeArgs = new TypeNode[] { nullable }, Ret = nullable, Sty = nullable,
            Sig = new TypeNode[] { methodVariable, new TypeNode.Array(methodVariable) },
            Args = new IBirExpression[] { new BirLocal { Name = "value", Sty = nullable },
                new BirLocal { Name = "values", Sty = new TypeNode.Array(erased) } } };
        var construction = new BirNew { Type = new TypeNode.Fqn("Holder", new TypeNode[] { nullable }),
            ArgTypes = new TypeNode[] { nullable }, Args = new IBirExpression[] { new BirLocal { Name = "value", Sty = nullable } } };
        var acceptCall = new BirCallStatic { OwnerType = new TypeNode.Fqn("Facade"), Method = "accept", DeclarationId = "accept-id",
            TypeArgs = new TypeNode[] { integer }, Args = new IBirExpression[] { construction } };
        var arrayCall = new BirCallInstance { OwnerType = new TypeNode.Fqn("Box", new TypeNode[] { text }),
            Method = "array", DeclarationId = "array-id", Args = Array.Empty<IBirExpression>(),
            Recv = new BirLocal { Name = "box", Sty = new TypeNode.Fqn("Box", new TypeNode[] { text }) },
            Ret = new TypeNode.Array(new TypeNode.Nullable(text)), Sty = new TypeNode.Array(new TypeNode.Nullable(text)) };
        var arrayVariable = Local("array", arrayCall.Sty.Value, arrayCall);
        var field = new BirFieldGet { OwnerType = new TypeNode.Fqn("Box", new TypeNode[] { integer }), Name = "value",
            Type = nullable, Sty = nullable, Recv = new BirLocal { Name = "intBox" } };
        var store = new BirSetField { OwnerType = field.OwnerType, Name = "value", Recv = new BirLocal { Name = "intBox" },
            Value = new BirLocal { Name = "value", Sty = nullable } };
        var readElement = new BirArrayGet { Array = new BirLocal { Name = "values", Sty = sourceArray },
            Index = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) }, Elem = nullable };
        var loopRead = new BirLocal { Name = "element", Sty = nullable };
        var loopReturn = new BirReturn { Value = loopRead };
        var loop = new BirForArray { Var = "element", Elem = nullable, Array = new BirLocal { Name = "values", Sty = sourceArray },
            Body = new IBirStatement[] { loopReturn } };
        var caller = Method("use", "use-id", nullable, Parameter("value", nullable),
            Parameter("values", new TypeNode.Array(erased)), Parameter("box", arrayCall.OwnerType.Value),
            Parameter("intBox", field.OwnerType.Value));
        caller.Body = new IBirStatement[] { Local("settled", erased, settle),
            Local("accepted", new TypeNode.Fqn("void"), acceptCall), arrayVariable, Local("read", erased, field), store,
            Local("elementRead", erased, readElement), loop };
        var file = new BirFileDeclaration { FileClass = "Facade", Methods = new[] { settleDeclaration, accept, caller },
            Types = new IBirTypeDeclaration[] { box, holder } };
        var index = CollectDeclaredMemberRets(new[] { file });
        Require(ReferenceEquals(ApplySourceUses(file, index, type => type.Name == "kotlin.Int", null), file), "root identity");
        Require(ReferenceEquals(file.Methods.Value[2], caller) && ReferenceEquals(file.Types.Value[0], box)
            && caller.DeclarationId.Value == "use-id" && caller.Params.Value[0].Type.Value == nullable,
            "body flow replaced declaration/parameter authority");
        Require(settle.TypeArgs.Value[0] == erased && settle.Ret.Value == erased && settle.Sty.Value == erased
            && settle.Args.Value[0] is BirCast { Type.Value: var firstTarget } && firstTarget == erased
            && settle.Sig.Value[0] == methodVariable, "two-phase instantiation or open descriptor");
        Require(((TypeNode.Fqn)construction.Type.Value).Args[0] == erased
            && construction.Args.Value[0] is BirCast { Type.Value: var ctorTarget } && ctorTarget == erased,
            "construction was not retyped before constructor argument flow");
        Require(arrayVariable.Init.Value is BirCast { E.Value: BirCallInstance inner, Type.Value: var semantic }
            && ReferenceEquals(inner, arrayCall) && inner.Ret.Value == new TypeNode.Array(erased)
            && semantic == new TypeNode.Array(new TypeNode.Nullable(text)), "reference-array result projection");
        Require(field.Type.Value == erased && field.Sty.Value == erased
            && store.Value.Value is BirCast { Type.Value: var fieldTarget } && fieldTarget == erased,
            "field read/write did not share the indexed declaration");
        Require(readElement.Elem.Value == erased && loop.Elem.Value == erased
            && loopRead.Sty.Value == erased && loopReturn.Value.Value is BirCast { Type.Value: var returnTarget }
            && returnTarget == nullable, "array element, loop binder or return target");
        var once = file.DeepClone();
        ApplySourceUses(file, index, type => type.Name == "kotlin.Int", null);
        Require(file.StructurallyEquals(once), "source-use flow is not idempotent");
        SourceUseFixedConsumersSelfTest();
        SourceUseDelegateConstructionSelfTest();
        SourceUseSuspendBodySelfTest();
        SourceUseVisibilitySelfTest();
        SourceUseMaterializedSelfTest();
        Console.WriteLine("[typed nullable source uses] self-test OK (two-phase arguments, current frames, constructors, result projections, slots, loop flow and visibility clock)");
    }

    static void SourceUseFixedConsumersSelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var nullable = new TypeNode.Nullable(integer);
        var erased = new TypeNode.Fqn("object");
        var text = new TypeNode.Fqn("kotlin.String");
        var semanticFunction = new TypeNode.Fn(false, nullable, new TypeNode[] { nullable });
        var physicalFunction = new TypeNode.Fn(false, erased, new TypeNode[] { erased });
        var invoke = new BirDelegateInvoke { Recv = new BirLocal { Name = "function", Sty = semanticFunction },
            FuncType = semanticFunction, Args = new IBirExpression[] { new BirLocal { Name = "value", Sty = nullable } } };
        var native = new BirNativeInstance { Type = integer, Method = "read", Recv = new BirLocal { Name = "value", Sty = nullable },
            Args = new IBirExpression[] { new BirLocal { Name = "erased", Sty = erased } }, ArgTypes = new TypeNode[] { text }, Ret = text };
        var join = new BirConditional { Type = nullable,
            Cond = new BirConstant { Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(true) },
            Then = new BirLocal { Name = "erased", Sty = erased }, Else = new BirLocal { Name = "value", Sty = nullable } };
        var hasValue = new BirNullableHasValue { Elem = integer, E = new BirLocal { Name = "erased", Sty = erased } };
        var sharedReturn = new BirReturn { Value = new BirLocal { Name = "erased", Sty = erased } };
        var caller = new BirFunctionDeclaration { Name = "use", Ret = nullable,
            Params = new[] { new BirParameter { Name = "value", Type = nullable }, new BirParameter { Name = "erased", Type = erased },
                new BirParameter { Name = "function", Type = physicalFunction } },
            Body = new IBirStatement[] { new BirVariable { Name = "invoke", Type = erased, Init = invoke },
                new BirVariable { Name = "native", Type = text, Init = native }, new BirVariable { Name = "join", Type = nullable, Init = join },
                new BirVariable { Name = "present", Type = new TypeNode.Fqn("kotlin.Boolean"), Init = hasValue }, sharedReturn } };
        var constructor = new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = nullable } },
            BaseArgs = new IBirExpression[] { new BirLocal { Name = "value", Sty = nullable } }, Body = Array.Empty<IBirStatement>() };
        var baseType = new BirClassDefinition { Kind = "class", Name = "Base", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter {
                Name = "value", Type = new TypeNode.Nullable(new TypeNode.Tv("type", 0)) } } } } };
        var derived = new BirClassDefinition { Kind = "class", Name = "Derived", Base = new TypeNode.Fqn("Base", new TypeNode[] { integer }),
            Ctors = new[] { constructor } };
        var file = new BirFileDeclaration { Methods = new[] { caller }, Types = new IBirTypeDeclaration[] { baseType, derived } };
        var index = CollectDeclaredMemberRets(new[] { file });
        ApplySourceUses(file, index, type => type.Name == "kotlin.Int", null);
        if (invoke.FuncType.Value != physicalFunction || invoke.Args.Value[0] is not BirCast { Type.Value: var argument } || argument != erased
            || native.Recv.Value is not BirNullableValue || native.Args.Value[0] is not BirCast { Type.Value: var nativeArgument }
            || nativeArgument != text || join.Then.Value is not BirCast { Type.Value: var joined } || joined != nullable
            || hasValue.E.Value is not BirCast { Type.Value: var tested } || tested != nullable
            || sharedReturn.Value.Value is not BirCast { Type.Value: var returned } || returned != nullable
            || constructor.BaseArgs.Value[0] is not BirCast { Type.Value: var delegated } || delegated != erased)
            throw new InvalidOperationException("Typed source-use flow lost a native/delegate/join/nullable/return/constructor fixed slot");
        var once = file.DeepClone();
        ApplySourceUses(file, index, type => type.Name == "kotlin.Int", null);
        if (!file.StructurallyEquals(once)) throw new InvalidOperationException("Typed fixed consumer flow is not idempotent");
    }

    static void SourceUseDelegateConstructionSelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var nullable = new TypeNode.Nullable(integer);
        var erased = new TypeNode.Fqn("object");
        var text = new TypeNode.Fqn("kotlin.String");
        var natural = new TypeNode.Fn(false, text, new TypeNode[] { nullable });
        var expected = new TypeNode.Fn(false, text, new TypeNode[] { erased });
        var declaration = new BirFunctionDeclaration { Name = "accept", DeclarationId = "accept-id", Static = true,
            Ret = new TypeNode.Fqn("void"), TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "action", Type = new TypeNode.Fn(false, text,
                new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("method", 0)) }) } } };
        var selected = new BirNewDelegate { Method = "target", FuncType = natural, Sig = new TypeNode[] { nullable } };
        var generated = new BirNewDelegate { Method = "generated", FuncType = natural };
        var closure = new BirNewClosure { Method = "invoke", FuncType = natural };
        BirVariable Use(string name, IBirExpression construction) => new() { Name = name, Type = new TypeNode.Fqn("void"),
            Init = new BirCallStatic { Method = "accept", DeclarationId = "accept-id", OwnerType = new TypeNode.Fqn("Facade"),
                TypeArgs = new TypeNode[] { integer }, Args = new[] { construction } } };
        var caller = new BirFunctionDeclaration { Name = "caller", Body = new IBirStatement[] {
            Use("selected", selected), Use("generated", generated), Use("closure", closure) } };
        var file = new BirFileDeclaration { FileClass = "Facade", Methods = new[] { declaration, caller } };
        var index = CollectDeclaredMemberRets(new[] { file });
        ApplySourceUses(file, index, type => type.Name == "kotlin.Int", null);
        if (selected.FuncType.Value != natural || selected.Sig.Value[0] != nullable
            || generated.FuncType.Value != expected || closure.FuncType.Value != expected)
            throw new InvalidOperationException("Source-use delegate retyping ignored the selected signature or skipped an owned target");
    }

    static void SourceUseSuspendBodySelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var nullable = new TypeNode.Nullable(integer);
        var erased = new TypeNode.Fqn("object");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var ownerType = new TypeNode.Fqn("Cell", new TypeNode[] { integer });
        var target = new BirFunctionDeclaration { Name = "read", DeclarationId = "read-id",
            Ret = new TypeNode.Nullable(ownerVariable), Params = Array.Empty<BirParameter>() };
        var owner = new BirClassDefinition { Name = "Cell", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { target } };
        var call = new BirCallInstance { OwnerType = ownerType, DeclarationId = "read-id", Method = "read",
            Recv = new BirLocal { Name = "captured", Sty = ownerType }, Args = Array.Empty<IBirExpression>(),
            Ret = nullable, Sty = nullable };
        var bodyValue = new BirVariable { Name = "result", Type = erased, Init = call };
        var suspension = new BirNewSuspendLambda { Arity = 0, SuspendRet = erased,
            Params = Array.Empty<BirLambdaParameter>(), Body = new IBirStatement[] { bodyValue },
            Captures = new[] { new BirCaptureDescriptor { Name = "captured", Type = ownerType } },
            CapValues = new IBirExpression[] { new BirLocal { Name = "captured", Sty = ownerType } },
            FuncType = new TypeNode.Fn(true, erased, Array.Empty<TypeNode>()) };
        var caller = new BirFunctionDeclaration { Name = "make", Params = new[] {
            new BirParameter { Name = "captured", Type = ownerType } },
            Body = new IBirStatement[] { new BirVariable { Name = "block", Type = suspension.FuncType.Value, Init = suspension } } };
        var file = new BirFileDeclaration { Methods = new[] { caller }, Types = new IBirTypeDeclaration[] { owner } };
        var index = CollectDeclaredMemberRets(new[] { file });
        ApplySourceUses(file, index, type => type.Name == "kotlin.Int", null);
        if (!ReferenceEquals(suspension.Body.Value[0], bodyValue) || call.Ret.Value != erased || call.Sty.Value != erased
            || !ReferenceEquals(bodyValue.Init.Value, call) || suspension.CapValues.Value[0] is not BirLocal)
            throw new InvalidOperationException("Typed source-use flow skipped the live suspend-lambda body or replaced its construction operands");
    }

    static void SourceUseVisibilitySelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var nullable = new TypeNode.Nullable(integer);
        var variable = new TypeNode.Tv("type", 0);
        var erased = new TypeNode.Fqn("object");
        var declaration = new BirFunctionDeclaration { Name = "read", DeclarationId = "read-id", Ret = new TypeNode.Nullable(variable),
            Params = Array.Empty<BirParameter>(), PropertyName = "value", PropertyAccessor = "get" };
        var owner = new BirClassDefinition { Name = "Future", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Methods = new[] { declaration }, Fields = new[] { new BirFieldDeclaration { Name = "value", Type = new TypeNode.Nullable(variable) } },
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Nullable(variable) } } } } };
        var canonical = CollectDeclaredMemberRets(Array.Empty<BirFileDeclaration>());
        var before = canonical.BindSourceVisibility();
        var snapshot = CaptureNewSyntheticTypes(new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } }, canonical);
        ApplyNewSyntheticTypes(snapshot, canonical);
        var after = canonical.BindSourceVisibility();
        if (before.IsVisibleOwner("Future") || !after.IsVisibleOwner("Future")
            || !canonical.IsVisibleOwner("Future") || !ReferenceEquals(before.ByOwner, after.ByOwner)
            || !ReferenceEquals(canonical.ByOwner["Future"]["read|0"], after.ByOwner["Future"]["read|0"]))
            throw new InvalidOperationException("Source visibility copied or exposed a future declaration authority");
        foreach (var binding in new[] { before, after, canonical })
        {
            var application = new TypeNode.Fqn("Future", new TypeNode[] { integer });
            var read = new BirCallInstance { Method = "read", DeclarationId = "read-id", OwnerType = application,
                Recv = new BirLocal { Name = "receiver", Sty = application }, Args = Array.Empty<IBirExpression>(), Ret = nullable };
            // The exact-ID read never enters local name/arity fallback. Keep a
            // separate no-ID use so removing that lookup's ceiling is detected.
            var nameRead = (BirCallInstance)read.DeepClone();
            nameRead.DeclarationId = default;
            var property = (BirCallInstance)read.DeepClone();
            property.PropertyName = "value"; property.PropertyAccessor = "get";
            var field = new BirFieldGet { OwnerType = application, Name = "value", Type = nullable };
            var construction = new BirNew { Type = application, ArgTypes = new TypeNode[] { nullable },
                Args = new IBirExpression[] { new BirLocal { Name = "value", Sty = nullable } } };
            var method = new BirFunctionDeclaration { Name = "caller", Ret = new TypeNode.Fqn("void"),
                Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
                    new BirVariable { Name = "read", Type = erased, Init = read },
                    new BirVariable { Name = "property", Type = erased, Init = property },
                    new BirVariable { Name = "field", Type = erased, Init = field },
                    new BirVariable { Name = "construction", Type = application, Init = construction },
                    new BirVariable { Name = "nameRead", Type = erased, Init = nameRead },
                } };
            // Test real base-argument flow through the visibility view; this
            // does not claim a producer for a hidden owner's this-delegation.
            var delegated = new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = nullable } },
                BaseArgs = new IBirExpression[] { new BirLocal { Name = "value", Sty = nullable } },
                Body = Array.Empty<IBirStatement>() };
            var derived = new BirClassDefinition { Name = "Derived", Kind = "class", Base = application,
                Ctors = new[] { delegated } };
            var source = new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] { derived } };
            // This is the still-live materialized/delegate capability boundary,
            // not an oracle for the retired initial DOM entry.
            var late = source.ToDom();
            ApplyAfterDelegateSlotAlignment(late, binding, type => type.Name == "kotlin.Int", null);
            ApplySourceUses(source, binding, type => type.Name == "kotlin.Int", null);
            var visible = !ReferenceEquals(binding, before);
            if ((read.Ret.Value == erased) != visible || (property.Ret.Value == erased) != visible
                || (field.Type.Value == erased) != visible || (construction.Args.Value[0] is BirCast) != visible
                || nameRead.Ret.Value != (visible ? erased : nullable)
                || (delegated.BaseArgs.Value[0] is BirCast) != visible
                || delegated.BaseArgs.Value[0] is BirCast baseCast && baseCast.Type.Value != erased)
                throw new InvalidOperationException("Owner/identity/property/constructor/slot visibility differs across phase bindings");
            var lateRead = late["methods"][0]["body"][0]["init"];
            var lateConstructorArgument = late["methods"][0]["body"][3]["init"]["args"][0];
            var lateNameRead = late["methods"][0]["body"][4]["init"];
            if (Str(lateNameRead["k"]) == "cast") lateNameRead = lateNameRead["e"];
            var lateBaseArgument = late["types"][0]["ctors"][0]["baseArgs"][0];
            if ((TypeJson.Read(lateRead["ret"]) == erased) != visible
                || (lateConstructorArgument["k"]?.GetValue<string>() == "cast") != visible
                || TypeJson.Read(lateNameRead["ret"]) != (visible ? erased : nullable)
                || (Str(lateBaseArgument["k"]) == "cast") != visible
                || visible && TypeJson.Read(lateBaseArgument["type"]) != erased)
                throw new InvalidOperationException("Late declared-use capability did not retain its per-file visibility ceiling");
        }
        var order = canonical.SourceApplicationOrder;
        order.Add(new[] { new InnerApplicationOrder.DeclarationFact("Inner", true, 1, "Outer") });
        var first = canonical.BindSourceVisibility();
        order.Add(new[] { new InnerApplicationOrder.DeclarationFact("Inner", true, 1, "Outer") });
        order.Add(new[] { new InnerApplicationOrder.DeclarationFact("Inner", true, 2, "Outer") });
        var second = canonical.BindSourceVisibility();
        var applicationType = new TypeNode.Fqn("Inner", new TypeNode[] {
            new TypeNode.Fqn("A"), new TypeNode.Fqn("B"), new TypeNode.Fqn("C"),
        });
        var oldOrder = first.SourceApplicationOrder.DeclarationArguments(applicationType);
        var newOrder = second.SourceApplicationOrder.DeclarationArguments(applicationType);
        if (((TypeNode.Fqn)oldOrder[0]).Name != "C" || ((TypeNode.Fqn)newOrder[0]).Name != "B"
            || !newOrder.SequenceEqual(order.DeclarationArguments(applicationType)))
            throw new InvalidOperationException("Inner owner shape updates escaped their captured phase visibility");
    }
}
