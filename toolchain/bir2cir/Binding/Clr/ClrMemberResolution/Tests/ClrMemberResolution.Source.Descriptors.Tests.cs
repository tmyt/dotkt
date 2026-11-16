using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void SourceDescriptorSelfTest()
    {
        PhysicalDescriptorForestSelfTest();
        static void Need(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Source call descriptors: " + reason); }
        var text = new TypeNode.Fqn("System.String"); var integer = new TypeNode.Fqn("System.Int32");
        var sourceArgs = new TypeNode[] { new TypeNode.Tv("method", 0) };
        var sourceShape = new TypeNode.Fqn("Box", sourceArgs);
        var existingSignature = new TypeNode[] { text };
        var existing = new BirCallStatic { Sig = existingSignature, ShapeTypes = new TypeNode[] { integer } };
        var shaped = new BirCallInstance { ShapeTypes = new TypeNode[] { sourceShape }, ArgTypes = new TypeNode[] { text } };
        var arguments = new TypeNode[] { integer };
        var fallback = new BirConstrainedCall { Sig = OptionalField<TypeNode[]>.Null, ShapeTypes = OptionalField<TypeNode[]>.Null,
            ArgTypes = arguments };
        var empty = new BirCallStatic { Args = Array.Empty<IBirExpression>() };
        var absent = new BirCallInstance(); var explicitNull = new BirCallStatic { Args = OptionalField<IBirExpression[]>.Null };
        var actualArgument = new BirLocal { Name = "value", Sty = integer };
        var nonempty = new BirCallStatic { Args = new IBirExpression[] { actualArgument } };
        var function = new TypeNode.Fn(false, text, new TypeNode[] { integer }, Recv: text);
        var direct = new BirNewDelegate { FuncType = function };
        var bound = new BirNewBoundDelegate { FuncType = function, Sig = OptionalField<TypeNode[]>.Null };
        var declaredDelegateSignature = new TypeNode[] { new TypeNode.Tv("method", 0) };
        var selected = new BirNewDelegate { FuncType = function, Sig = declaredDelegateSignature };
        BirExpressionStatement Evaluate(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        var saved = new byte[] { 3, 2, 1 };
        var body = new IBirStatement[] { Evaluate(existing), Evaluate(shaped), Evaluate(fallback), Evaluate(empty),
            Evaluate(absent), Evaluate(explicitNull), Evaluate(nonempty), Evaluate(direct), Evaluate(bound), Evaluate(selected) };
        var method = new BirFunctionDeclaration { Name = "caller", Params = Array.Empty<BirParameter>(),
            Ret = new TypeNode.Fqn("void"), Body = body, InlineBir = saved };
        var root = new BirFileDeclaration { FileClass = "Descriptors", Methods = new[] { method } };
        EnsurePlainCallDescriptors(root);
        Need(ReferenceEquals(existing.Sig.Value, existingSignature) && !existing.ShapeTypes.IsPresent,
            "an existing descriptor was replaced or left a second ShapeTypes dialect");
        Need(shaped.Sig.Value[0] == sourceShape && !shaped.ShapeTypes.IsPresent
            && !ReferenceEquals(((TypeNode.Fqn)shaped.Sig.Value[0]).Args, sourceArgs) && shaped.ArgTypes.Value[0] == text,
            "shape/argument priority or independent signature type ownership changed");
        sourceArgs[0] = text;
        Need(((TypeNode.Fqn)shaped.Sig.Value[0]).Args[0] == new TypeNode.Tv("method", 0)
            && fallback.Sig.Value.SequenceEqual(arguments) && ReferenceEquals(fallback.ArgTypes.Value, arguments)
            && !ReferenceEquals(fallback.Sig.Value, arguments) && fallback.ShapeTypes.IsNull,
            "argument fallback, null shape presence or copied type vectors changed");
        Need(empty.Sig.HasValue && empty.Sig.Value.Length == 0 && !absent.Sig.IsPresent && !explicitNull.Sig.IsPresent
            && !nonempty.Sig.IsPresent && ReferenceEquals(nonempty.Args.Value[0], actualArgument),
            "a missing/nonempty argument list was used to infer a signature");
        Need(direct.Sig.Value.SequenceEqual(new TypeNode[] { text, integer }) && bound.Sig.Value.SequenceEqual(direct.Sig.Value)
            && ReferenceEquals(selected.Sig.Value, declaredDelegateSignature) && !ReferenceEquals(direct.Sig.Value, function.Params),
            "delegate fallback lost its extension receiver or replaced an open declaration signature");
        Need(ReferenceEquals(root.Methods.Value[0], method)
            && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(method.InlineBir.Value, saved),
            "descriptor normalization changed continuing declarations, operands or opaque metadata");
        Console.WriteLine("[source call descriptors] exact priority, presence, open delegates and ownership: OK");
    }

    static void PhysicalDescriptorForestSelfTest()
    {
        static void Need(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Physical descriptor forest: " + reason); }
        var text = new TypeNode.Fqn("System.String"); var integer = new TypeNode.Fqn("System.Int32");
        var unit = new TypeNode.Fqn("void"); var ownerType = new TypeNode.Fqn("DescriptorOwner");
        var initializerReceiver = new BirThis();
        var initializer = new BirCallInstance { OwnerType = ownerType, Method = "read",
            Recv = OptionalField<IBirExpression>.Of(initializerReceiver), Args = Array.Empty<IBirExpression>(), Ret = text };
        var field = new BirFieldDeclaration { Name = "value", Type = text, Init = OptionalField<IBirExpression>.Of(initializer) };
        var preCall = new BirCallStatic { Owner = ownerType, Method = "before", Args = Array.Empty<IBirExpression>(), Ret = unit };
        var delegationArgument = new BirCallStatic { Owner = ownerType, Method = "argument",
            Args = Array.Empty<IBirExpression>(), Ret = integer };
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(),
            PreStmts = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(preCall) } },
            BaseArgs = new IBirExpression[] { delegationArgument }, DelegationSig = new TypeNode[] { integer },
            Body = Array.Empty<IBirStatement>() };
        var nativeArguments = new IBirExpression[] { new BirLocal { Name = "needle", Sty = text } };
        var nativeSignature = new TypeNode[] { text };
        var native = new BirNativeInstance { Type = text, Method = "Contains", Args = nativeArguments,
            Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = "text", Sty = text }),
            ArgTypes = nativeSignature, ResolvedMemberParams = nativeSignature, Ret = new TypeNode.Fqn("System.Boolean") };
        var closureType = new TypeNode.Fn(false, text, Array.Empty<TypeNode>());
        var closure = new BirNewClosure { ClosureType = new TypeNode.Fqn("Closure"), Method = "invoke",
            FuncType = closureType, Captures = Array.Empty<IBirExpression>() };
        var method = new BirFunctionDeclaration { Name = "physical", Ret = unit, Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(native) },
                new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(closure) } } };
        var owner = new BirClassDefinition { Kind = "class", Name = "DescriptorOwner", Fields = new[] { field },
            Ctors = new[] { constructor }, Methods = new[] { method } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
        var fields = owner.Fields.Value; var constructors = owner.Ctors.Value; var methods = owner.Methods.Value;
        var preStatements = constructor.PreStmts.Value; var baseArguments = constructor.BaseArgs.Value;
        var statements = method.Body.Value;
        EnsurePlainCallDescriptors(file);
        Need(initializer.Sig.HasValue && initializer.Sig.Value.Length == 0 && preCall.Sig.HasValue && preCall.Sig.Value.Length == 0
            && delegationArgument.Sig.HasValue && delegationArgument.Sig.Value.Length == 0
            && ReferenceEquals(initializer.Recv.Value, initializerReceiver) && ReferenceEquals(field.Init.Value, initializer),
            "final descriptor pass missed field/constructor forests or replaced an actual operand");
        Need(ReferenceEquals(native.ArgTypes.Value, nativeSignature) && ReferenceEquals(native.ResolvedMemberParams.Value, nativeSignature)
            && ReferenceEquals(native.Args.Value, nativeArguments) && ReferenceEquals(closure.FuncType.Value, closureType)
            && !closure.CalleeParams.IsPresent,
            "plain-call normalization crossed into native selection or physical closure invocation policy");
        Need(ReferenceEquals(owner.Fields.Value, fields) && ReferenceEquals(owner.Ctors.Value, constructors)
            && ReferenceEquals(owner.Methods.Value, methods) && ReferenceEquals(constructor.PreStmts.Value, preStatements)
            && ReferenceEquals(constructor.BaseArgs.Value, baseArguments) && ReferenceEquals(method.Body.Value, statements)
            && ReferenceEquals(baseArguments[0], delegationArgument), "descriptor pass changed owning declaration/body vectors");
        var existing = initializer.Sig.Value;
        EnsurePlainCallDescriptors(file);
        Need(ReferenceEquals(initializer.Sig.Value, existing), "an already explicit final signature was regenerated");
    }
}
