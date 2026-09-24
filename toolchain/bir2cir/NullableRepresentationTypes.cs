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
    readonly Func<TypeNode.Fqn, bool, NullableRepresentationFrame, TypeNode> _argumentHead;
    readonly GenericRepresentationPolicy _policy;
    internal NullableRepresentationFrame OwnerFrame => _owner;
    internal NullableRepresentationFrame MethodFrame => _method;

    // These facts are expressed in the selected declaration's frame, not the lexical caller's frame.
    internal static bool IsDeclarationFrameKey(string key, string kind, JsonObject expression) => key is
        "sig" or "shapeTypes" or "paramSig" or "delegationSig"
        or "memberOwnerTypeParams" or "memberMethodTypeParams"
        or "sharedCellTypeParams"
        or "memberReturnType" or "memberSignature" or "memberType" or "awaitResult"
        || key == "retType" && kind == "callInline"
        || key == "argTypes" && kind != null && kind != "new"
        // BIR's exact result-stamp contract (spec §2.7): a result equal to sty is already caller-relative,
        // even on a constructed member/property call. Owner presence does not establish result ownership.
        || key == "ret" && kind is "callStatic" or "callInstance"
            && !(TypeJson.Read(expression["sty"]) is TypeNode stamp
                && stamp.Equals(TypeJson.Read(expression["ret"])));

    public NullableRepresentationTypes(NullableRepresentationFrame owner, NullableRepresentationFrame method,
        IReadOnlyDictionary<string, NullableRepresentationFrame> types, ValueTypeOracle isValue,
        Func<TypeNode.Fqn, bool, NullableRepresentationFrame, TypeNode> argumentHead = null,
        GenericRepresentationPolicy policy = null)
    {
        _owner = owner;
        _method = method;
        _types = types;
        _isValue = isValue;
        _argumentHead = argumentHead ?? (policy == null ? null : policy.ProjectArgumentHead);
        _policy = policy;
    }

    public TypeNode[] CloseMethod(NullableRepresentationFrame declaration, IReadOnlyList<TypeNode> arguments) =>
        declaration.Close(arguments, Argument, NullableArgument, StorageArgument, NullableStorageArgument);

    public TypeNode Slot(TypeNode type) => Rewrite(type, NullableGenericErasure.Pos.Slot);
    public TypeNode Argument(TypeNode type) => Rewrite(type, NullableGenericErasure.Pos.Argument);
    public TypeNode StorageArgument(TypeNode type) => Rewrite(type, NullableGenericErasure.Pos.Argument, storage: true);
    public bool IsStorageElement(string kind, string key) => _policy?.IsStorageElement(kind, key) == true;
    public TypeNode ArgumentForRole(TypeNode type, NullableRepresentationFrame.Role role) => role switch {
        NullableRepresentationFrame.Role.Ordinary => Argument(type),
        NullableRepresentationFrame.Role.Nullable => NullableArgument(type),
        NullableRepresentationFrame.Role.Storage => StorageArgument(type),
        NullableRepresentationFrame.Role.NullableStorage => NullableStorageArgument(type),
        _ => throw new InvalidOperationException("Unknown representation argument role"),
    };

    public TypeNode NullableArgument(TypeNode type) => NullableArgument(type, storage: false);
    public TypeNode NullableStorageArgument(TypeNode type) => NullableArgument(type, storage: true);

    TypeNode NullableArgument(TypeNode type, bool storage)
    {
        while (type is TypeNode.Nullable or TypeNode.Oblivious)
            type = type is TypeNode.Nullable nullable ? nullable.Of : ((TypeNode.Oblivious)type).Of;
        return Rewrite(new TypeNode.Nullable(type), NullableGenericErasure.Pos.Argument, storage);
    }

    public TypeNode Rewrite(TypeNode type, NullableGenericErasure.Pos position) => Rewrite(type, position, storage: false);

    TypeNode Rewrite(TypeNode type, NullableGenericErasure.Pos position, bool storage)
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
                .Variable(variable, storage ? NullableRepresentationFrame.Role.NullableStorage : NullableRepresentationFrame.Role.Nullable);
        }
        if (type is TypeNode.Tv storageVariable && storage)
        {
            var frame = storageVariable.Scope == "type" ? _owner : storageVariable.Scope == "method" ? _method
                : throw new InvalidOperationException("Unknown storage representation scope");
            return (frame ?? throw new InvalidOperationException("Missing storage representation frame"))
                .Variable(storageVariable, NullableRepresentationFrame.Role.Storage);
        }
        // This pass allocates open nullable frames, not concrete value boxing. Keep concrete V? intact until
        // NullableGenericErasure runs after reified nullability witnesses have consumed the source argument.
        return type switch {
            TypeNode.Tv { Scope: "type" } ownerVariable when _owner != null =>
                new TypeNode.Tv("type", _owner.SourcePosition(ownerVariable.I)),
            TypeNode.Tv { Scope: "method" } methodVariable when _method != null =>
                new TypeNode.Tv("method", _method.SourcePosition(methodVariable.I)),
            TypeNode.Nullable nullable => new TypeNode.Nullable(Rewrite(nullable.Of, position, storage)),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(Rewrite(oblivious.Of, position, storage)),
            TypeNode.Projection projection => new TypeNode.Projection(projection.Variance, Rewrite(projection.Of, position, storage)),
            TypeNode.Fqn named => Named(named, position, storage),
            TypeNode.Array array => new TypeNode.Array(Argument(array.Elem), array.Rank, array.SzArray),
            TypeNode.ByRef byRef => new TypeNode.ByRef(Slot(byRef.Of)),
            TypeNode.Fn function => new TypeNode.Fn(function.Suspend, Argument(function.Ret),
                function.Params.Select(Slot).ToArray(), function.Recv == null ? null : Slot(function.Recv),
                function.Clr, function.Ctx?.Select(Slot).ToArray()),
            _ => type,
        };
    }

    TypeNode Named(TypeNode.Fqn source, NullableGenericErasure.Pos position, bool storage)
    {
        var arguments = source.Args;
        _types.TryGetValue(source.Name, out var frame);
        TypeNode MapArgument(TypeNode type, NullableRepresentationFrame.Role role) =>
            ArgumentForRole(type, _policy?.ApplicationRole(source.Name, role) ?? role);
        var mapped = new TypeNode.Fqn(source.Name, arguments == null ? null
            : frame != null
                ? frame.Close(arguments,
                    type => MapArgument(type, NullableRepresentationFrame.Role.Ordinary),
                    type => MapArgument(type, NullableRepresentationFrame.Role.Nullable),
                    type => MapArgument(type, NullableRepresentationFrame.Role.Storage),
                    type => MapArgument(type, NullableRepresentationFrame.Role.NullableStorage))
                : arguments.Select(type => MapArgument(type, NullableRepresentationFrame.Role.Ordinary)).ToArray());
        if (position != NullableGenericErasure.Pos.Argument) return mapped;
        // The declaring binding supplies its concrete head projection. A frame can map open variables on its
        // own, but cannot derive storage heads from the spelling/layout of an already-lowered ordinary type.
        if (_argumentHead != null) return _argumentHead(mapped, storage, frame);
        if (storage) throw new InvalidOperationException("Concrete storage argument has no declaration-owned projection");
        return mapped;
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
        var roleFrame = new NullableRepresentationFrame(1, new[] { 0 }, storageIndices: new[] { 0 },
            nullableStorageIndices: new[] { 0 });
        var roleTypes = new Dictionary<string, NullableRepresentationFrame> {
            ["RoleStore"] = roleFrame,
            ["kotlin.collections.Collection"] = new NullableRepresentationFrame(1, new[] { 0 }),
        };
        static TypeNode ProjectArgumentHead(TypeNode.Fqn source, bool storage, NullableRepresentationFrame declaration) =>
            source.Name == "kotlin.collections.Collection"
                ? new TypeNode.Fqn(storage ? "System.Collections.Generic.ICollection"
                    : "System.Collections.Generic.IReadOnlyCollection", declaration.OrdinaryArguments(source.Args))
                : source;
        var roles = new NullableRepresentationTypes(roleFrame, roleFrame, roleTypes, _ => false, ProjectArgumentHead);
        var ownerSource = new TypeNode.Tv("type", 0);
        var methodSource = new TypeNode.Tv("method", 0);
        Equal(roles.StorageArgument(ownerSource), new TypeNode.Tv("type", 2), "scoped owner storage argument");
        Equal(roles.StorageArgument(methodSource), new TypeNode.Tv("method", 2), "scoped method storage argument");
        Equal(roles.NullableStorageArgument(methodSource), new TypeNode.Tv("method", 3), "method nullable-storage argument");
        Equal(roles.StorageArgument(new TypeNode.Nullable(ownerSource)), new TypeNode.Tv("type", 3),
            "storage of nullable source chooses nullable-storage");
        var roleClosure = roles.CloseMethod(roleFrame, new TypeNode[] { new TypeNode.Nullable(ownerSource) });
        if (!roleClosure.SequenceEqual(new TypeNode[] {
            new TypeNode.Tv("type", 1), new TypeNode.Tv("type", 1),
            new TypeNode.Tv("type", 3), new TypeNode.Tv("type", 3),
        })) throw new InvalidOperationException("Nullable role closure interpreted physical companions as source variables");
        Equal(roles.Slot(new TypeNode.Array(ownerSource)), new TypeNode.Array(ownerSource),
            "native array uses the ordinary source variable");
        Equal(roles.Slot(new TypeNode.Fqn("RoleStore", new TypeNode[] { methodSource })),
            new TypeNode.Fqn("RoleStore", Enumerable.Range(0, 4).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray()),
            "constructed type closes every declared representation role");
        var semanticCollection = new TypeNode.Fqn("kotlin.collections.Collection", new TypeNode[] { text });
        var rootCollection = new TypeNode.Fqn("System.Collections.Generic.IReadOnlyCollection", new TypeNode[] { text });
        var storageCollection = new TypeNode.Fqn("System.Collections.Generic.ICollection", new TypeNode[] { text });
        var concreteClosure = roles.CloseMethod(roleFrame, new TypeNode[] { semanticCollection });
        if (!concreteClosure.SequenceEqual(new TypeNode[] {
            rootCollection, new TypeNode.Nullable(rootCollection), storageCollection, new TypeNode.Nullable(storageCollection),
        })) throw new InvalidOperationException("Closed representation roles lost their declaration-owned head projection");
        Equal(roles.Slot(new TypeNode.Array(semanticCollection)), new TypeNode.Array(rootCollection),
            "array element consumes ordinary head projection");
        Equal(roles.Rewrite(new TypeNode.Fqn("Foreign", new TypeNode[] { semanticCollection }), NullableGenericErasure.Pos.Bound),
            new TypeNode.Fqn("Foreign", new TypeNode[] { semanticCollection }), "foreign descriptor bypasses head projections");
        Console.WriteLine("[nullable representation types] self-test OK (exact constructions, scoped frames, native positions)");
    }
}
