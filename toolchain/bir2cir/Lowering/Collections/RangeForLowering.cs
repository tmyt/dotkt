using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Local stdlib ranges retain the explicit accessor contract. Referenced
// IntRange values become a counter loop over one captured range expression.
static class RangeForLowering
{
    static int _tmp;
    public static BirFileDeclaration Apply(BirFileDeclaration root, bool stdlibBuild)
    {
        Walk(root);
        return root;
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            if (node is BirForRange { Range.HasValue: true, AccessOwner.HasValue: false } loop)
            {
                if (stdlibBuild) {
                    loop.RangeType = default;
                    loop.AccessOwner = new TypeNode.Fqn("kotlin.ranges.IntProgression");
                    loop.FirstM = KotlinPropertyAccessors.PhysicalName("first", "get");
                    loop.LastM = KotlinPropertyAccessors.PhysicalName("last", "get");
                    loop.StepM = KotlinPropertyAccessors.PhysicalName("step", "get");
                } else node = AppForm(loop);
            }
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node;
        }
    }

    static BirBlock AppForm(BirForRange loop)
    {
        var name = "__rng$" + System.Threading.Interlocked.Increment(ref _tmp);
        IBirExpression Access(string property) => new BirCallInstance {
            OwnerType = new TypeNode.Fqn("kotlin.ranges.IntProgression"), Virtual = true,
            Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = name }),
            Method = property, Prop = "get", Args = System.Array.Empty<IBirExpression>(),
        };
        return new BirBlock { Body = new IBirStatement[] {
            new BirVariable {
                Name = name, Type = loop.RangeType.IsPresent ? loop.RangeType : OptionalField<TypeNode>.Null,
                Init = loop.Range,
            },
            new BirFor {
                Label = loop.Label.IsPresent ? loop.Label : OptionalField<string>.Null,
                Var = loop.Var.IsPresent ? loop.Var : OptionalField<string>.Null,
                From = OptionalField<IBirExpression>.Of(Access("first")),
                To = OptionalField<IBirExpression>.Of(Access("last")), Cmp = "<=", Step = 1,
                Body = loop.Body.HasValue ? loop.Body : OptionalField<IBirStatement[]>.Of(System.Array.Empty<IBirStatement>()),
            },
        } };
    }
}
