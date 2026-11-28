using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// One compilation owns declaration/temporary identities across construction and value-flow consumers.
// Consumers retain their own per-file caches and publish the returned declaration as before.
sealed class PhysicalDelegateDeclarations
{
    int nextAdapter;
    int nextValue;
    readonly HashSet<string> nativeResultTargets = new(StringComparer.Ordinal);
    internal bool PublishesResult(string name) => nativeResultTargets.Contains(name);

    internal string FreshValueTemporary() => $"dotkt$unitDelegateValue{nextValue++}";
    internal string FreshAdapterName(string scope, string role) => $"dotkt${scope}${role}{nextAdapter++}";

    internal BirClassDefinition CreateAdapter(IReadOnlySet<string> localTypes, string scope, ClrMemberResolution.DelegateAdapterPlan plan,
        ReferenceMetadataIndex references, Func<IBirExpression> unitValue,
        Func<IBirExpression, ClrMemberResolution.ReturnedFunction, IBirExpression> adaptResult)
    {
        var name = FreshAdapterName(scope, "UnitDelegateAdapter");
        var self = new TypeNode.Fqn(name);
        TypeNode captured = (TypeNode)plan.NominalSource ?? plan.Frame;
        var arguments = Enumerable.Range(0, plan.Arity)
            .Select(i => (IBirExpression)new BirLocal { Name = "p" + i }).ToArray();
        // The physical value-flow pass visits new nominal adapters. Let it convert
        // each invocation edge (including a nested delegate) in its own direction.
        if (plan.Parameters != null && plan.NominalSource == null)
            for (var i = 0; i < arguments.Length; i++)
                if (!plan.Frame.DelegateParams[i].Equals(plan.Parameters[i]))
                    arguments[i] = new BirCast { Type = plan.Frame.DelegateParams[i], E = OptionalField<IBirExpression>.Of(arguments[i]) };
        var receiver = new BirFieldGet { OwnerType = self, Recv = new BirThis(), Name = "d" };
        var invokeRef = BirMemberReference.FromPhysical(plan.NominalSource is {} named
            ? ClrMemberResolution.PhysicalDelegateMember(localTypes, references, named, constructor: false)
            : ClrMemberResolution.DelegateInvokeReference(localTypes, references, captured));
        IBirExpression call = plan.NominalSource != null ? new BirNativeInstance {
            Type = plan.NominalSource, Method = "Invoke", MemberRef = invokeRef, Recv = receiver,
            Dispatch = "callvirt", ArgTypes = plan.Frame.DelegateParams, Ret = plan.Frame.Ret, Args = arguments,
        } : new BirDelegateInvoke {
            FuncType = plan.Frame,
            SourceClrBoundary = plan.PublishArguments,
            Recv = receiver,
            Args = arguments,
            InvokeRef = invokeRef,
        };
        if (plan.NominalSource != null && SignatureValueTypes.Of(plan.Return) is TypeNode.ByRef)
            call = new BirByRefOf { Inner = OptionalField<IBirExpression>.Of(call) };
        IBirStatement[] body;
        if (plan.Parameters == null)
            body = new IBirStatement[] {
                new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) }, new BirReturn { Value = OptionalField<IBirExpression>.Of(unitValue()) },
            };
        else if (IsVoid(plan.Return))
            body = new IBirStatement[] {
                new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) }, new BirReturn(),
            };
        else if (IsVoid(plan.Frame.Ret))
            body = new IBirStatement[] {
                new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) }, new BirReturn { Value = OptionalField<IBirExpression>.Of(unitValue()) },
            };
        else
            body = new IBirStatement[] { new BirReturn {
                Value = OptionalField<IBirExpression>.Of(plan.ResultFunction is {} result ? adaptResult(call, result)
                    : plan.NominalSource != null || plan.Frame.Ret.Equals(plan.Return) ? call : new BirCast { Type = plan.Return, E = OptionalField<IBirExpression>.Of(call) }),
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
            Fields = new[] { new BirFieldDeclaration { Name = "d", Type = captured } },
            Ctors = new[] { new BirConstructor {
                Params = new[] { new BirParameter { Name = "d", Type = captured } },
                BaseArgs = OptionalField<IBirExpression[]>.Null,
                Body = new IBirStatement[] { new BirSetField {
                    OwnerType = self, Recv = new BirThis(), Name = "d", Value = new BirLocal { Name = "d" },
                } },
            } },
            Methods = new[] { invoke },
        };
        var arity = plan.GenericArity ?? plan.Arity;
        if (plan.PublishReturn)
        {
            nativeResultTargets.Add(name);
            declaration.Interfaces = new TypeNode[] { new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinCallbackEntry") };
            declaration.Methods = new[] { invoke, new BirFunctionDeclaration {
                Name = "dotktKotlinEntry", Vis = "public", Virtual = true, ClrFinal = true,
                Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("System.Object"),
                Body = new IBirStatement[] { new BirReturn { Value = (BirFieldGet)receiver.DeepClone() } }, BodyTerminates = true,
            } };
        }
        if (arity != 0)
            declaration.TypeParams = plan.LexicalFrame ?? Enumerable.Range(0, arity).Select(i => new BirTypeParameter {
                Name = "T" + i, SpecialConstraints = new[] { "allowsRefStruct" },
            }).ToArray();
        return declaration;
    }

    static bool IsVoid(TypeNode type) => type is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };
}
