using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableGenericErasure
{
    internal static void SourceSelfTest()
    {
        SourcePositionsSelfTest();
        SourceLoopSelfTest();
        SourceStampsSelfTest();
        Console.WriteLine("[nullable generic erasure] typed self-test OK (positions, source facts, loop arguments, stamps)");
    }

    static void SourceRequire(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static bool SourceSame(TypeNode actual, TypeNode expected) => TypeNodeDataEquality.Equal(actual, expected);
    static TypeNode.Fqn SourceType(string name, params TypeNode[] arguments)
        => new(name, arguments.Length == 0 ? null : arguments);
    static BirExpressionStatement SourceStatement(IBirExpression expression)
        => new() { Expr = OptionalField<IBirExpression>.Of(expression) };

    static void SourcePositionsSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var nullableVariable = new TypeNode.Nullable(variable);
        var nullableInteger = new TypeNode.Nullable(SourceType("kotlin.Int"));
        var list = SourceType("Box", nullableInteger);
        var erasedList = SourceType("Box", SourceType("object"));
        var nullableSlot = new BirParameter { Name = "optional", Type = nullableVariable };
        var concreteSlot = new BirParameter { Name = "number", Type = nullableInteger };
        var signature = new BirCallStatic { Owner = SourceType("Target"), Method = "Use",
            Sig = new TypeNode[] { nullableInteger, nullableVariable }, TypeArgs = new TypeNode[] { nullableInteger },
            Args = Array.Empty<IBirExpression>(), Ret = list };
        var foreign = new BirNativeStatic { Type = SourceType("Foreign"), Method = "Use",
            ArgTypes = new TypeNode[] { list, nullableVariable }, ResolvedMemberParams = new TypeNode[] { list },
            Sig = new TypeNode[] { list }, Args = Array.Empty<IBirExpression>() };
        var pointerType = SourceType(FunctionPointerVocabulary.Pointer, nullableInteger);
        var pointer = new BirParameter { Name = "pointer", Type = pointerType };
        var function = new TypeNode.Fn(false, nullableInteger, new TypeNode[] { nullableInteger, nullableVariable },
            nullableInteger, "Example.Delegate", new TypeNode[] { nullableInteger });
        var functionSlot = new BirParameter { Name = "fn", Type = function };
        var suspended = new TypeNode.Fn(true, nullableVariable, Array.Empty<TypeNode>());
        var suspendSlot = new BirParameter { Name = "suspendFn", Type = suspended };
        var array = new BirNewArraySized { Elem = nullableInteger };
        var construction = new BirNew { Type = SourceType("Carrier"), ArgTypes = new TypeNode[] { list } };
        var referent = new BirByrefLoad { Elem = list };
        var overrideEdge = new BirOverride { Owner = list, Member = "Use", Kind = "function", Arity = 0 };
        var constructorSlot = new BirParameter { Name = "initial", Type = nullableVariable };
        var field = new BirFieldDeclaration { Name = "value", Type = nullableVariable };
        var property = new BirPropertyDeclaration { Name = "items", Type = list };
        var method = new BirFunctionDeclaration { Name = "entry", Ret = nullableVariable, RetKotlinType = list,
            Params = new[] { nullableSlot, concreteSlot, pointer, functionSlot, suspendSlot },
            Overrides = new[] { overrideEdge },
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { list } } },
            Body = new IBirStatement[] { SourceStatement(signature), SourceStatement(foreign), SourceStatement(array),
                SourceStatement(construction), SourceStatement(referent) } };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { method },
            Fields = new[] { field }, Properties = new[] { property }, Base = list,
            Ctors = new[] { new BirConstructor { Params = new[] { constructorSlot } } } };
        var file = new BirFileDeclaration { FileClass = "Positions", Types = new IBirTypeDeclaration[] { owner } };
        Apply(file, type => type.Name == "kotlin.Int");
        SourceRequire(ReferenceEquals(file.Types.Value[0], owner) && ReferenceEquals(owner.Methods.Value[0], method)
            && ReferenceEquals(method.Params.Value[0], nullableSlot) && ReferenceEquals(((BirExpressionStatement)method.Body.Value[0]).Expr.Value, signature),
            "Nullable erasure replaced continuing declaration or expression records");
        SourceRequire(SourceSame(method.Ret.Value, SourceType("object"))
            && SourceSame(nullableSlot.Type.Value, SourceType("object")) && SourceSame(concreteSlot.Type.Value, nullableInteger)
            && SourceSame(field.Type.Value, SourceType("object")) && SourceSame(property.Type.Value, erasedList)
            && SourceSame(owner.Base.Value, erasedList) && SourceSame(constructorSlot.Type.Value, SourceType("object"))
            && SourceSame(array.Elem.Value, SourceType("object")) && SourceSame(overrideEdge.Owner.Value, list)
            && SourceSame(construction.ArgTypes.Value[0], erasedList) && SourceSame(referent.Elem.Value, erasedList),
            "Nullable declaration/array slots did not follow positional erasure or an override identity was rewritten");
        SourceRequire(SourceSame(signature.Sig.Value[0], nullableInteger) && SourceSame(signature.Sig.Value[1], SourceType("object"))
            && SourceSame(signature.TypeArgs.Value[0], SourceType("object")) && SourceSame(signature.Ret.Value, erasedList)
            && SourceSame(foreign.ArgTypes.Value[0], list) && SourceSame(foreign.ArgTypes.Value[1], SourceType("object"))
            && SourceSame(foreign.ResolvedMemberParams.Value[0], list) && SourceSame(foreign.Sig.Value[0], erasedList),
            "Call arguments, Kotlin signature slots, and bound CLR argument signatures were conflated");
        SourceRequire(SourceSame(pointer.Type.Value, pointerType)
            && functionSlot.Type.Value is TypeNode.Fn { Clr: "Example.Delegate" } physicalFunction
            && SourceSame(physicalFunction.Ret, SourceType("object")) && SourceSame(physicalFunction.Params[0], nullableInteger)
            && SourceSame(physicalFunction.Params[1], SourceType("object")) && SourceSame(physicalFunction.Recv, nullableInteger)
            && SourceSame(physicalFunction.Ctx.Single(), nullableInteger),
            "Pointer opacity or nominal function parameter/return positions changed");
        SourceRequire(SourceSame(method.NullableGenericRet.Value, nullableVariable) && SourceSame(method.RetKotlinType.Value, list)
            && SourceSame(nullableSlot.NullableGeneric.Value, nullableVariable) && nullableSlot.NullableFlags.HasValue
            && SourceSame(constructorSlot.NullableGeneric.Value, nullableVariable)
            && SourceSame(property.NullableGeneric.Value, list) && SourceSame(owner.KotlinSupertypesPre.Value.Base, list)
            && SourceSame(method.MethodTypeParameterBoundsPre.Value.Bounds[0].Single(), list)
            && SourceSame(method.TypeParams.Value[0].Constraints.Value.Single(), erasedList)
            && SourceSame(suspendSlot.SuspendFnTypePre.Value, suspended)
            && suspendSlot.Type.Value is TypeNode.Fn { Ret: TypeNode.Fqn { Name: "object" } },
            "Pre-erasure source metadata was consumed, overwritten, or left uncaptured");
        var sourceFacts = owner.KotlinSupertypesPre.Value;
        Apply(file, type => type.Name == "kotlin.Int");
        SourceRequire(ReferenceEquals(owner.KotlinSupertypesPre.Value, sourceFacts)
            && SourceSame(method.NullableGenericRet.Value, nullableVariable), "Repeated erasure replaced original source carriers");
    }

    static void SourceLoopSelfTest()
    {
        var element = new TypeNode.Tv("method", 0);
        var nullableElement = new TypeNode.Nullable(element);
        var argument = new BirLocal { Name = "item" };
        var receiver = new BirLocal { Name = "item" };
        var call = new BirCallInstance { Recv = receiver, Method = "Use", Args = new IBirExpression[] { argument } };
        var loop = new BirForEach { Src = new BirLocal { Name = "items" }, Var = "item", Elem = nullableElement,
            Body = new IBirStatement[] { SourceStatement(call) } };
        var other = new BirCallStatic { Owner = SourceType("Target"), Method = "Use",
            Args = new IBirExpression[] { new BirLocal { Name = "other" } } };
        var otherLoop = new BirForEach { Src = new BirLocal { Name = "otherItems" }, Var = "other", Elem = element,
            Body = new IBirStatement[] { SourceStatement(other) } };
        var method = new BirFunctionDeclaration { Name = "narrow", Params = new[] {
            new BirParameter { Name = "items", Type = SourceType("Iterable", nullableElement) },
            new BirParameter { Name = "otherItems", Type = SourceType("Iterable", element) } },
            Body = new IBirStatement[] { loop, otherLoop } };
        var file = new BirFileDeclaration { FileClass = "Loops", Methods = new[] { method } };
        Apply(file, _ => false);
        SourceRequire(ReferenceEquals(method.Body.Value[0], loop) && ReferenceEquals(call.Recv.Value, receiver)
            && SourceSame(loop.Elem.Value, SourceType("object"))
            && call.Args.Value[0] is BirCast { Type.HasValue: true, E.HasValue: true } cast
            && SourceSame(cast.Type.Value, element) && cast.E.Value is BirLocal { Name.Value: "item" } copied
            && !ReferenceEquals(copied, argument) && other.Args.Value[0] is BirLocal && SourceSame(otherLoop.Elem.Value, element),
            "Loop element erasure lost its pre-erasure argument narrowing or touched another operand/scope");
    }

    static void SourceStampsSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var result = SourceType("Box", new TypeNode.Nullable(variable));
        var stale = new BirCallStatic { Owner = SourceType("Target"), Method = "Read", Ret = result,
            Sty = SourceType("Box", SourceType("kotlin.String")) };
        var agrees = new BirCallStatic { Owner = SourceType("Target"), Method = "Read", Ret = result, Sty = result };
        var dynamic = new BirCallStatic { Owner = SourceType("Target"), Method = "Read",
            DynRet = result, Sty = SourceType("Box", SourceType("kotlin.String")) };
        var noChange = new BirCallStatic { Owner = SourceType("Target"), Method = "Read", Ret = SourceType("kotlin.String"),
            Sty = SourceType("kotlin.Int") };
        var explicitNull = new BirCallStatic { Owner = SourceType("Target"), Method = "Read", Ret = result,
            Sty = OptionalField<TypeNode>.Null };
        var method = new BirFunctionDeclaration { Name = "stamps", Body = new IBirStatement[] {
            SourceStatement(stale), SourceStatement(agrees), SourceStatement(dynamic), SourceStatement(noChange), SourceStatement(explicitNull) } };
        Apply(new BirFileDeclaration { FileClass = "Stamps", Methods = new[] { method } }, _ => false);
        SourceRequire(!stale.Sty.IsPresent && !dynamic.Sty.IsPresent && agrees.Sty.HasValue
            && SourceSame(agrees.Sty.Value, agrees.Ret.Value) && noChange.Sty.HasValue && explicitNull.Sty.IsNull,
            "Result erasure failed to consume only newly refuted stamps or lost explicit presence");
    }
}
