using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SourceGraphRewrite
{
    internal static void SelfTest()
    {
        TypeNode text = new TypeNode.Fqn("kotlin.String"), integer = new TypeNode.Fqn("kotlin.Int"),
            wide = new TypeNode.Fqn("kotlin.Long"), narrow = new TypeNode.Fqn("kotlin.Short");
        var expected = new Dictionary<BirLocal, TypeNode>(ReferenceEqualityComparer.Instance);
        BirLocal Read(TypeNode type)
        {
            var read = new BirLocal { Name = "x" };
            expected.Add(read, type);
            return read;
        }
        BirExpressionStatement Use(TypeNode type) => new() { Expr = Read(type) };
        BirVariable Declare(TypeNode type, TypeNode initializer) => new() {
            Name = "x", Type = type, Init = Read(initializer),
        };
        var absentStatements = new BirValueBlock { Result = Read(integer) };
        var nullStatements = new BirValueBlock {
            Stmts = OptionalField<IBirStatement[]>.Null, Result = Read(integer),
        };
        var absentBody = new BirInlineLambda {
            Params = new[] { new BirLambdaParameter { Name = "x", Type = text } }, Result = Read(text),
        };
        var nullBody = new BirInlineLambda {
            Params = new[] { new BirLambdaParameter { Name = "x", Type = text } },
            Body = OptionalField<IBirStatement[]>.Null, Result = Read(text),
        };
        var method = new BirFunctionDeclaration {
            Name = "sourceScopes", Ret = new TypeNode.Fqn("kotlin.Unit"),
            Params = new[] {
                new BirParameter { Name = "x", Type = text },
                new BirParameter { Name = "y", Type = text,
                    DefaultBir = new BirDefaultBody { Index = 1, Body = Read(text) } },
            },
            Body = new IBirStatement[] {
                Use(text), Declare(integer, text), Use(integer),
                new BirTry {
                    Body = new IBirStatement[] { Declare(wide, integer), Use(wide) },
                    Catches = new[] { new BirCatch {
                        Var = "failure", ExcType = new TypeNode.Fqn("kotlin.Throwable"),
                        Body = new IBirStatement[] { Use(integer), Declare(narrow, integer), Use(narrow) },
                    } },
                    Finally = new IBirStatement[] { Use(integer) },
                },
                new BirIf { Branches = new[] {
                    new BirBranch { Cond = new BirConstant { Type = new TypeNode.Fqn("kotlin.Boolean"),
                        Value = new BirLiteral.BooleanLiteral(true) },
                        Body = new IBirStatement[] { Declare(wide, integer), Use(wide) } },
                    new BirBranch { Else = true, Body = new IBirStatement[] { Use(integer) } },
                } },
                new BirExpressionStatement { Expr = new BirValueBlock {
                    Stmts = new IBirStatement[] { Declare(wide, integer) }, Result = Read(wide),
                } },
                Use(integer),
                new BirExpressionStatement { Expr = new BirInlineLambda {
                    Params = new[] { new BirLambdaParameter { Name = "x", Type = text } },
                    Captures = Array.Empty<BirInlineCapture>(),
                    Body = new IBirStatement[] { Declare(wide, text) }, Result = Read(wide),
                } },
                Use(integer),
                new BirExpressionStatement { Expr = absentStatements },
                new BirExpressionStatement { Expr = nullStatements },
                new BirExpressionStatement { Expr = absentBody },
                new BirExpressionStatement { Expr = nullBody },
                Use(integer),
            },
        };
        var observed = new List<(IReadOnlyDictionary<string, TypeNode> Scope, TypeNode Type)>();
        var visited = new HashSet<BirLocal>(ReferenceEqualityComparer.Instance);
        var checkedReads = 0;
        Rewrite(method, (node, scope) => {
            if (node is BirLocal read)
            {
                if (!visited.Add(read) || !expected.TryGetValue(read, out var type))
                    throw new InvalidOperationException("Source lexical rewrite visited a result more than once");
                if (BirGraphStaticType.Surface((IBirExpression)read, scope) != type)
                    throw new InvalidOperationException("Source lexical rewrite leaked or prematurely declared a local");
                observed.Add((scope, type));
                checkedReads++;
                return read with { Sty = type };
            }
            if (node is BirExpressionStatement { Expr.HasValue: true } statement
                && statement.Expr.Value is BirLocal { Sty.HasValue: false })
                throw new InvalidOperationException("Source lexical rewriting did not replace children bottom-up");
            return node;
        });
        if (checkedReads != expected.Count || observed.Any(item => item.Scope["x"] != item.Type))
            throw new InvalidOperationException("Source lexical callbacks did not retain immutable scope snapshots");
        if (absentStatements.Stmts.IsPresent || !nullStatements.Stmts.IsNull
            || absentBody.Body.IsPresent || !nullBody.Body.IsNull)
            throw new InvalidOperationException("Source result regions changed absent/null statement-field presence");

        var payload = new BirInlinePayload {
            Params = new[] { new BirParameter { Name = "x", Type = text } },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirLocal { Name = "x" } } },
        };
        var entered = false;
        if (!ReferenceEquals(Rewrite(payload, (node, _) => { entered = true; return node; }), payload) || entered)
            throw new InvalidOperationException("Source rewrite entered a saved executable payload");

        var locals = new Dictionary<string, TypeNode> { ["x"] = new TypeNode.Nullable(integer) };
        var local = new BirLocal { Name = "x" };
        var unwrapped = new BirNullableValue { E = local, Elem = integer };
        var boxed = new BirCast { Type = new TypeNode.Fqn("kotlin.Any"), E = unwrapped };
        if (BirGraphStaticType.Value(boxed, locals) != integer
            || BirGraphStaticType.Value(new BirNullableValue { E = local }, locals) != locals["x"]
            || BirGraphStaticType.Value(new BirLocal { Name = "x", Sty = text }, locals) != text
            || !ReferenceEquals(FaithfulHints.StripAnyCast(boxed), unwrapped))
            throw new InvalidOperationException("Source value recognition changed cast, nullable element, or stamp precedence");
        foreach (var name in new[] { "System.Object", "object" })
            if (!ReferenceEquals(FaithfulHints.StripAnyCast(new BirCast {
                Type = new TypeNode.Fqn(name), E = local,
            }), local))
                throw new InvalidOperationException("Any-cast recognition lost a physical Object spelling");
        var concreteCast = new BirCast { Type = text, E = local };
        var nestedBox = new BirCast { Type = new TypeNode.Fqn("kotlin.Any"), E = boxed };
        var block = new BirValueBlock {
            Stmts = new IBirStatement[] { new BirVariable { Name = "x", Type = wide } }, Result = local,
        };
        if (!ReferenceEquals(FaithfulHints.StripAnyCast(concreteCast), concreteCast)
            || !ReferenceEquals(FaithfulHints.StripAnyCast(nestedBox), boxed)
            || BirGraphStaticType.Value(concreteCast, locals) != locals["x"]
            || BirGraphStaticType.Value(block, locals) != wide
            || BirGraphStaticType.Value(block with { Type = text }, locals) != text)
            throw new InvalidOperationException("Cast stripping changed single-layer Any or underlying-value policy");
        Console.WriteLine("[source lexical rewrite] self-test OK (parameters/defaults, preceding locals, independent regions, opaque payloads, value recognition)");
    }
}
