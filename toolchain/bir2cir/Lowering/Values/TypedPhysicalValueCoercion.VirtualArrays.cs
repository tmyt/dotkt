using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static PhysicalValueCoercion;

static partial class TypedPhysicalValueCoercion
{
    internal static void PreserveVirtualTargets(CirFileDeclaration file, ReferenceMetadataIndex refs)
    {
        var text = new TypeNode.Fqn("System.String");
        var access = new TypeNode.Fqn("System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes");
        AttributeApplication dependency = null, methodGroup = null;
        AttributeApplication Bind(params TypeNode[] args)
        {
            var attribute = ClrMemberResolution.ResolveAttributeCtor(null,
                new AttributeApplication(new TypeNode.Fqn("System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute"),
                    args, [], true, "System.Runtime"), refs);
            return attribute.MemberRef != null ? attribute : throw new InvalidOperationException("Cannot bind the virtual-target metadata dependency.");
        }
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
                // Linker member signatures are parsed, not literal metadata names.
                // A backtick-quoted Kotlin identifier may contain their delimiters.
                var root = method.Name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
                    ? (dependency ??= Bind(text)) with { Args = [new AttributeArgument.TextValue(signature)] }
                    : (methodGroup ??= Bind(access, new TypeNode.Fqn("System.Type"))) with { Args = [
                        new AttributeArgument.Int32Value((int)(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
                            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicMethods)),
                        new AttributeArgument.TypeValue(new TypeNode.Fqn(owner.Name))] };
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
        var boolean = new TypeNode.Fqn("System.Boolean");
        IBirExpression Inspects(IBirExpression receiver) => ArrayRuntimeCall("virtualTargetPublishesArrays", boolean, [Object], index,
            bound with {
                OwnerType = owner, CalleeOwner = owner, Virtual = true, Recv = Field(receiver), FuncType = slot,
                DelegateCtorRef = local ? default(OptionalField<BirMemberReference>) :
                    BirMemberReference.FromPhysical(ClrMemberResolution.DelegateConstructorReference(null, index.References, slot)),
            });
        if (call is not BirConstrainedCall cc || cc.RecvType.OrDefault() is not TypeNode.Tv tv) return Inspects;
        return saved => {
            // Never emit box !!T: the JIT rejects it for a ref-like T even in
            // a dead branch. Stack-only native structs must supply their own
            // interface implementation. Other structs may inherit a Kotlin DIM;
            // classify the real slot on a boxed copy, leaving the call on its address.
            var byteType = new TypeNode.Fqn("System.Byte");
            var handle = new TypeNode.Fqn("System.RuntimeTypeHandle");
            TypeNode[] parameters = [new TypeNode.ByRef(byteType), handle];
            const string runtime = "System.Runtime.CompilerServices.RuntimeHelpers";
            if (!index.References.TryResolveStaticMemberSignature(runtime, "Box", 0, true, parameters, null,
                    out _, out var method, out var declaring))
                throw new InvalidOperationException("bir2cir: missing RuntimeHelpers.Box(ref byte, RuntimeTypeHandle)");
            var box = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, index.References, method, "method", declaring, null));
            IBirExpression Location() => saved is BirByRefLoad load ? load.Ptr.OrDefault() ?? new BirLocal { Name = load.Local }
                : Expression(Graph(saved).DeepClone());
            var boxed = new BirNativeStatic { Type = box.DeclaringType, Method = box.Name, Ret = Object, MemberRef = box,
                Args = new IBirExpression[] { index.ReferenceView(tv, byteType, Location()), PhysicalTypeKindTest.Read(tv,
                    PhysicalTypeKindTest.Bind(index.References, nameof(Type.TypeHandle))) } };
            var reference = new BirByRefLoad { Elem = Object, Ptr = index.ReferenceView(tv, Object, Location()) };
            var receiver = new BirConditional { Type = Object, Cond = index.IsValueType(tv), Then = boxed, Else = reference };
            return new BirConditional { Type = boolean, Cond = PhysicalTypeKindTest.Read(tv,
                PhysicalTypeKindTest.Bind(index.References, nameof(Type.IsByRefLike))),
                Then = new BirConstant { Type = boolean, Value = new BirLiteral.BooleanLiteral(true) },
                Else = Field(Inspects(Cast(receiver, owner))) };
        };
    }
}
