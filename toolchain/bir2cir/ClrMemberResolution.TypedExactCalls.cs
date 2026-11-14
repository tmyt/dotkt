using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static BirCallStatic ExactStaticCall(ReferenceMetadataIndex refs, MethodInfo method,
        TypeNode.Fqn declaringOwner, IReadOnlyList<IBirExpression> arguments)
    {
        var plan = PlanExactDispatch(refs, method, declaringOwner, isStatic: true);
        var call = new BirCallStatic {
            Owner = BirGraphValue.CopyType(plan.Reference.DeclaringType), Method = plan.Name, Virtual = false,
            Sig = plan.Reference.ParameterTypes.Select(BirGraphValue.CopyType).ToArray(),
            Ret = BirGraphValue.CopyType(plan.Result), Sty = BirGraphValue.CopyType(plan.Result),
            Args = ExactArguments(plan, arguments), MemberRef = BirMemberReference.FromPhysical(plan.Reference),
            DotktDeclarationIdentityBound = true,
        };
        if (plan.Visibility != null)
        {
            call.MemberVisibility = plan.Visibility;
            call.MemberOwnerTypeParams = ExactOwnerParameters(plan);
            call.MemberReturnType = BirGraphValue.CopyType(plan.Reference.ReturnType);
        }
        return call;
    }

    internal static BirCallInstance ExactInstanceCall(ReferenceMetadataIndex refs, MethodInfo method,
        TypeNode.Fqn declaringOwner, IBirExpression receiver, IReadOnlyList<IBirExpression> arguments)
    {
        var plan = PlanExactDispatch(refs, method, declaringOwner, isStatic: false);
        var call = new BirCallInstance {
            OwnerType = BirGraphValue.CopyType(plan.Reference.DeclaringType), Method = plan.Name, Virtual = true,
            Recv = OptionalField<IBirExpression>.Of(plan.Visibility == "protected" && receiver is BirThis
                ? (IBirExpression)((BirGraphValue)receiver).DeepClone()
                : new BirCast { Type = BirGraphValue.CopyType(plan.Reference.DeclaringType), E = OptionalField<IBirExpression>.Of(receiver) }),
            Sig = plan.Reference.ParameterTypes.Select(BirGraphValue.CopyType).ToArray(),
            Ret = BirGraphValue.CopyType(plan.Result), Sty = BirGraphValue.CopyType(plan.Result),
            Args = ExactArguments(plan, arguments), MemberRef = BirMemberReference.FromPhysical(plan.Reference),
            DotktDeclarationIdentityBound = true,
        };
        if (plan.Visibility != null)
        {
            call.MemberVisibility = plan.Visibility;
            call.MemberOwnerTypeParams = ExactOwnerParameters(plan);
            call.MemberReturnType = BirGraphValue.CopyType(plan.Reference.ReturnType);
        }
        return call;
    }

    static IBirExpression[] ExactArguments(ExactDispatchPlan plan, IReadOnlyList<IBirExpression> arguments)
        => arguments.Select((argument, index) => (IBirExpression)new BirCast {
            Type = BirGraphValue.CopyType(plan.Arguments[index]),
            E = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)argument).DeepClone()),
        }).ToArray();

    static OptionalField<BirTypeParameter[]> ExactOwnerParameters(ExactDispatchPlan plan)
        => plan.OwnerParameters == null ? OptionalField<BirTypeParameter[]>.Null
            : OptionalField<BirTypeParameter[]>.Of(plan.OwnerParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray());
}
