using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SequenceForEachLowering
{
    internal static void Apply(BirFileDeclaration root) => Rewrite(root);

    static BirGraphValue Rewrite(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return node;
        // Allocate/rewrite the outer loop before walking its moved source/body,
        // exactly as the original in-place owner did.
        if (node is BirForEach { Src.HasValue: true } loop)
        {
            if (loop.ErasedEnumeration.OrDefault() || IsSequenceTyped((BirGraphValue)loop.Src.Value))
                node = RewriteLoop(loop);
            else loop.ErasedEnumeration = default;
        }
        node.VisitOwnChildSlots((_, role, slot) => {
            if (!NullableRepresentationViews.Frozen(role)) slot.Replace(Rewrite(slot.Value));
        });
        return node;
    }

    static BirBlock RewriteLoop(BirForEach loop)
    {
        var iterator = "$seqIt$" + System.Threading.Interlocked.Increment(ref _tmp);
        var enumerableType = new TypeNode.Fqn("System.Collections.IEnumerable");
        var iteratorType = new TypeNode.Fqn("System.Collections.IEnumerator");
        var element = loop.Elem;
        var source = loop.Src.Value;
        var body = loop.Body.OrEmpty();
        loop.Src = default;
        loop.Body = default;
        var iteratorVariable = new BirVariable { Name = iterator, Type = iteratorType,
            Init = OptionalField<IBirExpression>.Of(new BirNativeInstance {
                Type = enumerableType, Method = "GetEnumerator", ArgTypes = Array.Empty<TypeNode>(),
                Args = Array.Empty<IBirExpression>(), Ret = iteratorType,
                Recv = OptionalField<IBirExpression>.Of(new BirCast {
                    Type = enumerableType, E = OptionalField<IBirExpression>.Of(source),
                }),
            }) };
        var elementVariable = new BirVariable { Name = loop.Var, Type = element,
            Init = OptionalField<IBirExpression>.Of(new BirCast { Type = element,
                E = OptionalField<IBirExpression>.Of(new BirNativeInstance {
                    Type = iteratorType, Method = "get_Current", ArgTypes = Array.Empty<TypeNode>(),
                    Args = Array.Empty<IBirExpression>(), Ret = new TypeNode.Fqn("System.Object"),
                    Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = iterator }),
                }),
            }) };
        var repeat = new BirWhile { Label = loop.Label.HasValue ? loop.Label : default,
            Cond = OptionalField<IBirExpression>.Of(new BirNativeInstance {
                Type = iteratorType, Method = "MoveNext", ArgTypes = Array.Empty<TypeNode>(),
                Args = Array.Empty<IBirExpression>(), Ret = new TypeNode.Fqn("System.Boolean"),
                Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = iterator }),
            }), Body = new IBirStatement[] { elementVariable }.Concat(body).ToArray() };
        return new BirBlock { Body = new IBirStatement[] { iteratorVariable, repeat } };
    }

    static bool IsSequenceTyped(BirGraphValue source)
    {
        var sequence = false;
        source.VisitOwnTypeFields((field, type) => {
            if (field is "ret" or "dynRet" or "type"
                && Unwrap(type) is TypeNode.Fqn { Name: "kotlin.sequences.Sequence" }) sequence = true;
        });
        return sequence || source is BirCast { Type.HasValue: true } cast && IsErasedCast(cast.Type.Value);
    }

    static bool IsErasedCast(TypeNode type) =>
        type is TypeNode.Fqn { Args: null } named && NonGenericIfaces.Contains(named.Name)
        || FaithfulHints.IsStarProjectedColl(type);
}
