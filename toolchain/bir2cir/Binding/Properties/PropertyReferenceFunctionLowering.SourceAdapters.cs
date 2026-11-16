using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PropertyReferenceFunctionLowering
{
    sealed record AdapterShape(int Arity, TypeNode.Fqn Interface, TypeNode[] Parameters,
        TypeNode Return, TypeNode[] Actuals, bool[] AllowsRefStruct);

    static AdapterShape PlanAdapter(TypeNode.Fqn authoredIface, TypeNode.Fn target)
    {
        if (authoredIface.Args is not { Length: > 0 } ifaceArgs)
            throw new InvalidOperationException("bir2cir: propertyFunction interface has no value argument");
        var arity = authoredIface.Name switch {
            "kotlin.reflect.KProperty0" or "kotlin.reflect.KMutableProperty0" => 0,
            "kotlin.reflect.KProperty1" or "kotlin.reflect.KMutableProperty1" => 1,
            "kotlin.reflect.KProperty2" or "kotlin.reflect.KMutableProperty2" => 2,
            _ => throw new InvalidOperationException($"bir2cir: propertyFunction names non-KProperty interface '{authoredIface.Name}'"),
        };
        if (ifaceArgs.Length != arity + 1 || target.DelegateParams.Length != arity)
            throw new InvalidOperationException($"bir2cir: KProperty{arity} cannot fill function slot with {target.DelegateParams.Length} parameter(s)");
        var actuals = new List<TypeNode>();
        var allowsRefStruct = new List<bool>();
        TypeNode Dense(TypeNode actual, bool propertyArgument)
        {
            if (!MentionsTypeVariable(actual)) return actual;
            var index = actuals.FindIndex(t => t.Equals(actual));
            if (index < 0)
            {
                index = actuals.Count;
                actuals.Add(actual);
                allowsRefStruct.Add(!propertyArgument);
            }
            else if (propertyArgument) allowsRefStruct[index] = false;
            return new TypeNode.Tv("type", index);
        }
        var iface = new TypeNode.Fqn($"kotlin.reflect.KProperty{arity}", ifaceArgs.Select(t => Dense(t, true)).ToArray());
        var parameters = target.DelegateParams.Select(t => Dense(t, false)).ToArray();
        var ret = Dense(target.Ret, false);
        return new(arity, iface, parameters, ret, actuals.ToArray(), allowsRefStruct.ToArray());
    }

    sealed partial class SourcePass
    {
        BirNewClosure Adapt(IBirExpression construction, TypeNode.Fqn authoredIface, TypeNode.Fn target)
        {
            var plan = PlanAdapter(authoredIface, target);
            string name;
            do name = $"dotkt${scope}$PropertyFunctionAdapter{nextAdapter++}";
            while (typeNames.Contains(name));
            typeNames.Add(name);
            var self = new TypeNode.Fqn(name);
            var field = new BirFieldDeclaration { Name = "p", Type = BirGraphValue.CopyType(plan.Interface) };
            var arguments = plan.Parameters.Select((parameter, i) => {
                IBirExpression argument = new BirLocal { Name = "p" + i };
                if (!parameter.Equals(plan.Interface.Args[i]))
                    argument = new BirCast { Type = BirGraphValue.CopyType(plan.Interface.Args[i]), E = Of(argument) };
                return argument;
            }).ToArray();
            var call = new BirCallInstance {
                OwnerType = BirGraphValue.CopyType(plan.Interface), Virtual = true,
                Recv = Of(new BirFieldGet { OwnerType = self, Recv = Of(new BirThis()), Name = "p" }),
                Method = "invoke", Sig = Enumerable.Range(0, plan.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray(),
                Args = arguments,
            };
            IBirExpression result = call;
            if (!plan.Interface.Args[^1].Equals(plan.Return) && plan.Return is TypeNode.Tv)
                result = new BirCast { Type = BirGraphValue.CopyType(plan.Return), E = Of(call) };
            var invoke = new BirFunctionDeclaration {
                Name = "invoke", Static = false, Override = false, Virtual = false,
                Params = plan.Parameters.Select((type, i) => new BirParameter { Name = "p" + i, Type = BirGraphValue.CopyType(type) }).ToArray(),
                Ret = BirGraphValue.CopyType(plan.Return),
                Body = new IBirStatement[] {
                    target.Ret is TypeNode.Fqn { Name: "kotlin.Unit" }
                        ? new BirExpressionStatement { Expr = Of(call) }
                        : new BirReturn { Value = Of(result) },
                },
            };
            var ctor = new BirConstructor {
                Params = new[] { new BirParameter { Name = "p", Type = BirGraphValue.CopyType(plan.Interface) } },
                BaseArgs = OptionalField<IBirExpression[]>.Null,
                Body = new IBirStatement[] { new BirSetField {
                    OwnerType = self, Recv = Of(new BirThis()), Name = "p", Value = Of(new BirLocal { Name = "p" }),
                } },
            };
            var adapter = new BirClassDefinition {
                Name = name, Kind = "class", Generated = true, Base = OptionalField<TypeNode>.Null,
                Interfaces = Array.Empty<TypeNode>(), Fields = new[] { field }, Ctors = new[] { ctor }, Methods = new[] { invoke },
            };
            if (plan.Actuals.Length != 0)
                adapter.TypeParams = plan.Actuals.Select((_, i) => new BirTypeParameter {
                    Name = "T" + i,
                    SpecialConstraints = plan.AllowsRefStruct[i]
                        ? OptionalField<string[]>.Of(new[] { "allowsRefStruct" }) : default,
                }).ToArray();
            adapters.Add(adapter);

            var captured = (BirGraphValue)((BirGraphValue)construction).DeepClone();
            if (captured is BirNew original) original.PropertyFunction = default;
            var closure = new BirNewClosure {
                ClosureType = plan.Actuals.Length == 0 ? self : new TypeNode.Fqn(name, plan.Actuals.Select(BirGraphValue.CopyType).ToArray()),
                Captures = new[] { (IBirExpression)captured }, Method = "invoke", FuncType = BirGraphValue.CopyType(target),
            };
            if (plan.Actuals.Length != 0) closure.TypeArgs = plan.Actuals.Select(BirGraphValue.CopyType).ToArray();
            return closure;
        }
    }
}
