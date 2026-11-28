using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static PhysicalValueCoercion;

static partial class TypedPhysicalValueCoercion
{
    internal static void PreserveVirtualTargets(CirFileDeclaration file, ReferenceMetadataIndex refs)
    {
        AttributeApplication dependency = null;
        foreach (var owner in file.Types)
        {
            var methods = owner.Methods.OrEmpty();
            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (method.Static || method.Abstract.OrDefault() ||
                    !(method.Virtual || method.Override || owner.Kind == CirTypeKind.Interface ||
                      method.ClrInterfaceImpls.OrEmpty().Length != 0 || method.ClrBaseImpls.OrEmpty().Length != 0) ||
                    !method.Params.Any(p => MayHoldArray(SignatureValueTypes.Of(p.Type) is TypeNode.ByRef by ? by.Of : p.Type, refs))) continue;
                var arity = method.TypeParams.OrEmpty().Length;
                var signature = method.Name + (arity == 0 ? "" : "``" + arity);
                dependency ??= ClrMemberResolution.ResolveAttributeCtor(null,
                    new AttributeApplication(new TypeNode.Fqn("System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute"),
                        [new TypeNode.Fqn("System.String")], [], true, "System.Runtime"), refs);
                var root = dependency with { Args = [new AttributeArgument.TextValue(signature)] };
                methods[i] = method with { Attrs = method.Attrs.OrEmpty().Append(root).ToArray() };
            }
        }
    }

    static Func<IBirExpression, IBirExpression> VirtualArrayTarget(BirGraphValue call, Scope scope, Index index)
    {
        if (index.References == null || call is not (BirCallInstance or BirConstrainedCall) ||
            call is BirCallInstance ordinary && (!ordinary.Virtual.OrDefault() || ordinary.Super.OrDefault()) ||
            ParameterTypes(call) is not {} args || !args.Any(t => MayHoldArray(SignatureValueTypes.Of(t) is TypeNode.ByRef by ? by.Of : t, index)) ||
            CallOwner(call) is not TypeNode.Fqn owner) return null;
        var bound = call switch {
            BirCallInstance i => new BirNewBoundDelegate { Method = i.Method, Sig = i.Sig,
                CalleeParams = i.CalleeParams, CalleeRet = i.CalleeRet, TypeArgs = i.TypeArgs, MemberRef = i.MemberRef },
            BirConstrainedCall c => new BirNewBoundDelegate { Method = c.Method, Sig = c.Sig,
                CalleeParams = c.CalleeParams, CalleeRet = c.CalleeRet, TypeArgs = c.TypeArgs, MemberRef = c.MemberRef },
            _ => throw new InvalidOperationException(),
        };
        var member = Member(call);
        var result = member?.ReturnType.OrDefault() ?? bound.CalleeRet.OrDefault();
        result = result == null ? ExprType(call, scope, index) : Close(result, member == null ? owner.Args : OwnerArgs(member), MethodArgs(call));
        if (result == null) return null;
        if (call is BirConstrainedCall concrete && concrete.RecvType.OrDefault() is TypeNode.Fqn receiver && index.References.IsByRefLikeFqn(receiver))
            return _ => new BirConstant { Type = new TypeNode.Fqn("System.Boolean"), Value = new BirLiteral.BooleanLiteral(true) };
        bool local = args.Length > 16 || args.Append(result).Any(t => t is TypeNode.ByRef or TypeNode.FnPtr or TypeNode.Ptr or TypeNode.Mod);
        var signature = IsVoid(result) ? args : args.Append(result).ToArray();
        var slot = local ? index.Declarations.DispatchDelegate(index.Document, args, result)
            : new TypeNode.Fqn(IsVoid(result) ? "System.Action" : "System.Func", signature.Length == 0 ? null : signature);
        return saved => ArrayRuntimeCall("virtualTargetPublishesArrays", new TypeNode.Fqn("System.Boolean"), [Object], index,
            bound with {
                OwnerType = owner, CalleeOwner = owner, Virtual = true, Recv = Field(saved), FuncType = slot,
                DelegateCtorRef = local ? default(OptionalField<BirMemberReference>) :
                    BirMemberReference.FromPhysical(ClrMemberResolution.DelegateConstructorReference(null, index.References, slot)),
            });
    }
}
