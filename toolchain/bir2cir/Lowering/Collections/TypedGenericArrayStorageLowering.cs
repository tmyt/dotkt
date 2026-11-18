using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The late physical array operations consume System.Array storage already chosen
// by representation lowering. Earlier array construction/type-check owners are
// still separate; this walker does not reinterpret their Kotlin type policy.
static class TypedGenericArrayStorageLowering
{
    internal sealed class FreshNames(BirGraphValue file)
    {
        readonly HashSet<string> names = new(StringComparer.Ordinal);
        BirGraphValue seed = file;

        internal void Reserve(BirGraphValue root)
        {
            root?.Visit(value => value.VisitNameFields(name => names.Add(name)));
        }

        internal string Next(string stem)
        {
            if (seed != null) { Reserve(seed); seed = null; }
            var index = 0;
            while (!names.Add(stem + index)) index++;
            return stem + index;
        }
    }

    static TypeNode.Fqn Type(string name) => new(name);
    // Synthetic local reads can occur at several use sites. Actual operands are
    // moved once: copying them would detach any captured callable declarations
    // from their already-established source bindings.
    static IBirExpression UseOperand(IBirExpression expression) => expression is BirLocal local
        ? (IBirExpression)local.Rewrite(value => value) : expression;
    static BirLocal Local(string name, TypeNode type) => new() { Name = name, Sty = type };
    static BirConstant Int(int value) => new() { Type = Type("System.Int32"), Value = new BirLiteral.IntegerLiteral(value) };
    static BirConstant Null() => new() { Type = Type("System.Object"), Value = new BirLiteral.NullLiteral() };
    static BirVariable Var(string name, TypeNode type, IBirExpression value) => new() {
        Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(value),
    };
    static BirBinaryOperation EqualNull(IBirExpression value) => new() {
        Op = "===", Lhs = OptionalField<IBirExpression>.Of(value), Rhs = Null(),
    };
    static BirUnaryOperation Not(IBirExpression value) => new() { Op = "!", E = OptionalField<IBirExpression>.Of(value) };
    static BirIf If(IBirExpression condition, params IBirStatement[] body) => new() {
        Branches = new[] { new BirBranch { Cond = OptionalField<IBirExpression>.Of(condition), Body = body } },
    };

    static IBirExpression RuntimeCall(ReferenceMetadataIndex refs, string owner, string method,
        IBirExpression receiver, string result, string[] parameters, params IBirExpression[] arguments)
    {
        var plan = ClrMemberResolution.PlanArrayStorageCall(null, refs, method, parameters, owner, receiver == null);
        var reference = BirMemberReference.FromPhysical(plan.Reference);
        var args = arguments.Select(UseOperand).ToArray();
        var parameterTypes = parameters.Select(name => (TypeNode)Type(name)).ToArray();
        if (receiver == null) return new BirNativeStatic {
            Type = Type(owner), Method = method, Recv = OptionalField<IBirExpression>.Null,
            Args = args, ArgTypes = parameterTypes, Ret = Type(result), Sty = Type(result),
            Dispatch = plan.Dispatch, MemberRef = reference,
        };
        return new BirNativeInstance {
            Type = Type(owner), Method = method, Recv = OptionalField<IBirExpression>.Of(UseOperand(receiver)), Args = args,
            ArgTypes = parameterTypes, Ret = Type(result), Sty = Type(result),
            Dispatch = plan.Dispatch, MemberRef = reference,
        };
    }

    static IBirExpression Call(ReferenceMetadataIndex refs, string method, IBirExpression receiver,
        string result, string[] parameters, params IBirExpression[] arguments) =>
        RuntimeCall(refs, "System.Array", method, receiver, result, parameters, arguments);

    static BirCast Read(ReferenceMetadataIndex refs, IBirExpression receiver, IBirExpression index, TypeNode result) => new() {
        Type = result, Sty = result,
        E = OptionalField<IBirExpression>.Of(Call(refs, "GetValue", receiver, "object", new[] { "System.Int32" }, index)),
    };

