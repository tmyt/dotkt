using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NetInteropBinding
{
    static BirValueBlock LowerProjectedComparison(BirCallStatic call, ReferenceMetadataIndex refs, int site)
    {
        var binding = call.ExternalMember.Value;
        var strict = (binding.Member.Value, binding.PairedMember.Value) switch {
            ("op_LessThan", "op_GreaterThan") => true,
            ("op_LessThanOrEqual", "op_GreaterThanOrEqual") => false,
            _ => throw new InvalidOperationException("Unknown native comparison pair"),
        };
        var slots = call.ShapeTypes.OrDefault() ?? call.ArgTypes.Value;
        var typeArgs = call.TypeArgs.OrEmpty();
        var statements = new List<IBirStatement>();
        var boolean = new TypeNode.Fqn("System.Boolean");
        var integer = new TypeNode.Fqn("System.Int32");
        string Bind(string role, TypeNode type, IBirExpression value)
        {
            var name = $"dotkt$comparison${site}${role}";
            statements.Add(new BirVariable { Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(value) });
            return name;
        }
        var operands = call.Args.Value.Select((arg, index) => Bind(index.ToString(),
            FBoundStarProjectionErasure.SubstituteMethodTypeArguments(slots[index], typeArgs), arg)).ToArray();
        IBirExpression Invoke(string member)
        {
            var native = new BirCallStatic { OwnerType = call.OwnerType, Method = member, Ret = boolean, Sty = boolean,
                Args = operands.Select(n => (IBirExpression)new BirLocal { Name = n }).ToArray(),
                Sig = call.Sig, ShapeTypes = call.ShapeTypes, ArgTypes = call.ArgTypes, TypeArgs = call.TypeArgs,
                ExternalMember = new BirExternalMemberBinding { Owner = binding.Owner, Member = member } };
            return (IBirExpression)LowerCall(new SourceCall(native), refs);
        }
        BirConstant Int(int i) => new() { Type = integer, Value = new BirLiteral.IntegerLiteral(i) };
        BirConditional Choose(IBirExpression test, IBirExpression yes, IBirExpression no) => new() {
            Type = integer, Cond = OptionalField<IBirExpression>.Of(test),
            Then = OptionalField<IBirExpression>.Of(yes), Else = OptionalField<IBirExpression>.Of(no) };
        var less = Invoke(binding.Member.Value);
        var greater = Invoke(binding.PairedMember.Value);
        IBirExpression result;
        if (strict) result = Choose(less, Int(-1), Choose(greater, Int(1), Int(0)));
        else
        {
            var le = Bind("le", boolean, less);
            var ge = Bind("ge", boolean, greater);
            result = new BirBinaryOperation { Op = "-",
                Lhs = Choose(new BirLocal { Name = ge }, Int(1), Int(0)),
                Rhs = Choose(new BirLocal { Name = le }, Int(1), Int(0)) };
        }
        return new BirValueBlock { Stmts = statements.ToArray(), Result = OptionalField<IBirExpression>.Of(result) };
    }

    static void BindProjectedStaticFrame(SourceCall call)
    {
        var binding = call.ExternalMember.Value;
        var arguments = call.TypeArguments.OrEmpty();
        if (binding.Owner.Value.Type is not TypeNode.Fqn open)
            throw new InvalidOperationException("Projected static member requires a named declaring type");
        var ownerParameters = open.Args ?? [];
        var methodArity = arguments.Length - ownerParameters.Length;
        if (methodArity < 0 || !ownerParameters.Select((argument, index) => argument is TypeNode.Tv { Scope: "method" } tv
                && tv.I == methodArity + index).All(match => match))
            throw new InvalidOperationException("Projected static member owner does not match its declared argument frame");
        var nativeFrame = arguments.Select((_, index) => (TypeNode)(index < methodArity
            ? new TypeNode.Tv("method", index) : new TypeNode.Tv("type", index - methodArity))).ToArray();
        call.Owner = FBoundStarProjectionErasure.SubstituteMethodTypeArguments(open, arguments);
        call.Method = binding.Member;
        call.TypeArguments = methodArity == 0 ? default : arguments[..methodArity];
        if (call.Shapes.HasValue) call.Shapes = call.Shapes.Value.Select(shape =>
            FBoundStarProjectionErasure.SubstituteMethodTypeArguments(shape, nativeFrame)).ToArray();
        ((BirCallStatic)call.Node).ExternalMember = default;
    }

    static BirGraphValue LowerSelectedExternalMember(SourceCall call, ReferenceMetadataIndex refs)
    {
        var binding = call.ExternalMember.Value;
        var sourceOwner = call.Owner.OrDefault() as TypeNode.Fqn
            ?? throw new InvalidOperationException("Selected external member has no declaration owner frame");
        var owner = SupertypeGraph.SubstOwnerTvs(binding.Owner.Value.Type,
            sourceOwner.Args ?? Array.Empty<TypeNode>()) as TypeNode.Fqn
            ?? throw new InvalidOperationException("Selected external member owner is not a named declaration");
        var open = ClrMemberResolution.ResolveOwnerType(owner, refs)
            ?? throw new InvalidOperationException("Selected external member owner does not resolve");
        // The projection selected this declaration, not an overload candidate.
        // In particular, two constructions can share its MethodDef token while
        // their explicit owner vectors remain distinct.
        var method = open.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == binding.Member.Value);
        var reference = ClrMemberResolution.MemberRefOf(null, refs, method, MemberRefNode.Kinds.Method, open, owner.Args);
        // A reference receiver can be a subtype of its declaration owner. Its
        // value is interface-compatible, but &Subtype is not &DeclarationOwner.
        if (!refs.IsValueType(sourceOwner))
            return new BirNativeInstance {
                Type = owner, Method = method.Name,
                Recv = call.Receiver, Args = call.Arguments, Ret = call.Ret, Sty = call.Sty,
                Dispatch = "callvirt", Sig = reference.ParameterTypes, CalleeParams = reference.ParameterTypes,
                MemberRef = BirMemberReference.FromPhysical(reference), ResolvedMemberReturn = reference.ReturnType,
                SuspendCall = call.Suspend, _capturedGenericArgument = call.CapturedArguments,
                _capturedGenericResult = call.CapturedResult,
            };
        return new BirConstrainedCall {
            Iface = owner, RecvType = sourceOwner, Method = method.Name,
            Recv = call.Receiver, Args = call.Arguments, Ret = call.Ret, Sty = call.Sty,
            // These remain in the selected declaration's OPEN frame. Existing
            // call-slot consumers apply the explicit Iface arguments once.
            Sig = reference.ParameterTypes, CalleeParams = reference.ParameterTypes, CalleeRet = reference.ReturnType,
            MemberRef = BirMemberReference.FromPhysical(reference), ResolvedMemberReturn = reference.ReturnType,
            SuspendCall = call.Suspend, _capturedGenericArgument = call.CapturedArguments,
            _capturedGenericResult = call.CapturedResult,
        };
    }
}
