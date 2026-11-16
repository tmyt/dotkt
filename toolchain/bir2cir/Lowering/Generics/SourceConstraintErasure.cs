using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Source declarations own the least-erased constraint list. Each representation
// rule selects which constraints move; this traversal preserves the whole list,
// including siblings, before applying that rule to the owning typed declaration.
static class SourceConstraintErasure
{
    internal static BirGraphValue Apply(BirGraphValue root,
        Func<TypeNode[], (TypeNode[] Retained, bool Changed)> rewrite, bool includeEmptyBounds)
    {
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is BirAttribute or BirOverride) return node;
            var parameters = node switch {
                BirFunctionDeclaration value => value.TypeParams,
                BirClassDefinition value => value.TypeParams,
                BirInterfaceDefinition value => value.TypeParams,
                BirEnumDefinition value => value.TypeParams,
                BirClosureDefinition value => value.TypeParams,
                BirRefTypeDeclaration value => value.TypeParams,
                BirInheritedClassMethod value => value.TypeParams,
                BirInheritedImplementation value => value.TypeParams,
                BirMethodImpl value => value.TypeParams,
                _ => default,
            };
            if (parameters.HasValue)
            {
                var bounds = new Dictionary<int, TypeNode[]>();
                var changed = false;
                var rewritten = parameters.Value.Select((parameter, index) => {
                    if (parameter.Constraints.OrDefault() is not { } constraints
                        || !includeEmptyBounds && constraints.Length == 0) return parameter;
                    bounds[index] = constraints;
                    var result = rewrite(constraints);
                    changed |= result.Changed;
                    return result.Changed ? parameter with { Constraints = result.Retained } : parameter;
                }).ToArray();
                if (changed)
                {
                    SourceTypeFacts Merge(OptionalField<SourceTypeFacts> previous) => previous.HasValue
                        ? previous.Value.Merge(bounds) : new SourceTypeFacts(bounds);
                    SourceBoundsSnapshot MethodBounds(OptionalField<SourceBoundsSnapshot> previous) => previous.HasValue
                        ? previous.Value.Merge(bounds) : new SourceBoundsSnapshot(bounds);
                    node = node switch {
                        BirFunctionDeclaration value => value with { TypeParams = rewritten, MethodTypeParameterBoundsPre = MethodBounds(value.MethodTypeParameterBoundsPre) },
                        BirClassDefinition value => value with { TypeParams = rewritten, KotlinSupertypesPre = Merge(value.KotlinSupertypesPre) },
                        BirInterfaceDefinition value => value with { TypeParams = rewritten, KotlinSupertypesPre = Merge(value.KotlinSupertypesPre) },
                        BirEnumDefinition value => value with { TypeParams = rewritten, KotlinSupertypesPre = Merge(value.KotlinSupertypesPre) },
                        BirClosureDefinition value => value with { TypeParams = rewritten, MethodTypeParameterBoundsPre = MethodBounds(value.MethodTypeParameterBoundsPre) },
                        BirRefTypeDeclaration value => value with { TypeParams = rewritten, MethodTypeParameterBoundsPre = MethodBounds(value.MethodTypeParameterBoundsPre) },
                        BirInheritedClassMethod value => value with { TypeParams = rewritten, MethodTypeParameterBoundsPre = MethodBounds(value.MethodTypeParameterBoundsPre) },
                        BirInheritedImplementation value => value with { TypeParams = rewritten, KotlinSupertypesPre = Merge(value.KotlinSupertypesPre) },
                        BirMethodImpl value => value with { TypeParams = rewritten, MethodTypeParameterBoundsPre = MethodBounds(value.MethodTypeParameterBoundsPre) },
                        _ => node,
                    };
                }
            }
            return node.RewriteChildren(Walk);
        }
        return Walk(root);
    }
}