    internal static BirGraphValue LowerOperation(BirGraphValue node, ReferenceMetadataIndex refs,
        FreshNames names, TypeNode receiverType = null)
    {
        var receiver = node switch {
            BirArrayGet value => value.Array.OrDefault(),
            BirArraySet value => value.Array.OrDefault(),
            BirArrayLength value => value.Array.OrDefault(),
            BirForArray value => value.Array.OrDefault(),
            _ => null,
        };
        if (receiver == null) return node;
        receiverType ??= (receiver as BirGraphValue)?.ResultTypeStamp
            ?? (receiver switch {
                BirLocal value => value.Sty.OrDefault(),
                BirConstant value => value.Type.OrDefault(),
                BirCast value => value.Type.OrDefault(),
                BirFieldGet value => value.Type.OrDefault() ?? value.MemberType.OrDefault(),
                _ => null,
            });
        if (receiverType is TypeNode.Nullable nullable) receiverType = nullable.Of;
        if (receiverType is not TypeNode.Fqn { Name: "System.Array" }) return node;
        return node switch {
            BirArrayGet value => Read(refs, receiver, value.Index.Value, value.Elem.Value),
            BirArraySet value => Store(value, receiver, refs, names),
            BirArrayLength => (BirGraphValue)Call(refs, "get_Length", receiver, "System.Int32", Array.Empty<string>()),
            BirForArray value => Loop(value, receiver, refs, names),
            _ => node,
        };
    }

    static BirValueBlock Store(BirArraySet store, IBirExpression receiver, ReferenceMetadataIndex refs, FreshNames names)
    {
        var arrayName = names.Next("$arrayStorage$");
        var indexName = names.Next("$arrayIndex$");
        var valueName = names.Next("$arrayValue$");
        var componentName = names.Next("$arrayComponent$");
        var arrayType = Type("System.Array");
        var intType = Type("System.Int32");
        var objectType = Type("System.Object");
        var typeType = Type("System.Type");
        var exceptionOwner = refs.ResolveRefType("System.ArrayTypeMismatchException");
        var exceptionConstructor = exceptionOwner.GetConstructor(System.Type.EmptyTypes);
        BirThrow Failure() => new() { Value = new BirNewClr {
            Type = Type("System.ArrayTypeMismatchException"), Args = Array.Empty<IBirExpression>(),
            MemberRef = BirMemberReference.FromPhysical(
                ClrMemberResolution.MemberRefOf(null, refs, exceptionConstructor, "ctor", exceptionOwner, null)),
        } };
        var component = Local(componentName, typeType);
        var value = Local(valueName, objectType);
        var typeCheck = If(Not(EqualNull(UseOperand(value))),
            If(Not(RuntimeCall(refs, "System.Type", "IsInstanceOfType", component, "System.Boolean",
                new[] { "System.Object" }, value)), Failure()));
        var nullCheck = If(EqualNull(UseOperand(value)),
            If(RuntimeCall(refs, "System.Type", "get_IsValueType", component, "System.Boolean", Array.Empty<string>()),
                If(EqualNull(RuntimeCall(refs, "System.Nullable", "GetUnderlyingType", null, "System.Type",
                    new[] { "System.Type" }, component)), Failure())));
        return new BirValueBlock {
            Type = Type("void"), Stmts = new IBirStatement[] {
                Var(arrayName, arrayType, UseOperand(receiver)),
                Var(indexName, intType, UseOperand(store.Index.Value)),
                Var(valueName, objectType, UseOperand(store.Value.Value)),
                Var(componentName, typeType, RuntimeCall(refs, "System.Type", "GetElementType",
                    RuntimeCall(refs, "System.Object", "GetType", Local(arrayName, arrayType),
                        "System.Type", Array.Empty<string>()), "System.Type", Array.Empty<string>())),
                typeCheck, nullCheck,
            },
            Result = OptionalField<IBirExpression>.Of(Call(refs, "SetValue", Local(arrayName, arrayType), "void",
                new[] { "System.Object", "System.Int32" }, value, Local(indexName, intType))),
        };
    }

    static BirBlock Loop(BirForArray loop, IBirExpression receiver, ReferenceMetadataIndex refs, FreshNames names)
    {
        var arrayName = names.Next("$arrayStorage$");
        var indexName = names.Next("$arrayIndex$");
        var lengthName = names.Next("$arrayLength$");
        var arrayType = Type("System.Array");
        var intType = Type("System.Int32");
        var body = new IBirStatement[] {
            Var(loop.Var.Value, loop.Elem.Value,
                Read(refs, Local(arrayName, arrayType), Local(indexName, intType), loop.Elem.Value)),
        }.Concat(loop.Body.OrEmpty()).ToArray();
        return new BirBlock { Body = new IBirStatement[] {
            Var(arrayName, arrayType, UseOperand(receiver)),
            Var(lengthName, intType, Call(refs, "get_Length", Local(arrayName, arrayType),
                "System.Int32", Array.Empty<string>())),
            new BirFor { Label = loop.Label.IsPresent ? loop.Label : OptionalField<string>.Null,
                Var = indexName, From = Int(0), To = Local(lengthName, intType), Cmp = "<", Step = 1, Body = body },
        } };
    }

