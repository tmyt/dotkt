using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Kotlin Comparable<X> supplies only the generic CLR face. Erased consumers and Comparable<*> slots
// also require System.IComparable, so non-reference builds add its CompareTo(Any) forwarding bridge
// on classes. Keep the target's semantic return stamp until normalization: a legal Nothing override
// must terminate this physical Int32 slot rather than return Nothing's CLR object erasure (#321).
static partial class ComparableBridgeSynthesis
{
    // Preserve vector order: the last non-collapsing application is the selected argument,
    // while ANY already non-generic/collapsing face suppresses this additional bridge.
    static TypeNode SelectComparableArgument(IEnumerable<TypeNode> interfaces, Func<TypeNode,TypeNode> physical)
    {
        TypeNode argument = null;
        var nonGeneric = false;
        foreach (var type in interfaces)
        {
            if (type is not TypeNode.Fqn face) continue;
            if (face.Name == "System.IComparable" && face.Args == null) nonGeneric = true;
            else if (face.Name is not ("kotlin.Comparable" or "System.IComparable")) continue;
            else if (face.Args is { Length:1 })
            {
                if (BirTypeLowering.ComparableApplicationCollapses(physical?.Invoke(face.Args[0]) ?? face.Args[0])) nonGeneric = true;
                else argument = face.Args[0];
            }
        }
        return nonGeneric ? null : argument;
    }

    static bool IsErasedCompareTo(string name, IReadOnlyList<TypeNode> parameters)
        => name == "CompareTo" && parameters is { Count:1 }
            && parameters[0] is TypeNode.Fqn { Args:null, Name:"object" or "kotlin.Any" };

    static bool IsComparableOverride(BirOverride edge)
        => edge.Owner.OrDefault() is TypeNode.Fqn { Name:"kotlin.Comparable" or "System.IComparable", Args.Length:1 }
            && edge.Member.OrDefault() is "compareTo" or "CompareTo" && edge.Kind.OrDefault() == "method";

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, Func<TypeNode,TypeNode> physical,
        Action<BirFileDeclaration,IBirTypeDeclaration,BirFunctionDeclaration> onMaterialized)
    {
        ArgumentNullException.ThrowIfNull(onMaterialized);
        foreach (var file in roots)
        foreach (var declaration in file.Types.OrEmpty())
        {
            if (declaration.Kind.OrDefault() != "class" || !declaration.Interfaces.HasValue) continue;
            var selfArgument = SelectComparableArgument(declaration.Interfaces.Value, physical);
            if (selfArgument == null) continue;
            if (!declaration.Methods.HasValue) declaration.Methods = Array.Empty<BirFunctionDeclaration>();
            var methods = declaration.Methods.Value;
            // A declared erased slot (including a prior synthesized bridge) already satisfies this ABI.
            if (methods.Any(method => IsErasedCompareTo(method.Name.OrDefault(),
                    method.Params.OrDefault()?.Select(parameter => parameter.Type.OrDefault()).ToArray()))) continue;
            var name = declaration.Name.OrDefault();
            if (string.IsNullOrEmpty(name)) continue;
            // The source edge identifies Comparable; compare the selected CLR face to the method's
            // actual parameter, not to the source type argument recorded on that edge.
            TypeNode Physical(TypeNode type) => physical?.Invoke(type) ?? type;
            var physicalArgument = Physical(selfArgument);
            var targets = methods.Where(method => method.Name.OrDefault() is "CompareTo" or "compareTo"
                && method.Params.HasValue && method.Params.Value.Length == 1
                && method.Params.Value[0].Type.HasValue
                && Physical(method.Params.Value[0].Type.Value).Equals(physicalArgument)
                && method.Overrides.OrEmpty().Any(IsComparableOverride)).ToArray();
            if (targets.Length != 1) continue;
            var target = targets[0];
            var targetParameter = target.Params.Value[0].Type.Value;
            var call = new BirCallInstance {
                OwnerType = new TypeNode.Fqn(name), Virtual = true, ClrOwnerResolved = true,
                Recv = new BirThis(), Method = target.Name,
                Sig = new[] { BirGraphValue.CopyType(targetParameter) },
                Args = new IBirExpression[] { new BirCast {
                    Type = BirGraphValue.CopyType(targetParameter), E = OptionalField<IBirExpression>.Of(new BirLocal { Name="obj" }),
                } },
                // Nothing's semantic result is essential here: later termination must not return its
                // object erasure from this physical Int32 bridge. Only an actual return value supplies a stamp.
                Sty = target.Ret.HasValue ? OptionalField<TypeNode>.Of(BirGraphValue.CopyType(target.Ret.Value)) : default,
            };
            var bridge = MaterializedExecutable.NormalizeSource(new BirFunctionDeclaration {
                Name = "CompareTo", Static = false, Override = false, Virtual = true,
                Abstract = false, ObjectOverride = false, Vis = "public",
                Params = new[] { new BirParameter { Name="obj", Type=new TypeNode.Fqn("kotlin.Any") } },
                Ret = new TypeNode.Fqn("kotlin.Int"),
                Body = new IBirStatement[] { new BirReturn { Value=OptionalField<IBirExpression>.Of(call) } },
            });
            declaration.Interfaces = declaration.Interfaces.Value.Append<TypeNode>(new TypeNode.Fqn("System.IComparable")).ToArray();
            declaration.Methods = methods.Append(bridge).ToArray();
            onMaterialized(file,declaration,bridge);
        }
    }
}
