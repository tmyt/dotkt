using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class AliasConstructorDelegationExpansion
{
    static BirLocal Read(string name) => new() { Name = name };
    static BirConstant Constant(string value) => new() { Type = new TypeNode.Fqn("System.String"),
        Value = value is null ? new BirLiteral.NullLiteral() : new BirLiteral.TextLiteral(value) };
    static BirExpressionStatement ReadStatement(string name) => new() { Expr = Read(name) };
    static BirConstructorAdapterPayload Payload(string[] parameters, TypeNode[] signature, IBirStatement[] statements,
        IBirExpression[] arguments, TypeNode[] terminal) => new() {
            Parameters = parameters, Signature = signature, Statements = statements, Arguments = arguments, TerminalSignature = terminal,
        };

    internal static void SelfTest()
    {
        var statement = new BirBlock { Body = new IBirStatement[] {
            ReadStatement("argument"),
            new BirTry { Body = Array.Empty<IBirStatement>(), Catches = new[] {
                new BirCatch { Var = "argument", Body = new IBirStatement[] { ReadStatement("argument") } },
            } },
            new BirForRange { Var = "argument", Range = Read("argument"), Body = new IBirStatement[] { ReadStatement("argument") } },
            new BirExpressionStatement { Expr = new BirInlineLambda {
                Params = new[] { new BirLambdaParameter { Name = "argument" } },
                Body = new IBirStatement[] { ReadStatement("argument") }, Result = Read("argument"),
            } },
        } };
        var substituted = (BirBlock)ConstructorAdapterLocals.Substitute(statement,
            new Dictionary<string, IBirExpression> { ["argument"] = Read("consumer") });
        var body = substituted.Body.Value;
        string Name(IBirStatement value) => ((BirLocal)((BirExpressionStatement)value).Expr.Value).Name.Value;
        var catchClause = ((BirTry)body[1]).Catches.Value[0];
        var loop = (BirForRange)body[2];
        var lambda = (BirInlineLambda)((BirExpressionStatement)body[3]).Expr.Value;
        if (Name(body[0]) != "consumer" || Name(catchClause.Body.Value[0]) != "argument"
            || ((BirLocal)loop.Range.Value).Name.Value != "consumer" || Name(loop.Body.Value[0]) != "argument"
            || Name(lambda.Body.Value[0]) != "argument" || ((BirLocal)lambda.Result.Value).Name.Value != "argument")
            throw new InvalidOperationException("Alias adapter substitution captured a nested lexical binder");

        var freshening = Payload(Array.Empty<string>(), Array.Empty<TypeNode>(), new IBirStatement[] {
            new BirTry { Body = Array.Empty<IBirStatement>(), Catches = new[] {
                new BirCatch { Var = "caught", Body = new IBirStatement[] { ReadStatement("caught") } },
            } },
            new BirForRange { Var = "item", Range = Read("consumer"), Body = new IBirStatement[] { ReadStatement("item") } },
        }, Array.Empty<IBirExpression>(), Array.Empty<TypeNode>());
        FreshenLocals(freshening, Read("caught"), Read("item"));
        catchClause = ((BirTry)freshening.Statements.Value[0]).Catches.Value[0];
        loop = (BirForRange)freshening.Statements.Value[1];
        if (catchClause.Var.Value != "caught$1" || Name(catchClause.Body.Value[0]) != "caught$1"
            || loop.Var.Value != "item$1" || Name(loop.Body.Value[0]) != "item$1"
            || ((BirLocal)loop.Range.Value).Name.Value != "consumer")
            throw new InvalidOperationException("Alias adapter alpha-conversion changed its lexical ownership");

        var exactSlot = new TypeNode.Fqn("System.Int32");
        var typedRead = Payload(Array.Empty<string>(), Array.Empty<TypeNode>(), new IBirStatement[] {
            new BirVariable { Name = "typed", Type = exactSlot,
                Init = new BirConstant { Type = exactSlot, Value = new BirLiteral.IntegerLiteral(7) } },
        }, new IBirExpression[] { new BirLocal { Name = "typed", Sty = exactSlot } }, new TypeNode[] { exactSlot });
        FreshenLocals(typedRead, Read("typed"));
        if (typedRead.Arguments.Value[0] is not BirLocal { Name.Value: "typed$1", Sty.Value: TypeNode.Fqn { Name: "System.Int32" } }
            || ((BirVariable)typedRead.Statements.Value[0]).Type.Value != exactSlot)
            throw new InvalidOperationException("Alias alpha-conversion discarded an exact known local slot type");

        var nestedName = $"cir$alias${localCounter + 1}";
        var argument = new BirValueBlock { Stmts = new IBirStatement[] {
            new BirVariable { Name = nestedName, Type = new TypeNode.Fqn("System.Object"), Init = Constant(null) },
        }, Result = Constant("argument") };
        var host = new BirConstructor();
        var template = Payload(new[] { "argument" }, new TypeNode[] { new TypeNode.Fqn("System.String") },
            Array.Empty<IBirStatement>(), Array.Empty<IBirExpression>(), Array.Empty<TypeNode>());
        var reads = MaterializeArguments(host, new IBirExpression[] { argument }, template);
        var temporary = (BirVariable)host.PreStmts.Value.Single();
        if (temporary.Name.Value == nestedName || ((BirLocal)reads["argument"]).Name.Value != temporary.Name.Value
            || ((BirVariable)((BirValueBlock)temporary.Init.Value).Stmts.Value[0]).Name.Value != nestedName)
            throw new InvalidOperationException("Alias argument temporary captured an incoming argument's local");
        SelfTestNestedConstructions();
        SelfTestCarrier();
        TypedCallEvalLowering.SelfTest();
        Console.WriteLine("[alias constructor expansion] self-test OK (owning composition, lexical scopes, nested identity, executable codecs)");
    }

    static void SelfTestNestedConstructions()
    {
        var text = new TypeNode.Fqn("System.String");
        var value = new TypeNode.Fqn("System.Object");
        var inner = new TypeNode.Fqn("Probe.InnerAlias");
        var outerSignature = new TypeNode[] { text, inner };
        var terminal = new TypeNode[] { text, value };
        BirConstructor Constructor(TypeNode[] signature) => new() { Params = signature.Select((slot, index) =>
            new BirParameter { Name = "p" + index, Type = slot }).ToArray() };
        BirNew Inner() => new() { Type = inner, ArgTypes = new TypeNode[] { text }, Args = new IBirExpression[] { Constant("inner") } };
        foreach (var baseDelegation in new[] { false, true })
        {
            var adapterLocal = $"cir$alias${localCounter + (baseDelegation ? 1 : 2)}";
            var expansion = new AliasConstructorDelegationExpansion(new Dictionary<string, BirClassDefinition> {
                [inner.Name] = new() { Name = inner.Name, Kind = "class", Ctors = new[] { Constructor(new[] { text }), Constructor(terminal) } },
                ["Probe.OuterAlias"] = new() { Name = "Probe.OuterAlias", Kind = "class", Ctors = new[] { Constructor(outerSignature) } },
            }, null);
            IBirStatement[] Prefix() => new IBirStatement[] { new BirVariable { Name = adapterLocal, Type = text, Init = Read("p0") } };
            expansion.memo[(inner.Name, 0)] = Payload(new[] { "p0" }, new[] { text }, baseDelegation ? Prefix() : Array.Empty<IBirStatement>(),
                new IBirExpression[] { Read(baseDelegation ? adapterLocal : "p0"), Constant(null) }, terminal);
            expansion.memo[(inner.Name, 1)] = Payload(new[] { "p0", "p1" }, terminal, baseDelegation ? Array.Empty<IBirStatement>() : Prefix(),
                new IBirExpression[] { Read(baseDelegation ? "p0" : adapterLocal), Read("p1") }, terminal);
            expansion.memo[("Probe.OuterAlias", 0)] = Payload(new[] { "p0", "p1" }, outerSignature, Array.Empty<IBirStatement>(),
                new IBirExpression[] { Read("p0"), Read("p1") }, terminal);
            var arguments = new IBirExpression[] { Constant("outer"), Inner() };
            var root = new BirFileDeclaration {
                Types = baseDelegation ? new IBirTypeDeclaration[] { new BirClassDefinition {
                    Name = "Probe.Consumer", Kind = "class", Base = new TypeNode.Fqn("Probe.OuterAlias"),
                    Ctors = new[] { new BirConstructor { Params = Array.Empty<BirParameter>(), BaseArgs = arguments, DelegationSig = outerSignature } },
                } } : Array.Empty<IBirTypeDeclaration>(),
                Methods = baseDelegation ? Array.Empty<BirFunctionDeclaration>() : new[] { new BirFunctionDeclaration {
                    Name = "test", Static = true, Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"),
                    Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirNew {
                        Type = new TypeNode.Fqn("Probe.OuterAlias"), Args = arguments, ArgTypes = outerSignature,
                    } } },
                } },
            };
            expansion.Apply(root);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var completed = 0;
            root.Visit(node => {
                if (node is BirVariable variable && !names.Add(variable.Name.Value))
                    throw new InvalidOperationException("Nested alias construction shadows an argument");
                if (node is BirNew { Type.Value: TypeNode.Fqn { Name: "Probe.InnerAlias" } }) completed++;
            });
            if (completed != 1 || names.Count != (baseDelegation ? 4 : 3))
                throw new InvalidOperationException("A completed nested alias argument was expanded again");
        }
    }

    static void SelfTestCarrier()
    {
        var frame = new TypeNode.Fqn("Probe.Box", new TypeNode[] { new TypeNode.Tv("type", 1) });
        var source = Payload(new[] { "argument" }, new TypeNode[] { frame }, new IBirStatement[] {
            new BirSourcePoint { Span = new BirSourceSpan { F = "producer.kt", L = 3 } },
        }, new IBirExpression[] { new BirConstant {
            Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral("a\ud800b\udfff"),
        } }, new TypeNode[] { new TypeNode.Fqn("System.String") });
        foreach (var codec in new[] { BirCarrier.JsonV1, BirCarrier.MessagePackV1 })
        {
            var restored = ConstructorAdapterCarrier.Decode(codec, ConstructorAdapterCarrier.Encode(codec, source));
            if (!ConstructorAdapterCarrier.Same(source, restored) || restored.Signature.Value[0] != frame)
                throw new InvalidOperationException("Constructor adapter codec changed frames or scalar values");
            var first = (BirConstructorAdapterPayload)restored.DeepClone();
            var second = (BirConstructorAdapterPayload)restored.DeepClone();
            ((TypeNode.Fqn)first.Signature.Value[0]).Args[0] = new TypeNode.Tv("type", 0);
            if (second.Signature.Value[0] != frame || source.Signature.Value[0] != frame)
                throw new InvalidOperationException("Constructor adapter snapshots share mutable type-argument vectors");
        }
        var attribute = ConstructorAdapterCarrier.Create(source);
        var hidden = ConstructorAdapterCarrier.Decode(ConstructorAdapterCarrier.Codec, ((BirOpaqueBytes)attribute.Args.Value[1]).Bytes.Value);
        if (((BirSourcePoint)hidden.Statements.Value[0]).Span.IsPresent || !((BirSourcePoint)source.Statements.Value[0]).Span.HasValue)
            throw new InvalidOperationException("Outgoing constructor metadata leaked or rewrote producer source positions");
    }
}
