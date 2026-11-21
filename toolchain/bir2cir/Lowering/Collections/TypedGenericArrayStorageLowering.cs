using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The late physical array operations consume System.Array storage already chosen
// by representation lowering. Earlier array construction/type-check owners are
// still separate; this walker does not reinterpret their Kotlin type policy.
static class TypedGenericArrayStorageLowering
{
    static TypeNode.Fqn Type(string name) => new(name);
    // Synthetic local reads can occur at several use sites. Actual operands are
    // moved once: copying them would detach any captured callable declarations
    // from their already-established source bindings.
    static IBirExpression UseOperand(IBirExpression expression) => expression is BirLocal local
        ? (IBirExpression)local.Rewrite(value => value) : expression;
    static BirLocal Local(string name, TypeNode type) => new() { Name = name, Sty = type };
    static BirConstant Int(int value) => new() { Type = Type("System.Int32"), Value = new BirLiteral.IntegerLiteral(value) };
    static BirVariable Var(string name, TypeNode type, IBirExpression value) => new() {
        Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(value),
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

    internal const string RuntimeOwner = "DotKt.Runtime.CompilerServices.ProjectedArrayStorageKt";
    internal static BirCallStatic Runtime(string method, TypeNode result, TypeNode[] signature, bool external, params IBirExpression[] operands)
    {
        var owner = Type(RuntimeOwner);
        var call = new BirCallStatic { Owner = owner, Method = method, Sig = signature, Ret = result, Args = operands };
        if (external) call.MemberRef = new BirMemberReference { Assembly = "DotKt.Stdlib", Kind = "method",
            DeclaringType = owner, Name = method, GenericArity = 0, CallingConvention = "static",
            ParameterTypes = signature, ReturnType = result };
        return call;
    }

    internal static BirCast Read(IBirExpression receiver, IBirExpression index, TypeNode result, bool external) => new() {
        Type = result, Sty = result, E = Runtime("readProjectedArray", Type("System.Object"),
            [Type("System.Array"), Type("System.Int32")], external, receiver, index),
    };

    internal static BirCallStatic Store(BirArraySet store, bool external) =>
        Runtime("writeProjectedArray", Type("void"), [Type("System.Array"), Type("System.Int32"), Type("System.Object")],
            external, store.Array.Value, store.Index.Value, new BirCast { Type = Type("System.Object"), E = store.Value });

    // A vector whose *actual* component is an ordinary non-generic class
    // stores its values directly. A covariantly viewed Derived<T>[] may
    // need logical storage, even when the static Base is locally declared.
    // Evaluate all Kotlin operands before inspecting the runtime vector.
    internal static BirValueBlock PlainClassAccess(IBirExpression receiver, IBirExpression offset,
        IBirExpression value, TypeNode component, BirTemporaryNames names, bool external)
    {
        var vector = new TypeNode.Array(component);
        var array = Var(names.Next("$plainArray$"), Type("System.Array"), receiver);
        var position = Var(names.Next("$plainIndex$"), Type("System.Int32"), offset);
        var statements = new List<IBirStatement> { array, position };
        BirLocal ReadVar(BirVariable v) => Local(v.Name.Value, v.Type.Value);
        BirCast ExactVector() => new() { Type = vector, E = ReadVar(array) };
        IBirExpression direct, projected;
        if (value == null)
        {
            direct = new BirArrayGet { Array = ExactVector(), Index = ReadVar(position), Elem = component };
            projected = Read(ReadVar(array), ReadVar(position), component, external);
        }
        else
        {
            var item = Var(names.Next("$plainValue$"), Type("System.Object"), value);
            statements.Add(item);
            direct = new BirArraySet { Array = ExactVector(), Index = ReadVar(position), Elem = component,
                Value = new BirCast { Type = component, E = ReadVar(item) } };
            projected = Runtime("writeProjectedArray", Type("void"),
                [Type("System.Array"), Type("System.Int32"), Type("System.Object")],
                external, ReadVar(array), ReadVar(position), ReadVar(item));
        }
        var result = value == null ? component : Type("void");
        return new BirValueBlock { Type = result, Stmts = statements.ToArray(), Result = new BirConditional {
            Type = result, Cond = new BirBinaryOperation { Op = "==", Lhs = new BirRuntimeClass { E = ReadVar(array) },
                Rhs = new BirClassReference { Type = vector } }, Then = OptionalField<IBirExpression>.Of(direct),
            Else = OptionalField<IBirExpression>.Of(projected) } };
    }

    internal static BirGraphValue LowerOperation(BirGraphValue node, ReferenceMetadataIndex refs,
        BirTemporaryNames names, TypeNode receiverType = null, bool external = false)
    {
        if (node is BirArrayGet { SourceClrBoundary: true }) return node;
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
            BirArrayGet value => Read(receiver, value.Index.Value, value.Elem.Value, external),
            BirArraySet value => Store(value, external),
            BirArrayLength => (BirGraphValue)Call(refs, "get_Length", receiver, "System.Int32", Array.Empty<string>()),
            BirForArray value => Loop(value, receiver, refs, names, (a, i) => Read(a, i, value.Elem.Value, external)),
            _ => node,
        };
    }

