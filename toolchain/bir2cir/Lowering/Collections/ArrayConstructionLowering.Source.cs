using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ArrayConstructionLowering
{
    public static BirFileDeclaration Apply(BirFileDeclaration root, ReferenceMetadataIndex refs = null)
    {
        Walk(root, ImmutableDictionary.Create<string, TypeNode>(StringComparer.Ordinal));
        return root;
    }

    static BirGraphValue Walk(BirGraphValue node, ImmutableDictionary<string, TypeNode> scope)
    {
        if (node == null || node is IBirExecutablePayload) return node;
        var childScope = scope;
        node.VisitSourceParameters((name, type) => childScope = childScope.SetItem(name, type));
        if (node is BirForArray loop)
        {
            // This operation owns the ordering: derive from the ORIGINAL array,
            // then lower that array, bind the variable in the body only, and
            // finally stamp the loop. An already-stamped elem is not a new
            // source-type authority for deriving the body's variable type.
            var element = DeriveArrayElem(loop.Array.OrDefault(), childScope);
            if (loop.Array.HasValue)
                loop.Array = OptionalField<IBirExpression>.Of((IBirExpression)Walk((BirGraphValue)loop.Array.Value, childScope));
            var bodyScope = element != null && loop.Var.HasValue
                ? childScope.SetItem(loop.Var.Value, element) : childScope;
            if (loop.Body.HasValue) WalkStatements(loop.Body.Value, bodyScope);
            if (element != null && !loop.Elem.HasValue)
            {
                loop.Elem = OptionalField<TypeNode>.Of(element);
                // Match the operation's canonical authored fields when the
                // source omitted them; physical loop IDs are assigned later.
                if (!loop.Label.IsPresent) loop.Label = OptionalField<string>.Null;
                if (!loop.Var.IsPresent) loop.Var = OptionalField<string>.Null;
                if (!loop.Body.IsPresent) loop.Body = OptionalField<IBirStatement[]>.Null;
                loop.LoopId = default;
            }
            return loop;
        }
        node.VisitSourceChildren(slot => slot.Replace(Walk(slot.Value, childScope)), region => {
            var current = WalkStatements(region.Statements, childScope);
            if (region.Result is { } result) result.Replace(Walk(result.Value, current));
        });
        return Lower(node, scope);
    }

    static ImmutableDictionary<string, TypeNode> WalkStatements(IBirStatement[] statements,
        ImmutableDictionary<string, TypeNode> scope)
    {
        var current = scope;
        for (var index = 0; index < statements.Length; index++)
        {
            statements[index] = (IBirStatement)Walk((BirGraphValue)statements[index], current);
            if (statements[index] is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                current = current.SetItem(variable.Name.Value, variable.Type.Value);
        }
        return current;
    }

    static BirGraphValue Lower(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> scope)
    {
        if (node is BirNewArrayInit existing && !existing.FuncType.HasValue
            && InitializerType(existing.Init.OrDefault()) is { } existingInitializer)
            existing.FuncType = OptionalField<TypeNode>.Of(existingInitializer);
        if (node is BirNew construction && construction.Type.OrDefault() is TypeNode.Fqn { Args: null } owner
            && BirTypeLowering.PrimArrayElem.TryGetValue(owner.Name, out var elementName))
        {
            var element = new TypeNode.Fqn(elementName);
            var args = construction.Args.OrEmpty();
            if (args.Length == 2)
            {
                var functionType = construction.ArgTypes.OrDefault() is { Length: > 1 } declared
                    ? declared[1] : InitializerType(args[1]);
                return new BirNewArrayInit {
                    Elem = element, Size = OptionalField<IBirExpression>.Of(args[0]),
                    Init = OptionalField<IBirExpression>.Of(args[1]),
                    FuncType = functionType == null ? default : OptionalField<TypeNode>.Of(functionType),
                };
            }
            if (args.Length == 1)
            {
                if (construction.ArgTypes.OrDefault() is { Length: 1 } declared && IsArrayTypeNode(declared[0]))
                    return construction;
                return new BirNewArraySized { Elem = element, Size = OptionalField<IBirExpression>.Of(args[0]) };
            }
            return construction;
        }
        if (node is BirArrayGet get && !get.Elem.HasValue && DeriveArrayElem(get.Array.OrDefault(), scope) is { } getElement)
            get.Elem = OptionalField<TypeNode>.Of(getElement);
        else if (node is BirArraySet set && !set.Elem.HasValue && DeriveArrayElem(set.Array.OrDefault(), scope) is { } setElement)
            set.Elem = OptionalField<TypeNode>.Of(setElement);
        return node;
    }

    static TypeNode InitializerType(IBirExpression expression) => (expression as BirGraphValue)?.FunctionTypeStamp;
    static TypeNode DeriveArrayElem(IBirExpression expression, IReadOnlyDictionary<string, TypeNode> scope)
        => expression == null ? null : ArrayElementOf(BirGraphStaticType.Surface(expression, scope));
}
