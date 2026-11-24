using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed class NumericTextOperands(SourceHelperBinder helpers)
{
    const string Owner = "DotKt.Runtime.CompilerServices.NumberRuntimeKt", Method = "invariantTextOperand";
    string identity;

    internal IBirExpression Select(IBirExpression value, IReadOnlyDictionary<string, TypeNode> locals)
    {
        var type = BirGraphStaticType.Surface(value, locals);
        if ((type is TypeNode.Nullable n ? n.Of : type) is TypeNode.Fqn {
            Name: "kotlin.String" or "System.String" or "kotlin.Boolean" or "System.Boolean" or "kotlin.Char" or "System.Char" })
            return value;
        var slot = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var signature = new TypeNode[] { slot };
        identity ??= helpers.Select(Owner, Method, 0, signature).Identity;
        return new BirCallStatic { Owner = new TypeNode.Fqn(Owner), Method = Method,
            DeclarationId = identity, Sig = signature, Args = new[] { value }, Ret = slot, Sty = slot };
    }
}
