using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
    internal NullableRepresentationFrame OwnerFrame => _owner;
    internal NullableRepresentationFrame MethodFrame => _method;

    // These facts are expressed in the selected declaration's frame, not the lexical caller's frame.
    internal static bool IsDeclarationFrameKey(string key, string kind, JsonObject expression) => key is
        "sig" or "shapeTypes" or "paramSig" or "delegationSig"
        or "memberOwnerTypeParams" or "memberMethodTypeParams"
        or "memberReturnType" or "memberSignature" or "memberType" or "awaitResult"
        || key == "retType" && kind == "callInline"
        || key == "argTypes" && kind != null && kind != "new"
        // BIR's exact result-stamp contract (spec §2.7): a result equal to sty is already caller-relative,
        // even on a constructed member/property call. Owner presence does not establish result ownership.
        || key == "ret" && kind is "callStatic" or "callInstance"
            && !(TypeJson.Read(expression["sty"]) is TypeNode stamp
                && stamp.Equals(TypeJson.Read(expression["ret"])));

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
        // Preserve it until exact member binding has captured the foreign declaration (including NRT T?).
        // NullableGenericErasure owns the later physical projection; doing it here destroys member identity.
        if (position == NullableGenericErasure.Pos.Bound)
            return type;
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
        // This pass allocates open nullable frames, not concrete value boxing. Keep concrete V? intact until
        // NullableGenericErasure runs after reified nullability witnesses have consumed the source argument.
        return type switch {
            TypeNode.Tv { Scope: "type" } ownerVariable when _owner != null =>
                new TypeNode.Tv("type", _owner.SourcePosition(ownerVariable.I)),
            TypeNode.Tv { Scope: "method" } methodVariable when _method != null =>
                new TypeNode.Tv("method", _method.SourcePosition(methodVariable.I)),
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
        Equal(mapping.Slot(Box(new TypeNode.Nullable(integer))), Box(new TypeNode.Nullable(integer)), "deferred concrete nullable argument");
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
        Equal(closed[1], new TypeNode.Nullable(integer), "method nullable argument before concrete erasure");
        var foreign = new TypeNode.Fqn("Foreign", new TypeNode[] { new TypeNode.Nullable(integer) });
        Equal(mapping.Rewrite(foreign, NullableGenericErasure.Pos.Bound), foreign, "foreign fixed nullable construction");
        Equal(mapping.Rewrite(new TypeNode.Nullable(tv), NullableGenericErasure.Pos.Bound),
            new TypeNode.Nullable(tv), "foreign nullable variable declaration before exact binding");
        var function = new TypeNode.Fn(false, Box(new TypeNode.Nullable(mv)), new[] { new TypeNode.Nullable(integer) },
            text, "delegate.family", new[] { text });
        Equal(mapping.Slot(function), new TypeNode.Fn(false, Box(mn), function.Params, text, "delegate.family", function.Ctx),
            "delegate parameter and family ownership");
        Console.WriteLine("[nullable representation types] self-test OK (exact constructions, scoped frames, native positions)");
    }
}
