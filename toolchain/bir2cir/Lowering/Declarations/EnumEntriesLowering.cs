using System.Threading;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Author ordinary, declaration-bound stdlib calls before representation demand analysis.
// Keep the cold enumValues operation semantic until inline specialization has selected its enum type.
static class EnumEntriesLowering
{
    const string Owner = "kotlin.enums.EnumEntriesCacheKt";
    static int counter;

    public static BirGraphValue Apply(BirGraphValue node, ReferenceMetadataIndex refs,
        Func<BirCallStatic, BirCallStatic> bind)
    {
        if (node is IBirExecutablePayload) return node;
        node.VisitChildSlots(child => child.Replace(Apply(child.Value, refs, bind)));
        if (node is not BirCallStatic call || !call.Args.HasValue || call.Args.Value.Length != 0) return node;

        TypeNode enumType = null;
        if (call.Method.OrDefault() == "entries" && call.Prop.OrDefault() == "get"
            && call.OwnerType.OrDefault() is TypeNode.Fqn owner
            && call.Ret.OrDefault() is TypeNode.Fqn { Name: "kotlin.enums.EnumEntries", Args.Length: 1 } result
            && result.Args[0] is TypeNode.Fqn entry && entry == owner)
            enumType = entry;
        else if (call.Method.OrDefault() is "enumEntries" or "enumEntriesIntrinsic"
            && call.Owner.OrDefault() == null && !call.OwnerType.IsPresent && !call.Prop.IsPresent
            && IsIntrinsic(call, refs) && call.TypeArgs.HasValue && call.TypeArgs.Value.Length == 1)
            enumType = call.TypeArgs.Value[0];
        if (enumType == null) return node;

        var entriesType = new TypeNode.Fqn("kotlin.enums.EnumEntries", new[] { enumType });
        var any = new TypeNode.Fqn("kotlin.Any");
        var anyN = new TypeNode.Nullable(any);
        var read = bind(new BirCallStatic {
            Owner = new TypeNode.Fqn(Owner), Method = "cachedEnumEntries",
            Sig = new TypeNode[] { any }, Args = new IBirExpression[] { new BirClassReference { Type = enumType } }, Ret = anyN,
        });
        var create = bind(new BirCallStatic {
            Owner = new TypeNode.Fqn(Owner), Method = "getOrCreateEnumEntries",
            Sig = new TypeNode[] { any, new TypeNode.Array(new TypeNode.Tv("method", 0)) },
            TypeArgs = new[] { enumType }, Ret = entriesType,
            Args = new IBirExpression[] { new BirClassReference { Type = enumType }, new BirEnumValues { Type = enumType } },
        });
        var name = "__enumEntries$" + Interlocked.Increment(ref counter);
        return new BirValueBlock {
            Type = entriesType,
            Stmts = new IBirStatement[] { new BirVariable { Name = name, Type = anyN, Init = read } },
            Result = new BirConditional {
                Cond = new BirObjectEquals {
                    Lhs = new BirLocal { Name = name },
                    Rhs = new BirConstant { Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Nothing")),
                        Value = OptionalField<BirLiteral>.Null },
                },
                Then = create,
                Else = new BirCast { Type = entriesType, E = new BirLocal { Name = name } },
            },
        };
    }

    static bool IsIntrinsic(BirCallStatic call, ReferenceMetadataIndex refs)
    {
        static bool IsEntriesOwner(string owner) => owner is "kotlin.enums.EnumEntriesKt" or "kotlin.enums.EnumEntriesClrKt";
        // Local stdlib calls retain their frontend owner. Referenced calls retain the selected declaration identity.
        // A same-named user function, local or imported, is not an enum intrinsic.
        if (call.CalleeOwner.OrDefault() is TypeNode.Fqn callee && IsEntriesOwner(callee.Name)) return true;
        return refs.TryDeclarationIdentity(call.DeclarationId.OrDefault(),
            out _, out var owner, out _, out _) && IsEntriesOwner(owner);
    }

    internal static void SelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var parameter = new TypeNode.Tv("method", 0);
        BirCallStatic Call(string owner) => new() {
            Owner = OptionalField<TypeNode>.Null, Method = "enumEntries", Sig = Array.Empty<TypeNode>(),
            TypeArgs = new TypeNode[] { parameter }, Args = Array.Empty<IBirExpression>(),
            CalleeOwner = new TypeNode.Fqn(owner),
        };
        var bound = new List<BirCallStatic>();
        BirCallStatic Bind(BirCallStatic call) { bound.Add(call); return call; }
        var intrinsic = Apply(Call("kotlin.enums.EnumEntriesKt"), refs, Bind);
        if (intrinsic is not BirValueBlock block || bound.Count != 2
            || bound[0].Method.OrDefault() != "cachedEnumEntries"
            || bound[1].Method.OrDefault() != "getOrCreateEnumEntries"
            || bound[1].TypeArgs.Value[0] != parameter
            || bound[1].Args.Value[1] is not BirEnumValues values || values.Type.OrDefault() != parameter
            || block.Result.OrDefault() is not BirConditional conditional
            || conditional.Then.OrDefault() is not BirCallStatic then || then.Args.Value[1] is not BirEnumValues)
            throw new InvalidOperationException("Enum entries lost its lazy cache or source-generic helper binding");
        var shadow = Call("user.EnumEntriesKt");
        if (!ReferenceEquals(Apply(shadow, refs, _ => throw new InvalidOperationException("A user enumEntries function became an intrinsic")), shadow))
            throw new InvalidOperationException("A user enumEntries function was rewritten");
        var overload = Call("kotlin.enums.EnumEntriesKt");
        overload.Args = new IBirExpression[] { new BirLocal { Name = "provider" } };
        Apply(overload, refs, _ => throw new InvalidOperationException("The enumEntries provider overload was rewritten"));
        Console.WriteLine("[enum entries] self-test OK (lazy cache, source frame, intrinsic identity, provider overload)");
    }
}
