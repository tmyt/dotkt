#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using DotKt.Bir;

namespace DotKt.Cir;

public static class ExecutableSelfTest
{
    public static void Run()
    {
        var integer = new TypeNode.Fqn("System.Int32");
        var root = new CirBlock { Body = [
            new CirSourcePoint(),
            new CirVariable { Name = "n", Type = integer },
            new CirVariable { Name = "maybe", Type = integer, Init = OptionalField<ICirExpression>.Null },
            new CirFor { Var = "i", From = new CirLocal { Name = "first" },
                To = new CirLocal { Name = "last" }, Cmp = "<=", Step = 1, LoopId = 7,
                Body = [new CirExpressionStatement { Expr = new CirValueBlock {
                    Stmts = [new CirSetLocal { Name = "n", Value = new CirLocal { Name = "i" } }],
                    Result = new CirLocal { Name = "n" }, Type = integer } }] },
            new CirTry { Body = [new CirReturn { Value = new CirLocal { Name = "n" } }],
                Catches = [new CirCatchClause { ExcType = new TypeNode.Fqn("System.Exception"),
                    Var = "failure", Body = [new CirThrow { Value = new CirLocal { Name = "failure" } }] }],
                Finally = new ICirStatement[] { new CirSourcePoint { Span = new CirSourceSpan {
                    F = "source.kt", L = 5, C = 3, El = 5, Ec = 9 } } } }
        ] };
        var tags = new[] { "block", "sourcePoint", "var", "var", "for", "local", "local",
            "exprStmt", "valueBlock", "setLocal", "local", "local", "try", "return", "local",
            "throw", "local", "sourcePoint" };
        CheckBinary(root);
        var encoded = JsonSerializer.Serialize<ICirStatement>(root, AttributeJson.Options);
        var copied = (CirBlock)root.Rewrite(node => node);
        if (JsonSerializer.Serialize<ICirStatement>(copied, AttributeJson.Options) != encoded
            || !CirExecutableWalk.DescendantsAndSelf(copied).Select(node => node.Tag).SequenceEqual(tags))
            throw new InvalidOperationException("Typed executable rewrite changed presence, ordered operands or source positions");
        ((CirFor)copied.Body[3]).Body[0] = new CirReturn();
        if (((CirFor)root.Body[3]).Body[0] is not CirExpressionStatement)
            throw new InvalidOperationException("Executable rewriting shared a mutable child array");
        var replaced = (CirBlock)root.Rewrite(node => node is CirLocal { Name: "n" or "failure" } local
            ? local with { Name = "replacement" } : node);
        if (CirExecutableWalk.DescendantsAndSelf(replaced).OfType<CirLocal>().Any(local => local.Name is "n" or "failure")
            || ((CirTry)replaced.Body[4]).Catches[0].Body[0] is not CirThrow { Value: CirLocal { Name: "replacement" } }
            || !CirExecutableWalk.DescendantsAndSelf(root).OfType<CirLocal>().Any(local => local.Name == "n"))
            throw new InvalidOperationException("Typed rewriting missed a catch/value-block child or changed the original");
        var branch = new CirIf { Branches = [new CirConditionalBranch {
            Cond = new CirLocal { Name = "condition" },
            Body = [new CirReturn { Value = new CirLocal { Name = "branchValue" } }] }] };
        ICirNode Rename(ICirNode node) => node is CirLocal local ? local with { Name = "rewritten" } : node;
        var changedBranch = (CirIf)branch.Rewrite(Rename);
        var spread = new CirSpreadPart { Spread = true, E = new CirLocal { Name = "spreadValue" } };
        var changedSpread = spread.RewriteChildren(node => ((CirNode)node).Rewrite(Rename));
        if (changedBranch.Branches[0].Cond.Value is not CirLocal { Name: "rewritten" }
            || changedBranch.Branches[0].Body[0] is not CirReturn { Value.Value: CirLocal { Name: "rewritten" } }
            || branch.Branches[0].Cond.Value is not CirLocal { Name: "condition" }
            || changedSpread.E is not CirLocal { Name: "rewritten" } || !changedSpread.Spread
            || spread.E is not CirLocal { Name: "spreadValue" })
            throw new InvalidOperationException("Typed rewriting missed an auxiliary branch/spread edge or changed its input");
        var decoded = JsonSerializer.Deserialize<ICirStatement>(encoded, AttributeJson.Options) as CirBlock
            ?? throw new InvalidOperationException("Executable role codec lost the concrete root");
        if (JsonSerializer.Serialize<ICirStatement>(decoded, AttributeJson.Options) != encoded
            || !CirExecutableWalk.DescendantsAndSelf(root).Select(node => node.Tag).SequenceEqual(tags)
            || !CirExecutableWalk.DescendantsAndSelf(decoded).Select(node => node.Tag).SequenceEqual(tags))
            throw new InvalidOperationException("Executable child roles or ordered nested records changed");
        if (decoded.Body[1] is not CirVariable { Init.IsPresent: false }
            || decoded.Body[2] is not CirVariable { Init.IsNull: true }
            || decoded.Body[0] is not CirSourcePoint { Span.IsPresent: false }
            || decoded.Body[4] is not CirTry { Finally.HasValue: true } attempt
            || attempt.Finally.Value[0] is not CirSourcePoint { Span.HasValue: true } point
            || point.Span.Value.F != "source.kt" || point.Span.Value.Ec != 9)
            throw new InvalidOperationException("Executable presence, hidden/source points or catch/finally data changed");
        if (ReferenceEquals(root.Body, decoded.Body) || ReferenceEquals(root.Body[3], decoded.Body[3]))
            throw new InvalidOperationException("A decoded executable graph reused mutable input storage");
        foreach (var constant in new[] {
            new CirConstant { Type = new TypeNode.Fqn("System.Single"),
                Value = new AttributeArgument.Float32Value(unchecked((int)0x7fa12345)) },
            new CirConstant { Type = new TypeNode.Fqn("System.UInt64"),
                Value = new AttributeArgument.UInt64Value(ulong.MaxValue) },
            new CirConstant { Type = new TypeNode.Fqn("System.String"),
                Value = new AttributeArgument.Utf16Value([0xd800, 0x0041, 0xdc00]) }
        })
        {
            CheckBinary(new CirExpressionStatement { Expr = constant });
            var text = JsonSerializer.Serialize<ICirExpression>(constant, AttributeJson.Options);
            var copy = JsonSerializer.Deserialize<ICirExpression>(text, AttributeJson.Options) as CirConstant
                ?? throw new InvalidOperationException("Executable scalar lost its node role");
            if (JsonSerializer.Serialize<ICirExpression>(copy, AttributeJson.Options) != text
                || CirExecutableWalk.DescendantsAndSelf(copy).Count() != 1)
                throw new InvalidOperationException("Executable scalar lost bits/code units or became a child node");
        }
        foreach (var malformed in new[] {
            """{"k":"const","type":{"t":"fqn","name":"System.Single"},"value":{"a":"f32"}}""",
            """{"k":"const","type":{"t":"fqn","name":"System.Single"},"value":{"a":"f32","bits":2147483648}}""",
            """{"k":"var","name":"n","type":{"t":"fqn","name":"System.Int32"}}""",
            """{"k":"local","name":"n","extra":1}""",
            """{"k":"callInstance","method":"Read","ownerType":{"t":"fqn","name":"Probe"},"recv":{"k":"this"},"args":[],"virtual":null}""",
            """{"k":"callStatic","method":"Read","args":[null]}""",
            """{"k":"valueBlock","stmts":[{"k":"try","body":[],"catches":[null]}],"result":{"k":"this"}}"""
        })
        {
            try { JsonSerializer.Deserialize<ICirExpression>(malformed, AttributeJson.Options); }
            catch (JsonException) { continue; }
            throw new InvalidOperationException("Malformed current executable scalar/role/field was accepted");
        }
        Console.WriteLine("[typed CIR executables] self-test OK (closed roles, child order, presence, source spans, current malformed shapes)");
    }

    private static void CheckBinary(ICirStatement body)
    {
        var file = new CirFileDeclaration { FileClass = "CodecProbe", HasMain = false, Fields = [], Types = [],
            Methods = [new CirMethod { Name = "Probe", Static = true, Override = false, Virtual = false,
                Params = [], Ret = new TypeNode.Fqn("System.Void"),
                Body = new ICirStatement[] { body } }] };
        if (DeclarationJson.Write(CirFileCodec.Decode(CirFileCodec.Encode(file))) != DeclarationJson.Write(file))
            throw new InvalidOperationException("CIR MessagePack changed executable roles, evaluation order, presence or scalar bits");
    }
}
