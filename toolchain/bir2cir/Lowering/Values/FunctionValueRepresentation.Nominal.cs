using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FunctionValueRepresentation
{
    internal const string FunctionRuntime = "DotKt.Runtime.CompilerServices.NominalFunctionKt";
    const string FunctionSource = "DotKt.Runtime.CompilerServices.KotlinFunctionSource";
    const string CreateView = "dotktCreateFunctionView";
    static readonly TypeNode Int = new TypeNode.Fqn("kotlin.Int");

    sealed record InvokeSlot(TypeNode.Fn Function, TypeNode Owner, string Name, TypeNode[] Parameters,
        TypeNode Return, OptionalField<string> DeclarationId);

    // A fake override need not have a MethodDef on this class. The frontend also
    // supplies the implementation selected for inherited class and default methods.
    static IEnumerable<InvokeSlot> InvokeSlots(BirClassDefinition owner, TypeNode self)
    {
        static TypeNode.Fn Function(BirOverride[] overrides) => overrides.Select(slot => slot.Owner.OrDefault())
            .OfType<TypeNode.Fn>().FirstOrDefault(fn => !fn.Suspend);
        foreach (var method in owner.Methods.OrEmpty())
            if (Function(method.Overrides.OrEmpty()) is { } fn)
                yield return new(fn, self, method.Name.Value, method.Params.OrEmpty().Select(p => p.Type.Value).ToArray(),
                    method.Ret.Value, method.DeclarationId);
        foreach (var method in owner.InheritedClassMethods.OrEmpty())
            if (Function(method.Overrides.OrEmpty()) is { } fn && method.InheritedImplementation.OrDefault() is { } impl)
                yield return new(fn, impl.Owner.Value, impl.Member.Value, method.Params.OrEmpty().Select(p => p.Type.Value).ToArray(),
                    method.Ret.Value, impl.DeclarationId);
        foreach (var method in owner.InheritedDefaultMethods.OrEmpty())
            if (Function(method.Overrides.OrEmpty()) is { } fn && method.Implementation.OrDefault() is { } impl)
                yield return new(fn, impl.Owner.Value, impl.Member.Value, method.Params.OrEmpty(), method.Ret.Value, impl.DeclarationId);
    }

    internal static void MaterializeNominalFunctions(BirFileDeclaration[] roots, ValueTypeOracle byRefLike)
    {
        foreach (var owner in roots.SelectMany(root => root.Types.OrEmpty()).OfType<BirClassDefinition>())
        {
            var arity = owner.TypeParams.OrEmpty().Length + owner.CapturedTypeParams.OrEmpty().Length;
            var self = new TypeNode.Fqn(owner.Name.Value, arity == 0 ? null : Enumerable.Range(0, arity)
                .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
            var functions = InvokeSlots(owner, self).ToArray();
            if (functions.Length == 0) continue;
            var methods = owner.Methods.OrEmpty().ToList();
            var branches = new List<IBirStatement>();
            var delegateType = new TypeNode.Nullable(new TypeNode.Fqn("DotKt.Runtime.CompilerServices.FunctionDelegate"));
            foreach (var (edge, target, member, declared, ret, id) in functions)
            {
                var physical = (TypeNode.Fn)RepresentType(edge, byRefLike);
                var slots = physical.DelegateParams.ToArray();
                if (slots.Length != declared.Length)
                    throw new InvalidOperationException("Function supertype and invoke parameter counts disagree on " + self.Name);
                var name = "dotkt$functionView$" + slots.Length;
                var args = slots.Select((s, i) => (IBirExpression)new BirCast { Type = declared[i],
                    E = new BirLocal { Name = "p" + i, Sty = s } }).ToArray();
                var invoke = new BirCallInstance { OwnerType = target, Method = member, Recv = new BirThis(),
                    Sig = declared, Args = args, DeclarationId = id, Ret = ret };
                var unit = ret is TypeNode.Fqn { Name: "kotlin.Unit" or "void" };
                var body = new List<IBirStatement>();
                if (unit) body.Add(new BirExpressionStatement { Expr = invoke });
                body.Add(new BirReturn { Value = OptionalField<IBirExpression>.Of(unit
                    ? (IBirExpression)new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("kotlin.Unit"), Name = "INSTANCE" }
                    : new BirCast { Type = physical.Ret, E = invoke }) });
                methods.Add(new BirFunctionDeclaration { Name = name, Generated = true, Vis = "private", Static = false,
                    Params = slots.Select((s, i) => new BirParameter { Name = "p" + i, Type = s }).ToArray(),
                    Ret = physical.Ret, Body = body.ToArray(), BodyTerminates = true });
                var capture = new BirNewBoundDelegate { Recv = new BirThis(), OwnerType = self, CalleeOwner = self,
                    Method = name, Virtual = false, Sig = slots, FuncType = physical };
                branches.Add(new BirIf { Branches = new[] { new BirBranch { Cond = new BirBinaryOperation {
                    Op = "==", Lhs = new BirLocal { Name = "arity", Sty = Int },
                    Rhs = new BirConstant { Type = Int, Value = new BirLiteral.IntegerLiteral(slots.Length) } },
                    Body = new IBirStatement[] { new BirReturn { Value = capture } } } } });
            }
            branches.Add(new BirReturn { Value = new BirConstant { Type = delegateType, Value = new BirLiteral.NullLiteral() } });
            var contract = new TypeNode.Fqn(FunctionSource);
            methods.Add(new BirFunctionDeclaration { Name = "dotkt$createFunctionView", Generated = true, Static = false, Virtual = true,
                Vis = "private", ClrInterfaceSlotBridge = true,
                ClrInterfaceImpls = new[] { new BirMethodImpl { Owner = contract, Member = CreateView, Arity = 0,
                    Params = new[] { Int }, Ret = delegateType } },
                Params = new[] { new BirParameter { Name = "arity", Type = Int } }, Ret = delegateType,
                Body = branches.ToArray(), BodyTerminates = true });
            owner.Interfaces = owner.Interfaces.OrEmpty().Append(contract).ToArray();
            owner.Methods = methods.ToArray();
        }
    }
}
