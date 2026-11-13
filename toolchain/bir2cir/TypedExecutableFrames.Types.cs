using DotKt.Bir;

static partial class TypedExecutableFrames
{
    // A selected application is inserted as a whole. In particular, an owner
    // TV inside a substituted method argument must not be substituted again.
    static TypeNode SubstituteType(TypeNode type, TypeNode[] methodArguments, int arity,
        TypeNode[] ownerArguments, bool typeScope) => RewriteType(type, variable => variable.Scope switch {
            "method" when variable.I >= 0 && variable.I < arity
                && variable.I < methodArguments.Length => methodArguments[variable.I],
            "type" when typeScope && ownerArguments != null && variable.I >= 0
                && variable.I < ownerArguments.Length => ownerArguments[variable.I],
            _ => null,
        });

    static TypeNode CopyType(TypeNode type) => RewriteType(type, _ => null);

    static TypeNode RewriteType(TypeNode type, Func<TypeNode.Tv, TypeNode> variable)
    {
        if (type == null) return null;
        if (type is TypeNode.Tv tv) return variable(tv) is { } replacement ? CopyType(replacement) : tv;
        return type switch {
            TypeNode.Fqn value => value with {
                Args = value.Args?.Select(item => RewriteType(item, variable)).ToArray(),
                Classifier = value.Classifier is { } classifier
                    ? classifier with { OwnerPath = classifier.OwnerPath.ToArray() } : null,
            },
            TypeNode.Star => type,
            TypeNode.Projection value => value with { Of = RewriteType(value.Of, variable) },
            TypeNode.Fn value => value with {
                Ret = RewriteType(value.Ret, variable), Params = value.Params.Select(item => RewriteType(item, variable)).ToArray(),
                Recv = RewriteType(value.Recv, variable), Ctx = value.Ctx?.Select(item => RewriteType(item, variable)).ToArray(),
            },
            TypeNode.Nullable value => value with { Of = RewriteType(value.Of, variable) },
            TypeNode.Oblivious value => value with { Of = RewriteType(value.Of, variable) },
            TypeNode.Array value => value with { Elem = RewriteType(value.Elem, variable) },
            TypeNode.ByRef value => value with { Of = RewriteType(value.Of, variable) },
            TypeNode.Ptr value => value with { Of = RewriteType(value.Of, variable) },
            TypeNode.FnPtr value => value with {
                Ret = RewriteType(value.Ret, variable), Params = value.Params.Select(item => RewriteType(item, variable)).ToArray(),
            },
            TypeNode.Mod value => value with { M = RewriteType(value.M, variable), Of = RewriteType(value.Of, variable) },
            _ => throw new InvalidOperationException("Unaccounted executable type-frame variant: " + type.GetType().Name),
        };
    }
}