    internal static BirBlock Loop(BirForArray loop, IBirExpression receiver, ReferenceMetadataIndex refs, BirTemporaryNames names,
        Func<IBirExpression, IBirExpression, IBirExpression> readElement)
    {
        var arrayName = names.Next("$arrayStorage$");
        var indexName = names.Next("$arrayIndex$");
        var lengthName = names.Next("$arrayLength$");
        var arrayType = Type("System.Array");
        var intType = Type("System.Int32");
        var body = new IBirStatement[] {
            Var(loop.Var.Value, loop.Elem.Value,
                readElement(Local(arrayName, arrayType), Local(indexName, intType))),
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
        var get = (BirArrayGet)source[0];
        var read = (BirCast)LowerOperation(get, refs, new BirTemporaryNames(get));
        Require(read.Type.Value == element && read.Sty.Value == element && read.E.Value is BirCallStatic
            { Method.Value: "readProjectedArray", Args.Value: [var vector, var position] }
            && ReferenceEquals(vector, get.Array.Value) && ReferenceEquals(position, get.Index.Value), "lost read operands or element cast");
        var store = (BirArraySet)source[1];
        var write = (BirCallStatic)LowerOperation(store, refs, new BirTemporaryNames(store));
        Require(write.Method.Value == "writeProjectedArray" && write.Args.Value is [var target, var offset, BirCast boxed]
            && ReferenceEquals(target, store.Array.Value) && ReferenceEquals(offset, store.Index.Value)
            && ReferenceEquals(boxed.E.Value, store.Value.Value), "lost ordered store operands");

        var length = (BirArrayLength)source[2];
        var lengthCall = Instance((IBirExpression)LowerOperation(length, refs, new BirTemporaryNames(length)),
            "System.Array", "get_Length", "System.Int32", "call");
        Require(lengthCall.Recv.Value is BirLocal { Name.Value: "array" } && lengthCall.Args.Value.Length == 0,
            "length lost its exact receiver/empty argument vector");
        var loop = (BirForArray)source[3];
        var loweredLoop = (BirBlock)LowerOperation(loop, refs, new BirTemporaryNames(loop));
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
        var loopRead = (BirCallStatic)((BirCast)((BirVariable)counted.Body.Value[0]).Init.Value).E.Value;
        Require(loopRead.Method.Value == "readProjectedArray" && loopRead.Args.Value is [BirLocal vectorRead, BirLocal indexRead]
            && vectorRead.Name.Value == ((BirVariable)loweredLoop.Body.Value[0]).Name.Value && indexRead.Name.Value == counted.Var.Value,
            "loop element read lost its captured array/index");
        var labelled = new BirForArray { Array = Local("array", array), Var = "element", Elem = element,
            Label = "outer", Body = Array.Empty<IBirStatement>() };
        Require(((BirFor)((BirBlock)LowerOperation(labelled, refs, new BirTemporaryNames(labelled))).Body.Value[2]).Label.Value == "outer",
            "loop label was not retained");
        var file = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Name = "later", Params = new[] { new BirParameter { Name = "$arrayStorage$0", Type = array } },
            Body = new IBirStatement[] { new BirForArray { Var = "$arrayIndex$0" } },
        } } };
        var names = new BirTemporaryNames(file);
        if (names.Next("$arrayStorage$") != "$arrayStorage$1" || names.Next("$arrayIndex$") != "$arrayIndex$1")
            throw new InvalidOperationException("Typed array names missed a later parameter/loop declaration");
        names.Reserve(new BirLocal { Name = "$arrayStorage$2" });
        if (names.Next("$arrayStorage$") != "$arrayStorage$3"
            || new BirTemporaryNames(new BirFileDeclaration()).Next("$arrayStorage$") != "$arrayStorage$0")
            throw new InvalidOperationException("Typed array reservation leaked between rewrite/file lifetimes");
        var lazy = new BirFileDeclaration();
        var lazyNames = new BirTemporaryNames(lazy);
        lazy.Fields = new[] { new BirFieldDeclaration { Name = "$arrayValue$0", Type = element } };
        if (lazyNames.Next("$arrayValue$") != "$arrayValue$1")
            throw new InvalidOperationException("Typed array names seeded before their first allocation");
        var structural = new BirVariable { Name = "declared", Type = new TypeNode.Fqn("$arrayStorage$0"),
            Init = new BirCast { Type = new TypeNode.Array(new TypeNode.Fqn("$arrayStorage$1")),
                E = new BirLocal { Name = "$arrayStorage$2" } } };
        if (new BirTemporaryNames(structural).Next("$arrayStorage$") != "$arrayStorage$3")
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
            LowerOperation(operation, refs, new BirTemporaryNames(operation), array).Visit(node => {
                if (node is BirFunctionDeclaration callable) observed.Add(callable);
            });
        if (observed.Count != callables.Count || callables.Any(callable =>
                observed.Count(actual => ReferenceEquals(actual, callable)) != 1))
            throw new InvalidOperationException("Typed array lowering copied or lost an owning callable operand/body");
        Console.WriteLine("[typed array operations] self-test OK (exact signatures, names, storage checks, ordered values, owning bodies)");
    }
}
