using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// One compilation owns declaration/temporary identities across construction and value-flow consumers.
// Consumers retain their own per-file caches and publish the returned declaration as before.
sealed class PhysicalDelegateDeclarations
{
    int nextAdapter;
    int nextValue;

    internal string FreshValueTemporary() => $"dotkt$unitDelegateValue{nextValue++}";

    internal BirClassDefinition CreateAdapter(IReadOnlySet<string> localTypes, string scope, ClrMemberResolution.DelegateAdapterPlan plan,
        ReferenceMetadataIndex references, Func<IBirExpression> unitValue,
        Func<IBirExpression, ClrMemberResolution.ReturnedFunction, IBirExpression> adaptResult)
    {
        var name = $"dotkt${scope}$UnitDelegateAdapter{nextAdapter++}";
        var self = new TypeNode.Fqn(name);
        var arguments = Enumerable.Range(0, plan.Arity)
            .Select(i => (IBirExpression)new BirLocal { Name = "p" + i }).ToArray();
        if (plan.Parameters != null)
            for (var i = 0; i < arguments.Length; i++)
                if (!plan.Frame.DelegateParams[i].Equals(plan.Parameters[i]))
                    arguments[i] = new BirCast { Type = plan.Frame.DelegateParams[i], E = OptionalField<IBirExpression>.Of(arguments[i]) };
        var call = new BirDelegateInvoke {
            FuncType = plan.Frame,
            SourceClrBoundary = plan.PublishArguments,
            Recv = new BirFieldGet { OwnerType = self, Recv = new BirThis(), Name = "d" },
            Args = arguments,
            InvokeRef = BirMemberReference.FromPhysical(
                ClrMemberResolution.DelegateInvokeReference(localTypes, references, plan.Frame)),
        };
        IBirStatement[] body;
        if (plan.Parameters == null)
            body = new IBirStatement[] {
                new BirExpressionStatement { Expr = call }, new BirReturn { Value = OptionalField<IBirExpression>.Of(unitValue()) },
            };
        else if (IsVoid(plan.Return))
            body = new IBirStatement[] {
                new BirExpressionStatement { Expr = call }, new BirReturn(),
            };
        else if (IsVoid(plan.Frame.Ret))
            body = new IBirStatement[] {
                new BirExpressionStatement { Expr = call }, new BirReturn { Value = OptionalField<IBirExpression>.Of(unitValue()) },
            };
        else
            body = new IBirStatement[] { new BirReturn {
                Value = OptionalField<IBirExpression>.Of(plan.ResultFunction is {} result ? adaptResult(call, result)
                    : plan.Frame.Ret.Equals(plan.Return) ? call : new BirCast { Type = plan.Return, E = call }),
            } };
        var invoke = new BirFunctionDeclaration {
            Name = "invoke", Static = false, Override = false, Virtual = false,
            Params = Enumerable.Range(0, plan.Arity).Select(i => new BirParameter {
                Name = "p" + i, Type = plan.Parameters?[i] ?? new TypeNode.Tv("type", i),
            }).ToArray(),
            Ret = plan.Return, Body = body, BodyTerminates = true, SourceClrBoundary = plan.PublishReturn,
        };
        var declaration = new BirClassDefinition {
            Name = name, Kind = "class", Generated = true,
            Base = OptionalField<TypeNode>.Null, Interfaces = Array.Empty<TypeNode>(),
            Fields = new[] { new BirFieldDeclaration { Name = "d", Type = plan.Frame } },
            Ctors = new[] { new BirConstructor {
                Params = new[] { new BirParameter { Name = "d", Type = plan.Frame } },
                BaseArgs = OptionalField<IBirExpression[]>.Null,
                Body = new IBirStatement[] { new BirSetField {
                    OwnerType = self, Recv = new BirThis(), Name = "d", Value = new BirLocal { Name = "d" },
                } },
            } },
            Methods = new[] { invoke },
        };
        var arity = plan.GenericArity ?? plan.Arity;
        if (arity != 0)
            declaration.TypeParams = Enumerable.Range(0, arity).Select(i => new BirTypeParameter {
                Name = "T" + i, SpecialConstraints = new[] { "allowsRefStruct" },
            }).ToArray();
        return declaration;
    }

    static bool IsVoid(TypeNode type) => type is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };
}
