using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ForInLowering
{
    internal static void SelfTestTypedRangePrefix()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var locals = new HashSet<string>(StringComparer.Ordinal);
        var supers = new Dictionary<string, List<string>>(StringComparer.Ordinal) {
            ["ConcreteSequence"] = new() { "IntermediateSequence" },
            ["IntermediateSequence"] = new() { "kotlin.sequences.Sequence" },
        };
        BirForIn Loop(string type) => new() {
            SrcType = new TypeNode.Fqn(type), Src = Read("source"), Var = "item",
            Elem = new TypeNode.Fqn("kotlin.Int"), Body = Array.Empty<IBirStatement>(),
            Fallback = new BirBlock { Body = new IBirStatement[] { new BirVariable {
                Name = "fallbackMarker", Type = new TypeNode.Fqn("kotlin.Int"), Init = Integer(0),
            } } },
        };
        var range = Loop("kotlin.ranges.IntRange");
        var progression = Loop("kotlin.ranges.IntProgression");
        var sequence = Loop("kotlin.sequences.Sequence");
        var concrete = Loop("ConcreteSequence");
        var user = Loop("UserIterable");
        var file = File(range, progression, sequence, concrete, user);
        var method = file.Methods.Value[0];
        var originalBody = method.Body.Value;
        var fallback = user.Fallback.Value;
        if (!ReferenceEquals(Apply(file, true, supers, locals, refs), file)
            || !ReferenceEquals(file.Methods.Value[0], method) || !ReferenceEquals(method.Body.Value, originalBody)
            || method.Body.Value[0] is not BirForRange { RangeType.Value: TypeNode.Fqn { Name: "kotlin.ranges.IntRange" } }
            || method.Body.Value[1] is not BirForRange
            || method.Body.Value[2] is not BirForEach { ErasedEnumeration.Value: false }
            || method.Body.Value[3] is not BirForEach || !ReferenceEquals(method.Body.Value[4], fallback))
            throw new InvalidOperationException("forIn source classification changed declaration ownership or hierarchy selection");
        var appConcrete = Loop("ConcreteSequence");
        var appFile = File(appConcrete);
        Apply(appFile, false, supers, locals, refs);
        if (!ReferenceEquals(appFile.Methods.Value[0].Body.Value[0], appConcrete.Fallback.Value))
            throw new InvalidOperationException("App forIn unexpectedly used the stdlib-only subtype fast path");

        // The retained stdlib form states its exact physical accessor contract.
        RangeForLowering.Apply(file, true);
        var stdlibRange = (BirForRange)method.Body.Value[0];
        if (stdlibRange.RangeType.IsPresent || stdlibRange.AccessOwner.Value is not TypeNode.Fqn { Name: "kotlin.ranges.IntProgression" }
            || stdlibRange.FirstM.Value != KotlinPropertyAccessors.PhysicalName("first", "get")
            || stdlibRange.LastM.Value != KotlinPropertyAccessors.PhysicalName("last", "get")
            || stdlibRange.StepM.Value != KotlinPropertyAccessors.PhysicalName("step", "get") || !stdlibRange.Label.IsNull)
            throw new InvalidOperationException("Stdlib range realization lost accessor roles or field presence");
        var source = new BirCallStatic { Owner = OptionalField<TypeNode>.Null, Method = "makeRange", Args = Array.Empty<IBirExpression>() };
        var appRange = Loop("kotlin.ranges.IntRange");
        appRange.Src = source;
        appRange.Label = "loop";
        var appRangeFile = File(appRange);
        Apply(appRangeFile, false, supers, locals, refs);
        RangeForLowering.Apply(appRangeFile, false);
        var block = (BirBlock)appRangeFile.Methods.Value[0].Body.Value[0];
        var captured = (BirVariable)block.Body.Value[0];
        var counted = (BirFor)block.Body.Value[1];
        if (!ReferenceEquals(captured.Init.Value, source) || captured.Type.Value is not TypeNode.Fqn { Name: "kotlin.ranges.IntRange" }
            || counted.Label.Value != "loop" || counted.Cmp.Value != "<=" || counted.Step.Value != 1
            || counted.From.Value is not BirCallInstance { Prop.Value: "get", Method.Value: "first", Recv.Value: BirLocal from }
            || counted.To.Value is not BirCallInstance { Prop.Value: "get", Method.Value: "last", Recv.Value: BirLocal to }
            || from.Name.Value != captured.Name.Value || to.Name.Value != captured.Name.Value)
            throw new InvalidOperationException("App range did not capture its source exactly once for the two accessors");

        // Classification is preorder, including the shared deterministic allocator.
        BirForIn DownTo(string variable) => new() {
            SrcType = new TypeNode.Fqn("kotlin.ranges.IntProgression"), Var = variable,
            Src = new BirCallStatic { Owner = OptionalField<TypeNode>.Null, Method = "downTo",
                Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") },
                Args = new IBirExpression[] { Read("from"), Read("to") } },
            Body = Array.Empty<IBirStatement>(), Fallback = new BirBlock { Body = Array.Empty<IBirStatement>() },
        };
        var outer = DownTo("outer");
        outer.Body = new IBirStatement[] { DownTo("inner") };
        var before = _tmp;
        var descendingFile = File(outer);
        Apply(descendingFile, false, supers, locals, refs);
        var outerBlock = (BirBlock)descendingFile.Methods.Value[0].Body.Value[0];
        var outerFor = (BirFor)outerBlock.Body.Value[2];
        var innerBlock = (BirBlock)outerFor.Body.Value[0];
        if (((BirVariable)outerBlock.Body.Value[0]).Name.Value != "$downTo$" + (before + 1) + "$from"
            || ((BirVariable)innerBlock.Body.Value[0]).Name.Value != "$downTo$" + (before + 2) + "$from"
            || outerFor.Cmp.Value != ">=" || outerFor.Step.Value != -1)
            throw new InvalidOperationException("Descending loop order or deterministic temporary allocation changed");
        var shadowed = DownTo("shadowed");
        var shadowedFile = File(shadowed);
        Apply(shadowedFile, false, supers, new HashSet<string> { "downTo" }, refs);
        if (!ReferenceEquals(shadowedFile.Methods.Value[0].Body.Value[0], shadowed.Fallback.Value))
            throw new InvalidOperationException("A local downTo shadow lost its authored iterator path");

        MembershipAndConstruction();
        Console.WriteLine("[typed range prefix] self-test OK (source classification, ordering, slots, width, and ownership)");
    }

    static void MembershipAndConstruction()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var longType = new TypeNode.Fqn("kotlin.Long");
        var locals = new HashSet<string>(StringComparer.Ordinal);
        BirCallInstance Bounds(string method, TypeNode primitive, IBirExpression lo, IBirExpression hi) => new() {
            OwnerType = primitive, Method = method, Sig = new[] { primitive },
            Recv = OptionalField<IBirExpression>.Of(lo), Args = new[] { hi },
        };
        BirCallInstance Membership(IBirExpression range, IBirExpression subject, TypeNode primitive, string rangeName) => new() {
            OwnerType = new TypeNode.Fqn(rangeName), Method = "contains", Sig = new[] { primitive },
            Recv = OptionalField<IBirExpression>.Of(range), Args = new[] { subject },
        };
        var lo = new BirCallStatic { Method = "lo", Owner = OptionalField<TypeNode>.Null, Args = Array.Empty<IBirExpression>() };
        var hi = new BirCallStatic { Method = "hi", Owner = OptionalField<TypeNode>.Null, Args = Array.Empty<IBirExpression>() };
        var subject = new BirCallStatic { Method = "subject", Owner = OptionalField<TypeNode>.Null, Args = Array.Empty<IBirExpression>() };
        var contains = Membership(Bounds("rangeUntil", longType, lo, hi), subject, longType, "kotlin.ranges.LongRange");
        var statement = new BirExpressionStatement { Expr = contains };
        var file = File(statement);
        RangeMembershipLowering.Apply(file, locals, true);
        var block = (BirValueBlock)statement.Expr.Value;
        var variables = block.Stmts.Value.Cast<BirVariable>().ToArray();
        if (variables.Length != 3 || !ReferenceEquals(variables[0].Init.Value, lo)
            || !ReferenceEquals(variables[1].Init.Value, hi) || !ReferenceEquals(variables[2].Init.Value, subject)
            || variables.Any(variable => variable.Type.Value is not TypeNode.Fqn { Name: "kotlin.Long" })
            || block.Result.Value is not BirConditional { Then.Value: BirBinaryOperation { Op.Value: "<", Lhs.Value: BirLocal thenSubject } }
            || thenSubject.Name.Value != variables[2].Name.Value)
            throw new InvalidOperationException("Membership failed lo/hi/subject evaluation order or declared Long slot widths");
        var literal = new BirExpressionStatement { Expr = Membership(Bounds("rangeTo", integer, Integer(1), Integer(3)),
            Integer(2), integer, "kotlin.ranges.IntRange") };
        RangeMembershipLowering.Apply(File(literal), locals, true);
        if (literal.Expr.Value is not BirConditional { Then.Value: BirBinaryOperation { Op.Value: "<=" } })
            throw new InvalidOperationException("Stable range operands unnecessarily changed their fast-path form");
        var until = new BirCallStatic { Owner = OptionalField<TypeNode>.Null, Method = "until",
            Sig = new TypeNode[] { integer, integer }, Args = new IBirExpression[] { Integer(1), Integer(3) } };
        var shadowedContains = Membership(until, Integer(2), integer, "kotlin.ranges.IntRange");
        var shadowedStatement = new BirExpressionStatement { Expr = shadowedContains };
        RangeMembershipLowering.Apply(File(shadowedStatement), new HashSet<string> { "until" }, true);
        if (!ReferenceEquals(shadowedStatement.Expr.Value, shadowedContains))
            throw new InvalidOperationException("Local until shadow was treated as a range intrinsic");

        var construction = new BirExpressionStatement { Expr = Bounds("rangeUntil", longType, lo, hi) };
        var user = Bounds("rangeTo", new TypeNode.Fqn("UserRange"), Integer(0), Integer(1));
        var userStatement = new BirExpressionStatement { Expr = user };
        RangeConstructionLowering.Apply(File(construction, userStatement));
        if (construction.Expr.Value is not BirNew { Type.Value: TypeNode.Fqn { Name: "kotlin.ranges.LongRange" },
                Args.Value: [var start, BirBinaryOperation { Op.Value: "-", Lhs.Value: var end,
                    Rhs.Value: BirConstant { Type.Value: TypeNode.Fqn { Name: "kotlin.Long" }, Value.Value: BirLiteral.IntegerLiteral { Value: 1 } } }] } created
            || !ReferenceEquals(start, lo) || !ReferenceEquals(end, hi)
            || created.ArgTypes.Value.Any(type => type is not TypeNode.Fqn { Name: "kotlin.Long" })
            || !ReferenceEquals(userStatement.Expr.Value, user))
            throw new InvalidOperationException("Range construction lost endpoint evaluation, width or user-operator identity");
    }

    static BirFileDeclaration File(params IBirStatement[] statements) => new() {
        FileClass = "RangePrefixProbe", Methods = new[] { new BirFunctionDeclaration {
            Name = "probe", Static = true, DeclarationId = "range-prefix-probe",
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"), Body = statements,
        } },
    };
    static BirLocal Read(string name) => new() { Name = name };
    static BirConstant Integer(long value) => new() {
        Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(value),
    };
}
