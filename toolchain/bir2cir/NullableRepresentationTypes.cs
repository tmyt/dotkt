using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// The type operation used when materializing a declaration's explicit nullable frame. This does not choose
// declaration identities or discover frame demand; both are inputs. Original Kotlin types remain immutable.
sealed class NullableRepresentationTypes
{
    internal const string MethodFrameKey = "nullableRepresentationFrame";
    readonly NullableRepresentationFrame _owner;
    readonly NullableRepresentationFrame _method;
    readonly IReadOnlyDictionary<string, NullableRepresentationFrame> _types;
    readonly ValueTypeOracle _isValue;

    public NullableRepresentationTypes(NullableRepresentationFrame owner, NullableRepresentationFrame method,
        IReadOnlyDictionary<string, NullableRepresentationFrame> types, ValueTypeOracle isValue)
    {
        _owner = owner;
        _method = method;
        _types = types;
        _isValue = isValue;
    }

    public TypeNode[] CloseMethod(NullableRepresentationFrame declaration, IReadOnlyList<TypeNode> arguments) =>
        declaration.Close(arguments, Argument, NullableArgument);

    public TypeNode Slot(TypeNode type) => Rewrite(type, NullableGenericErasure.Pos.Slot);
    public TypeNode Argument(TypeNode type) => Rewrite(type, NullableGenericErasure.Pos.Argument);

    public TypeNode NullableArgument(TypeNode type)
    {
        while (type is TypeNode.Nullable or TypeNode.Oblivious)
            type = type is TypeNode.Nullable nullable ? nullable.Of : ((TypeNode.Oblivious)type).Of;
        return Argument(new TypeNode.Nullable(type));
    }

