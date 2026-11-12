using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A singleton-class enum has the same Kotlin comparison contract as a native CLR enum. Give it a real
// compiler-owned comparison face so dispatch works without an impossible System.Enum bound or taking over a
// user-authored CLR IComparable implementation.
// The ordinal field comes only from the producer's explicit enum map, never from a physical-name convention.
static class RichEnumComparisonSynthesis
{
    internal const string Face = "DotKt.Runtime.CompilerServices.KotlinEnumComparison";
    internal static BirFileDeclaration Apply(BirFileDeclaration root)
    {
        if (!root.Types.HasValue) return root;
        return root with { Types = root.Types.Value.Select(type =>
        {
            if (type is not BirClassDefinition { RichEnum.HasValue: true } declaration) return type;
            var owner = declaration.Name.Value;
            var ordinal = declaration.RichEnum.Value.Ordinal.Value;
            var names = declaration.Methods.OrEmpty().Select(method => method.Name.OrDefault()).ToHashSet();
            var suffix = 0;
            string name;
            do { name = "$enumComparison$" + suffix++; } while (names.Contains(name));
            BirFieldGet Ordinal(IBirExpression receiver) => new() {
                OwnerType = new TypeNode.Fqn(owner), Name = ordinal, Recv = OptionalField<IBirExpression>.Of(receiver),
            };
            var bridge = new BirFunctionDeclaration {
                Name = name, Static = false, Virtual = true, Override = false,
                Abstract = false, Vis = "private", Generated = true, PhysicalSlotBridge = true,
                Params = new[] { new BirParameter { Name = "other", Type = new TypeNode.Fqn("kotlin.Any") } },
                Ret = new TypeNode.Fqn("kotlin.Int"),
                Body = new IBirStatement[] { new BirReturn { Value = new BirBinaryOperation {
                    Op = "-", Lhs = Ordinal(new BirThis()),
                    Rhs = Ordinal(new BirCast { Type = new TypeNode.Fqn(owner), E = new BirLocal { Name = "other" } }),
                } } },
                ClrInterfaceImpls = new[] { new BirMethodImpl {
                    Owner = new TypeNode.Fqn(Face), Member = "compareEnumTo", Arity = 0,
                    Params = new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, Ret = new TypeNode.Fqn("kotlin.Int"),
                } },
            };
            return declaration with {
                Base = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinEnumComparable"),
                Ctors = declaration.Ctors.HasValue ? OptionalField<BirConstructor[]>.Of(declaration.Ctors.Value.Select(constructor =>
                    constructor.ThisArgs.HasValue ? constructor : constructor with {
                        BaseArgs = Array.Empty<IBirExpression>(), DelegationSig = Array.Empty<TypeNode>(), DelegationDeclarationId = default,
                    }).ToArray()) : declaration.Ctors,
                Interfaces = declaration.Interfaces.OrEmpty().Append(new TypeNode.Fqn(Face)).ToArray(),
                Methods = declaration.Methods.OrEmpty().Append(bridge).ToArray(),
            };
        }).ToArray() };
    }
}
