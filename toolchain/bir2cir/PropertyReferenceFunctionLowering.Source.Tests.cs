using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PropertyReferenceFunctionLowering
{
    internal static void SelfTestSource()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var function = new TypeNode.Fn(false, text, new TypeNode[] { integer });
        var propertyInterface = new TypeNode.Fqn("kotlin.reflect.KProperty1", new TypeNode[] { integer, text });
        BirNew Property() => new() { Type = new TypeNode.Fqn("AuthoredProperty"), ArgTypes = Array.Empty<TypeNode>(),
            Args = Array.Empty<IBirExpression>(), PropertyFunction = new BirPropertyFunction { Iface = BirGraphValue.CopyType(propertyInterface) } };
        var property = Property();
        var siblingSlot = new BirFieldDeclaration { Name = "callback", Type = function };
        var sibling = new BirFileDeclaration { FileClass = "Sibling", Fields = new[] { siblingSlot } };
        var snapshot = CollectFieldSlots(new[] { sibling });
        Require(snapshot[("Sibling", "callback")] is TypeNode.Fn capturedFunction
            && !ReferenceEquals(capturedFunction.Params, function.Params), "field signature snapshot owns nested vectors");
        var initial = new BirVariable { Name = "callback", Type = function, Init = Property() };
        var assigned = new BirSetLocal { Name = "callback", Value = Property() };
        var invokeSource = new BirCallInstance {
            OwnerType = propertyInterface, Method = "invoke", Sty = text,
            Recv = new BirCast { Type = propertyInterface, E = new BirLocal { Name = "callback", Sty = propertyInterface } },
            Args = new IBirExpression[] { new BirLocal { Name = "argument", Sty = integer } },
        };
        var invocation = new BirExpressionStatement { Expr = invokeSource };
        var crossFile = new BirStaticFieldSet { OwnerType = new TypeNode.Fqn("Sibling"), Name = "callback", Value = property };
        var join = new BirConditional { Type = function, Cond = new BirLocal { Name = "condition" }, Then = Property(), Else = Property() };
        var returned = new BirReturn { Value = join };
        var array = new BirNewArray { Elem = function, Elems = new IBirExpression[] { Property() } };
        var arrayVariable = new BirVariable { Name = "array", Type = new TypeNode.Array(function), Init = array };
        var cast = new BirCast { Type = function, E = Property() };
        var castVariable = new BirVariable { Name = "cast", Type = function, Init = cast };
        var native = new BirGenericStatic { Type = new TypeNode.Fqn("NativeApi"), Method = "Consume",
            TypeArgs = new TypeNode[] { function }, ResolvedMemberParams = new TypeNode[] { new TypeNode.Tv("method", 0) },
            Args = new IBirExpression[] { Property() } };
        var nativeStatement = new BirExpressionStatement { Expr = native };
        var ownerCall = new BirCallInstance { OwnerType = new TypeNode.Fqn("GenericOwner", new TypeNode[] { function }),
            Method = "Consume", Recv = new BirLocal { Name = "owner" },
            ShapeTypes = new TypeNode[] { new TypeNode.Tv("type", 0) },
            ArgTypes = new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, Sig = Array.Empty<TypeNode>(),
            Args = new IBirExpression[] { Property() } };
        var ownerStatement = new BirExpressionStatement { Expr = ownerCall };
        var body = new IBirStatement[] { initial, assigned, invocation, new BirExpressionStatement { Expr = crossFile },
            returned, arrayVariable, castVariable, nativeStatement, ownerStatement };
        var method = new BirFunctionDeclaration { Name = "make", Ret = function, Body = body,
            Params = new[] { new BirParameter { Name = "argument", Type = integer } } };
        var occupied = new BirClassDefinition { Name = "dotkt$Owning_File$PropertyFunctionAdapter0" };
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>(),
            BaseArgs = new IBirExpression[] { Property() }, DelegationSig = new TypeNode[] { function } };
        var hierarchyArguments = new TypeNode[] { new TypeNode.Tv("type", 0) };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Ctors = new[] { constructor },
            Base = new TypeNode.Fqn("Base", hierarchyArguments),
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } },
            Methods = new[] { new BirFunctionDeclaration { Name = "toString", ObjectOverride = true } } };
        var file = new BirFileDeclaration { FileClass = "Owning.File", Methods = new[] { method },
            Types = new IBirTypeDeclaration[] { occupied, owner } };
        var methods = file.Methods.Value;
        var declarations = file.Types.Value;
        var objectSlots = AnySlotRebind.CollectLocalTypes(new[] { file });
        var downcasts = GenericDowncastRealignment.Collect(new[] { file });
        var iterable = SupertypeGraph.CollectSource(new[] { file });
        Require(ReferenceEquals(Apply(file, snapshot), file) && ReferenceEquals(file.Methods.Value, methods)
            && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(file.Types.Value[0], declarations[0])
            && ReferenceEquals(file.Types.Value[1], owner), "original declaration, statement vector and source record identities");
        Require(initial.Init.Value is BirNewClosure && assigned.Value.Value is BirNewClosure
            && crossFile.Value.Value is BirNewClosure && join.Then.Value is BirNewClosure && join.Else.Value is BirNewClosure
            && array.Elems.Value[0] is BirNewClosure && cast.E.Value is BirNewClosure
            && native.Args.Value[0] is BirNewClosure && ownerCall.Args.Value[0] is BirNewClosure
            && constructor.BaseArgs.Value[0] is BirNewClosure, "all declared slots consume property values");
        Require(invocation.Expr.Value is BirDelegateInvoke lowered && lowered.Recv.Value is BirLocal { Name.HasValue: true } local
            && local.Name.Value == "callback" && lowered.Sty.OrDefault() == text,
            "mutable function invocation uses declared function storage and retains result stamp");
        var synthesized = file.Types.Value.OfType<BirClassDefinition>().Where(t => t.Generated.OrDefault()).ToArray();
        Require(synthesized.Length == 10 && synthesized[0].Name.Value == "dotkt$Owning_File$PropertyFunctionAdapter1"
            && synthesized.Select(t => t.Name.Value).Distinct().Count() == 10, "single adaptation, append order and allocator collision avoidance");
        Require(objectSlots.Keys.SequenceEqual(new[] { "Owner" })
            && objectSlots["Owner"].DeclaredSlots.SetEquals(new[] { "ToString" })
            && downcasts.Count == 2 && downcasts["Owner"].Arity == 1 && iterable["Owner"].Arity == 2
            && synthesized.All(type => !downcasts.ContainsKey(type.Name.Value) && !iterable.ContainsKey(type.Name.Value)
                && !objectSlots.ContainsKey(type.Name.Value)), "module inventories keep their pre-adaptation clock and distinct frame arities");
        hierarchyArguments[0] = new TypeNode.Fqn("Changed");
        Require(downcasts["Owner"].Base.Args[0] == new TypeNode.Tv("type", 0), "downcast hierarchy snapshot owns nested type vectors");
        var captured = ((BirNewClosure)crossFile.Value.Value).Captures.Value;
        Require(captured.Length == 1 && captured[0] is BirNew capture && !capture.PropertyFunction.IsPresent
            && !ReferenceEquals(capture, property) && property.PropertyFunction.HasValue,
            "capture is evaluated once and detached source is not mutated");
        var markers = 0;
        file.Visit(node => { if (node is BirNew { PropertyFunction.IsPresent: true }) markers++; });
        Require(markers == 0, "live markers consumed");

        // Different caller variables that occupy distinct adapter slots must not
        // collapse to one CLR frame. A property interface cannot allow a ref
        // struct, while a delegate-only slot keeps that permission.
        var tv0 = new TypeNode.Tv("method", 0);
        var tv1 = new TypeNode.Tv("method", 1);
        var genericTarget = new TypeNode.Fn(false, tv1, Array.Empty<TypeNode>());
        var genericProperty = new BirNew { Type = new TypeNode.Fqn("GenericProperty"),
            PropertyFunction = new BirPropertyFunction { Iface = new TypeNode.Fqn("kotlin.reflect.KProperty0", new TypeNode[] { tv0 }) } };
        var genericReturn = new BirReturn { Value = genericProperty };
        var genericFile = new BirFileDeclaration { FileClass = "Generic", Methods = new[] { new BirFunctionDeclaration {
            Name = "factory", Ret = genericTarget, TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" } },
            Body = new IBirStatement[] { genericReturn },
        } } };
        Apply(genericFile, snapshot);
        var genericClosure = (BirNewClosure)genericReturn.Value.Value;
        var genericAdapter = genericFile.Types.Value.Single();
        Require(genericClosure.TypeArgs.Value.SequenceEqual(new TypeNode[] { tv0, tv1 })
            && !genericAdapter.TypeParams.Value[0].SpecialConstraints.IsPresent
            && genericAdapter.TypeParams.Value[1].SpecialConstraints.Value.SequenceEqual(new[] { "allowsRefStruct" }),
            "dense frame order and per-slot ref-struct constraints");
        Require(genericAdapter.Methods.Value[0].Body.Value[0] is BirReturn { Value.HasValue: true } genericResult
            && genericResult.Value.Value is BirCast { Type.HasValue: true } resultCast && resultCast.Type.Value == new TypeNode.Tv("type", 1),
            "generic result adapter retains caller frame separation");
        // A temporary need not repeat its initializer's result type. Its outer
        // slot must not hide an inner call's fully declared function argument.
        var nested = new BirCallStatic { Method = "consume", Ret = text, Sig = new TypeNode[] { function },
            Args = new IBirExpression[] { Property() } };
        var temporary = new BirVariable { Name = "temporary", Init = nested };
        var inferred = new BirFileDeclaration { FileClass = "Inferred", Methods = new[] {
            new BirFunctionDeclaration { Name = "nested", Ret = text, Body = new IBirStatement[] { temporary } },
        } };
        Apply(inferred, snapshot);
        Require(!temporary.Type.IsPresent && nested.Args.Value[0] is BirNewClosure,
            "untyped enclosing storage stays transparent to a declared inner argument slot");
        Console.WriteLine("[typed property function slots] self-test OK (module fields, declared slots, generic adapters, ordered ownership, invocation and transparent containers)");

        static void Require(bool condition, string site)
        {
            if (!condition) throw new InvalidOperationException("Property function source self-test failed: " + site);
        }
    }
}
