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
            root?.Visit(value => {
                var name = value switch {
                    EventPropertySourceFacts item => item.Name.OrDefault(),
                    BirFieldGet item => item.Name.OrDefault(),
                    BirStaticFieldGet item => item.Name.OrDefault(),
                    BirFieldSetExpression item => item.Name.OrDefault(),
                    BirStaticFieldSet item => item.Name.OrDefault(),
                    BirLateinitGet item => item.Name.OrDefault(),
                    BirAttributeNamedArgument item => item.Name.OrDefault(),
                    BirParameter item => item.Name.OrDefault(),
                    BirFunctionDeclaration item => item.Name.OrDefault(),
                    BirFieldDeclaration item => item.Name.OrDefault(),
                    BirRefTypeDeclaration item => item.Name.OrDefault(),
                    BirPropertyDeclaration item => item.Name.OrDefault(),
                    BirInterfaceDefinition item => item.Name.OrDefault(),
                    BirEnumEntryDeclaration item => item.Name.OrDefault(),
                    BirEnumDefinition item => item.Name.OrDefault(),
                    BirClassDefinition item => item.Name.OrDefault(),
                    BirKotlinCompanion item => item.Name.OrDefault(),
                    BirRichEnumEntry item => item.Name.OrDefault(),
                    BirRichEnumDeclaration item => item.Name.OrDefault(),
                    BirClrEventBacking item => item.Name.OrDefault(),
                    BirClrEventForwarder item => item.Name.OrDefault(),
                    BirCaptureDescriptor item => item.Name.OrDefault(),
                    BirClosureDefinition item => item.Name.OrDefault(),
                    BirEventGet item => item.Name.OrDefault(),
                    BirLambdaParameter item => item.Name.OrDefault(),
                    BirInlineCapture item => item.Name.OrDefault(),
                    BirCompanionValue item => item.Name.OrDefault(),
                    BirSetLocal item => item.Name.OrDefault(),
                    BirSetField item => item.Name.OrDefault(),
                    BirCatch item => item.Var.OrDefault(),
                    BirForArray item => item.Var.OrDefault(),
                    BirForIn item => item.Var.OrDefault(),
                    BirVariable item => item.Name.OrDefault(),
                    BirLocal item => item.Name.OrDefault(),
                    BirFor item => item.Var.OrDefault(),
                    BirForRange item => item.Var.OrDefault(),
                    BirEventAccessorBody item => item.Name.OrDefault(),
                    BirPropertyGet item => item.Name.OrDefault(),
                    BirPropertySet item => item.Name.OrDefault(),
                    BirForEach item => item.Var.OrDefault(),
                    BirRepeat item => item.Var.OrDefault(),
                    BirTypeParameter item => item.Name.OrDefault(),
                    BirMemberReference item => item.Name.OrDefault(),
                    _ => null,
                };
                if (name != null) names.Add(name);
            });
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
    static IBirExpression Copy(IBirExpression expression) =>
        (IBirExpression)((BirGraphValue)expression).Rewrite(value => value);
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
        var reference = BirMemberReference.FromPhysical(
            ClrMemberResolution.ArrayStorageMethodOf(refs, method, parameters, owner, receiver == null));
        var args = arguments.Select(Copy).ToArray();
        var parameterTypes = parameters.Select(name => (TypeNode)Type(name)).ToArray();
        if (receiver == null) return new BirNativeStatic {
            Type = Type(owner), Method = method, Recv = OptionalField<IBirExpression>.Null,
            Args = args, ArgTypes = parameterTypes, Ret = Type(result), Sty = Type(result),
            Dispatch = "call", MemberRef = reference,
        };
        return new BirNativeInstance {
            Type = Type(owner), Method = method, Recv = OptionalField<IBirExpression>.Of(Copy(receiver)), Args = args,
            ArgTypes = parameterTypes, Ret = Type(result), Sty = Type(result),
            Dispatch = "callvirt", MemberRef = reference,
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
                ClrMemberResolution.MemberRefOf(refs, exceptionConstructor, "ctor", exceptionOwner, null)),
        } };
        var component = Local(componentName, typeType);
        var value = Local(valueName, objectType);
        var typeCheck = If(Not(EqualNull(Copy(value))),
            If(Not(RuntimeCall(refs, "System.Type", "IsInstanceOfType", component, "System.Boolean",
                new[] { "System.Object" }, value)), Failure()));
        var nullCheck = If(EqualNull(Copy(value)),
            If(RuntimeCall(refs, "System.Type", "get_IsValueType", component, "System.Boolean", Array.Empty<string>()),
                If(EqualNull(RuntimeCall(refs, "System.Nullable", "GetUnderlyingType", null, "System.Type",
                    new[] { "System.Type" }, component)), Failure())));
        return new BirValueBlock {
            Type = Type("void"), Stmts = new IBirStatement[] {
                Var(arrayName, arrayType, Copy(receiver)),
                Var(indexName, intType, Copy(store.Index.Value)),
                Var(valueName, objectType, Copy(store.Value.Value)),
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
        }.Concat(loop.Body.OrEmpty().Select(value => (IBirStatement)((BirGraphValue)value).Rewrite(item => item))).ToArray();
        return new BirBlock { Body = new IBirStatement[] {
            Var(arrayName, arrayType, Copy(receiver)),
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
        var element = Type("System.Object");
        var source = new BirGraphValue[] {
            new BirArrayGet { Array = Local("array", array), Index = Int(2), Elem = element },
            new BirArraySet { Array = Local("receiverEffect", array), Index = Local("indexEffect", integer),
                Value = Local("valueEffect", element), Elem = element },
            new BirArrayLength { Array = Local("array", array) },
            new BirForArray { Array = Local("array", array), Var = "element", Elem = element,
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = Local("element", element) } } },
        };
        foreach (var original in source)
        {
            var old = original.ToDom();
            if (!GenericArrayStorageLowering.LowerOperation(old, refs,
                    new GenericArrayStorageLowering.FreshNames(old)))
                throw new InvalidOperationException("Array parity fixture did not exercise its existing owner");
            var lowered = LowerOperation(original, refs, new FreshNames(original));
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(old, lowered.ToDom()))
                throw new InvalidOperationException("Typed array storage changed operation " + original.GetType().Name);
        }
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
        Console.WriteLine("[typed array operations] self-test OK (exact operation parity, names, storage checks, ordered store values)");
    }
}