    public TypeNode Rewrite(TypeNode type, NullableGenericErasure.Pos position)
    {
        // Another CLR declaration owns this complete signature; its variables are not this lexical frame.
        if (position == NullableGenericErasure.Pos.Bound)
            return NullableGenericErasure.EraseBound(type, _isValue);
        if (type is TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn }) return type;
        if (type is TypeNode.Nullable { Of: TypeNode.Tv variable })
        {
            if (position == NullableGenericErasure.Pos.Slot) return new TypeNode.Fqn("object");
            var frame = variable.Scope switch {
                "type" => _owner,
                "method" => _method,
                _ => throw new InvalidOperationException("Unknown nullable representation scope"),
            };
            return (frame ?? throw new InvalidOperationException("Missing nullable representation frame"))
                .NullableVariable(variable);
        }
        // Retain the concrete nullable-value boxing policy. Only open nullable arguments need the extra frame.
        if (position == NullableGenericErasure.Pos.Argument
            && type is TypeNode.Nullable { Of: TypeNode.Fqn value } && _isValue(value))
            return new TypeNode.Fqn("object");
        return type switch {
            TypeNode.Nullable nullable => new TypeNode.Nullable(Slot(nullable.Of)),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(Rewrite(oblivious.Of, position)),
            TypeNode.Projection projection => new TypeNode.Projection(projection.Variance, Rewrite(projection.Of, position)),
            TypeNode.Fqn { Args: { } arguments } named => new TypeNode.Fqn(named.Name,
                _types.TryGetValue(named.Name, out var frame)
                    ? frame.Close(arguments, Argument, NullableArgument)
                    : arguments.Select(Argument).ToArray()),
            TypeNode.Array array => new TypeNode.Array(Argument(array.Elem), array.Rank, array.SzArray),
            TypeNode.ByRef byRef => new TypeNode.ByRef(Slot(byRef.Of)),
            TypeNode.Fn function => new TypeNode.Fn(function.Suspend, Argument(function.Ret),
                function.Params.Select(Slot).ToArray(), function.Recv == null ? null : Slot(function.Recv),
                function.Clr, function.Ctx?.Select(Slot).ToArray()),
            _ => type,
        };
    }

    public static void SelfTest()
    {
        static void Equal(TypeNode actual, TypeNode expected, string name)
        {
            if (actual != expected) throw new InvalidOperationException("Nullable representation type self-test: " + name);
        }
        var owner = new NullableRepresentationFrame(1, new[] { 0 });
        var method = new NullableRepresentationFrame(2, new[] { 1 });
        var types = new Dictionary<string, NullableRepresentationFrame> {
            ["Exchange"] = owner, ["Box"] = new NullableRepresentationFrame(1, Array.Empty<int>()),
        };
        var mapping = new NullableRepresentationTypes(owner, method, types, type => type.Name == "kotlin.Int");
        var tv = new TypeNode.Tv("type", 0);
        var mv = new TypeNode.Tv("method", 1);
        var tn = new TypeNode.Tv("type", 1);
        var mn = new TypeNode.Tv("method", 2);
        var text = new TypeNode.Fqn("kotlin.String");
        var integer = new TypeNode.Fqn("kotlin.Int");
        var obj = new TypeNode.Fqn("object");
        TypeNode Box(TypeNode type) => new TypeNode.Fqn("Box", new[] { type });
        Equal(mapping.Slot(Box(new TypeNode.Nullable(tv))), Box(tn), "owner nullable argument");
        Equal(mapping.Slot(Box(new TypeNode.Nullable(mv))), Box(mn), "method nullable argument");
        Equal(mapping.Slot(Box(new TypeNode.Nullable(text))), Box(new TypeNode.Nullable(text)), "native reference argument");
        Equal(mapping.Slot(Box(new TypeNode.Nullable(integer))), Box(obj), "boxed nullable value argument");
        Equal(mapping.Slot(new TypeNode.Nullable(integer)), new TypeNode.Nullable(integer), "native nullable scalar");
        Equal(mapping.Slot(new TypeNode.ByRef(new TypeNode.Nullable(tv))), new TypeNode.ByRef(obj), "direct ref scalar");
        Equal(mapping.Slot(new TypeNode.ByRef(Box(tv))), new TypeNode.ByRef(Box(tv)), "exact invariant ref referent");
        Equal(mapping.Slot(new TypeNode.Array(new TypeNode.Nullable(tv))), new TypeNode.Array(tn), "array element frame");
        Equal(mapping.NullableArgument(new TypeNode.Nullable(tv)), tn, "nullable idempotence");
        Equal(mapping.NullableArgument(new TypeNode.Oblivious(tv)), tn, "nullable platform type argument");
        Equal(mapping.Slot(new TypeNode.Fqn("Exchange", new TypeNode[] { tv })),
            new TypeNode.Fqn("Exchange", new TypeNode[] { tv, tn }), "transitive owner frame");
        Equal(mapping.Slot(new TypeNode.Fqn("Exchange", new TypeNode[] { new TypeNode.Nullable(tv) })),
            new TypeNode.Fqn("Exchange", new TypeNode[] { tn, tn }), "already nullable construction");
        var closed = mapping.CloseMethod(owner, new TypeNode[] { integer });
        Equal(closed[0], integer, "method ordinary argument");
        Equal(closed[1], obj, "method nullable argument");
        var foreign = new TypeNode.Fqn("Foreign", new TypeNode[] { new TypeNode.Nullable(integer) });
        Equal(mapping.Rewrite(foreign, NullableGenericErasure.Pos.Bound), foreign, "foreign fixed nullable construction");
        var function = new TypeNode.Fn(false, Box(new TypeNode.Nullable(mv)), new[] { new TypeNode.Nullable(integer) },
            text, "delegate.family", new[] { text });
        Equal(mapping.Slot(function), new TypeNode.Fn(false, Box(mn), function.Params, text, "delegate.family", function.Ctx),
            "delegate parameter and family ownership");
        Console.WriteLine("[nullable representation types] self-test OK (exact constructions, scoped frames, native positions)");
    }
}