    internal static void SelfTest(ReferenceMetadataIndex refs)
    {
        var array = Type("System.Array");
        var integer = Type("System.Int32");
        var element = Type("System.String");
        if (ClrMemberResolution.PlanArrayStorageCall(null, refs, "get_Length", Array.Empty<string>()).Dispatch != "call"
            || ClrMemberResolution.PlanArrayStorageCall(null, refs, "GetValue", new[] { "System.Int32" }).Dispatch != "call"
            || ClrMemberResolution.PlanArrayStorageCall(null, refs, "GetElementType", Array.Empty<string>(), "System.Type").Dispatch != "callvirt")
            throw new InvalidOperationException("Array storage calls lost their selected nonvirtual/virtual dispatch");
        var source = new BirGraphValue[] {
            new BirArrayGet { Array = Local("array", array), Index = Int(2), Elem = element },
            new BirArraySet { Array = Local("receiverEffect", array), Index = Local("indexEffect", integer),
                Value = Local("valueEffect", element), Elem = element },
            new BirArrayLength { Array = Local("array", array) },
            new BirForArray { Array = Local("array", array), Var = "element", Elem = element,
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = Local("element", element) } } },
        };
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed array storage: " + message); }
        static BirNativeInstance Instance(IBirExpression expression, string owner, string method,
            string result, string dispatch, params string[] parameters)
        {
            if (expression is not BirNativeInstance call) throw new InvalidOperationException("Expected native array call " + method);
            Require(call.Type.OrDefault() == Type(owner) && call.Method.OrDefault() == method
                && call.Ret.OrDefault() == Type(result) && call.Sty.OrDefault() == Type(result)
                && call.Dispatch.OrDefault() == dispatch && call.Args.HasValue && call.Args.Value.Length == parameters.Length
                && call.ArgTypes.HasValue && call.ArgTypes.Value.SequenceEqual(parameters.Select(Type))
                && call.MemberRef.HasValue && call.MemberRef.Value.Kind.OrDefault() == "method"
                && call.MemberRef.Value.DeclaringType.OrDefault() == Type(owner)
                && call.MemberRef.Value.Name.OrDefault() == method
                && call.MemberRef.Value.ParameterTypes.HasValue
                && call.MemberRef.Value.ParameterTypes.Value.SequenceEqual(parameters.Select(Type))
                && call.MemberRef.Value.ReturnType.HasValue,
                "exact native signature/result/dispatch changed: " + method);
            return call;
        }
        static BirBranch Branch(IBirStatement statement) => statement is BirIf { Branches.Value: [var branch] }
            ? branch : throw new InvalidOperationException("Expected single array-check branch");
        static IBirExpression Negated(IBirExpression expression) => expression is BirUnaryOperation { Op.Value: "!", E.HasValue: true } negation
            ? negation.E.Value : throw new InvalidOperationException("Expected negated array check");
        static bool NullTest(IBirExpression expression, string name) => expression is BirBinaryOperation {
            Op.Value: "===", Lhs.Value: BirLocal { Name.Value: var actual },
            Rhs.Value: BirConstant { Value.Value: BirLiteral.NullLiteral } } && actual == name;
        static void Failure(IBirStatement statement)
        {
            Require(statement is BirThrow { Value.Value: BirNewClr {
                Type.Value: TypeNode.Fqn { Name: "System.ArrayTypeMismatchException" }, Args.Value: [],
                MemberRef.Value: { Kind.Value: "ctor", DeclaringType.Value: TypeNode.Fqn { Name: "System.ArrayTypeMismatchException" },
                    ParameterTypes.Value: [] } } }, "array mismatch lost its exact exception constructor");
        }
        var get = (BirArrayGet)source[0];
        var read = (BirCast)LowerOperation(get, refs, new FreshNames(get));
        Require(read.Type.Value == element && read.Sty.Value == element, "read lost the authored element cast");
        var readCall = Instance(read.E.Value, "System.Array", "GetValue", "object", "call", "System.Int32");
        Require(readCall.Recv.Value is BirLocal { Name.Value: "array" }
            && ReferenceEquals(readCall.Args.Value[0], get.Index.Value), "read lost receiver/index transport");

        var store = (BirArraySet)source[1];
        var stored = (BirValueBlock)LowerOperation(store, refs, new FreshNames(store));
        var spills = stored.Stmts.Value;
        Require(stored.Type.Value == Type("void") && spills.Length == 6, "store lost ordered spill/check block");
        var arraySpill = (BirVariable)spills[0]; var indexSpill = (BirVariable)spills[1];
        var valueSpill = (BirVariable)spills[2]; var componentSpill = (BirVariable)spills[3];
        Require(arraySpill.Type.Value == array && indexSpill.Type.Value == integer && valueSpill.Type.Value == Type("System.Object")
            && componentSpill.Type.Value == Type("System.Type")
            && new[] { arraySpill.Init.Value, indexSpill.Init.Value, valueSpill.Init.Value }
                .Cast<BirLocal>().Select(local => local.Name.Value).SequenceEqual(new[] { "receiverEffect", "indexEffect", "valueEffect" }),
            "store receiver/index/value evaluation order or slot types changed");
        var componentCall = Instance(componentSpill.Init.Value, "System.Type", "GetElementType", "System.Type", "callvirt");
        var runtimeType = Instance(componentCall.Recv.Value, "System.Object", "GetType", "System.Type", "call");
        Require(runtimeType.Recv.Value is BirLocal { Name.Value: var allocated } && allocated == arraySpill.Name.Value,
            "component lookup does not use the single captured allocation");
        var nonnull = Branch(spills[4]);
        Require(NullTest(Negated(nonnull.Cond.Value), valueSpill.Name.Value), "nonnull store guard changed");
        var mismatch = Branch(nonnull.Body.Value.Single());
        var typeCheck = Instance(Negated(mismatch.Cond.Value), "System.Type", "IsInstanceOfType", "System.Boolean", "callvirt", "System.Object");
        Require(((BirLocal)typeCheck.Recv.Value).Name.Value == componentSpill.Name.Value
            && ((BirLocal)typeCheck.Args.Value.Single()).Name.Value == valueSpill.Name.Value, "component guard checked another allocation/value");
        Failure(mismatch.Body.Value.Single());
        var nullValue = Branch(spills[5]);
        Require(NullTest(nullValue.Cond.Value, valueSpill.Name.Value), "null store guard changed");
        var valueType = Branch(nullValue.Body.Value.Single());
        var isValueType = Instance(valueType.Cond.Value, "System.Type", "get_IsValueType", "System.Boolean", "call");
        Require(((BirLocal)isValueType.Recv.Value).Name.Value == componentSpill.Name.Value, "value-array guard lost its component");
        var nonnullable = Branch(valueType.Body.Value.Single());
        Require(nonnullable.Cond.Value is BirBinaryOperation { Op.Value: "===", Rhs.Value: BirConstant { Value.Value: BirLiteral.NullLiteral },
            Lhs.Value: BirNativeStatic { Type.Value: TypeNode.Fqn { Name: "System.Nullable" }, Method.Value: "GetUnderlyingType",
                Dispatch.Value: "call", Args.Value: [BirLocal { Name.Value: var nullableComponent }],
                MemberRef.Value: { Name.Value: "GetUnderlyingType", ParameterTypes.Value: [TypeNode.Fqn { Name: "System.Type" }] } } }
                && nullableComponent == componentSpill.Name.Value, "null rejection lost its nullable value-type exception");
        Failure(nonnullable.Body.Value.Single());
        var writeCall = Instance(stored.Result.Value, "System.Array", "SetValue", "void", "call", "System.Object", "System.Int32");
        Require(((BirLocal)writeCall.Recv.Value).Name.Value == arraySpill.Name.Value
            && writeCall.Args.Value.Cast<BirLocal>().Select(local => local.Name.Value)
                .SequenceEqual(new[] { valueSpill.Name.Value, indexSpill.Name.Value }), "SetValue argument order changed");

        var length = (BirArrayLength)source[2];
        var lengthCall = Instance((IBirExpression)LowerOperation(length, refs, new FreshNames(length)),
            "System.Array", "get_Length", "System.Int32", "call");
        Require(lengthCall.Recv.Value is BirLocal { Name.Value: "array" } && lengthCall.Args.Value.Length == 0,
            "length lost its exact receiver/empty argument vector");
        var loop = (BirForArray)source[3];
        var loweredLoop = (BirBlock)LowerOperation(loop, refs, new FreshNames(loop));
        Require(loweredLoop.Body.Value is [BirVariable { Type.Value: var receiverType }, BirVariable { Type.Value: var lengthType },
            BirFor { Cmp.Value: "<", Step.Value: 1, From.Value: BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 0 } } }]
            && receiverType == array && lengthType == integer, "array loop lost counted receiver/length layout");
        var counted = (BirFor)loweredLoop.Body.Value[2];
        var loopLength = (BirVariable)loweredLoop.Body.Value[1];
        _ = Instance(loopLength.Init.Value, "System.Array", "get_Length", "System.Int32", "call");
        Require(counted.Label.IsPresent && !counted.Label.HasValue
            && ((BirLocal)counted.To.Value).Name.Value == loopLength.Name.Value
            && counted.Body.Value[0] is BirVariable { Name.Value: "element", Type.Value: var itemType,
                Init.Value: BirCast { Type.Value: var castType } } && itemType == element && castType == element
            && ReferenceEquals(counted.Body.Value[1], loop.Body.Value.Single()), "loop lost label, element binding or original body ownership");
        var loopRead = Instance(((BirCast)((BirVariable)counted.Body.Value[0]).Init.Value).E.Value,
            "System.Array", "GetValue", "object", "call", "System.Int32");
        Require(((BirLocal)loopRead.Recv.Value).Name.Value == ((BirVariable)loweredLoop.Body.Value[0]).Name.Value
            && ((BirLocal)loopRead.Args.Value.Single()).Name.Value == counted.Var.Value,
            "loop element read lost its captured array/index");
        var labelled = new BirForArray { Array = Local("array", array), Var = "element", Elem = element,
            Label = "outer", Body = Array.Empty<IBirStatement>() };
        Require(((BirFor)((BirBlock)LowerOperation(labelled, refs, new FreshNames(labelled))).Body.Value[2]).Label.Value == "outer",
            "loop label was not retained");
        var file = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Name = "later", Params = new[] { new BirParameter { Name = "$arrayStorage$0", Type = array } },
            Body = new IBirStatement[] { new BirForArray { Var = "$arrayIndex$0" } },
        } } };
        var names = new FreshNames(file);
        if (names.Next("$arrayStorage$") != "$arrayStorage$1" || names.Next("$arrayIndex$") != "$arrayIndex$1")
            throw new InvalidOperationException("Typed array names missed a later parameter/loop declaration");
        names.Reserve(new BirLocal { Name = "$arrayStorage$2" });
        if (names.Next("$arrayStorage$") != "$arrayStorage$3"
            || new FreshNames(new BirFileDeclaration()).Next("$arrayStorage$") != "$arrayStorage$0")
            throw new InvalidOperationException("Typed array reservation leaked between rewrite/file lifetimes");
        var lazy = new BirFileDeclaration();
        var lazyNames = new FreshNames(lazy);
        lazy.Fields = new[] { new BirFieldDeclaration { Name = "$arrayValue$0", Type = element } };
        if (lazyNames.Next("$arrayValue$") != "$arrayValue$1")
            throw new InvalidOperationException("Typed array names seeded before their first allocation");
        var structural = new BirVariable { Name = "declared", Type = new TypeNode.Fqn("$arrayStorage$0"),
            Init = new BirCast { Type = new TypeNode.Array(new TypeNode.Fqn("$arrayStorage$1")),
                E = new BirLocal { Name = "$arrayStorage$2" } } };
        if (new FreshNames(structural).Next("$arrayStorage$") != "$arrayStorage$3")
            throw new InvalidOperationException("Array allocation names omitted a structured type/name field");
        var callables = new List<BirFunctionDeclaration>();
        BirValueBlock Operand(TypeNode type)
        {
            var callable = new BirFunctionDeclaration { Name = "nested" + callables.Count,
                Params = Array.Empty<BirParameter>(), Ret = element, Body = Array.Empty<IBirStatement>() };
            callables.Add(callable);
            return new BirValueBlock { Type = type,
                Stmts = new IBirStatement[] { new BirLocalFunction { Id = callable.Name.Value, Decl = callable } },
                Result = Local("operand", type) };
        }
        var operands = new BirGraphValue[] {
            new BirArrayGet { Array = Operand(array), Index = Operand(integer), Elem = element },
            new BirArraySet { Array = Operand(array), Index = Operand(integer), Value = Operand(element), Elem = element },
            new BirArrayLength { Array = Operand(array) },
            new BirForArray { Array = Operand(array), Var = "item", Elem = element,
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = Operand(element) } } },
        };
        var observed = new List<BirFunctionDeclaration>();
        foreach (var operation in operands)
            LowerOperation(operation, refs, new FreshNames(operation), array).Visit(node => {
                if (node is BirFunctionDeclaration callable) observed.Add(callable);
            });
        if (observed.Count != callables.Count || callables.Any(callable =>
                observed.Count(actual => ReferenceEquals(actual, callable)) != 1))
            throw new InvalidOperationException("Typed array lowering copied or lost an owning callable operand/body");
        Console.WriteLine("[typed array operations] self-test OK (exact signatures, names, storage checks, ordered values, owning bodies)");
    }
}
