using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The final value-flow owner reuses the construction owner's representation
// plan and exact CLR bindings. Only declaration and expression construction is
// different: every occurrence below is an owning typed value, never a DOM view.
sealed class TypedPhysicalDelegateAdapter
{
    readonly ReferenceMetadataIndex references;
    readonly Func<IBirExpression> unitValue;
    readonly Dictionary<string, string> adapters = new(StringComparer.Ordinal);
    BirFileDeclaration document;
    string scope;

    internal TypedPhysicalDelegateAdapter(ReferenceMetadataIndex references, Func<IBirExpression> unitValue)
    {
        this.references = references;
        this.unitValue = unitValue;
    }

    internal IBirExpression AdaptUnitDelegateValue(BirFileDeclaration document, IBirExpression expression,
        TypeNode source, TypeNode target)
    {
        var plan = ClrMemberResolution.UnitDelegateValuePlan(references, source, target, out var slot);
        if (plan == null) return null;
        if (!ReferenceEquals(this.document, document))
        {
            this.document = document;
            adapters.Clear();
            scope = string.Concat((document.FileClass.OrDefault() ?? "File")
                .Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        }
        var temporary = ClrMemberResolution.FreshUnitDelegateTemporary();
        var adapter = AdapterClass(plan);
        var adapted = new BirNewClosure {
            ClosureType = new TypeNode.Fqn(adapter), Method = "invoke",
            Captures = new IBirExpression[] { new BirLocal { Name = temporary } }, FuncType = slot,
            DelegateCtorRef = BirMemberReference.FromPhysical(
                ClrMemberResolution.DelegateConstructorReference(references, slot)),
        };
        if (plan.Parameters != null || plan.Arguments.Length != 0) adapted.TypeArgs = plan.Arguments.ToArray();

        // The source is transferred to exactly one initializer. Testing and
        // capturing separate temporary reads preserves effects and nullable values.
        return new BirValueBlock {
            Type = target,
            Stmts = new IBirStatement[] { new BirVariable {
                Name = temporary, Type = source, Init = OptionalField<IBirExpression>.Of(expression),
            } },
            Result = new BirConditional {
                Type = target,
                Cond = new BirBinaryOperation {
                    Op = "===", Lhs = new BirLocal { Name = temporary }, Rhs = Null(),
                },
                Then = Null(), Else = adapted,
            },
        };
    }

    static BirConstant Null() => new() {
        Type = new TypeNode.Fqn("System.Object"), Value = new BirLiteral.NullLiteral(),
    };

    string AdapterClass(ClrMemberResolution.DelegateAdapterPlan plan)
    {
        var key = ClrMemberResolution.DelegateAdapterKey(plan);
        if (adapters.TryGetValue(key, out var existing)) return existing;
        var name = ClrMemberResolution.FreshDelegateAdapterName(scope);
        var self = new TypeNode.Fqn(name);
        var arguments = Enumerable.Range(0, plan.Arity)
            .Select(i => (IBirExpression)new BirLocal { Name = "p" + i }).ToArray();
        if (plan.Parameters != null)
            for (var i = 0; i < arguments.Length; i++)
                if (!plan.Frame.DelegateParams[i].Equals(plan.Parameters[i]))
                    arguments[i] = new BirCast { Type = plan.Frame.DelegateParams[i], E = OptionalField<IBirExpression>.Of(arguments[i]) };
        var call = new BirDelegateInvoke {
            FuncType = plan.Frame,
            Recv = new BirFieldGet { OwnerType = self, Recv = new BirThis(), Name = "d" },
            Args = arguments,
            InvokeRef = BirMemberReference.FromPhysical(
                ClrMemberResolution.DelegateInvokeReference(references, plan.Frame)),
        };
        IBirStatement[] body;
        if (plan.Parameters == null || IsVoid(plan.Frame.Ret))
            body = new IBirStatement[] {
                new BirExpressionStatement { Expr = call }, new BirReturn { Value = OptionalField<IBirExpression>.Of(unitValue()) },
            };
        else if (IsVoid(plan.Return))
            body = new IBirStatement[] {
                new BirExpressionStatement { Expr = call }, new BirReturn(),
            };
        else
            body = new IBirStatement[] { new BirReturn {
                Value = plan.Frame.Ret.Equals(plan.Return) ? call : new BirCast { Type = plan.Return, E = call },
            } };
        var invoke = new BirFunctionDeclaration {
            Name = "invoke", Static = false, Override = false, Virtual = false,
            Params = Enumerable.Range(0, plan.Arity).Select(i => new BirParameter {
                Name = "p" + i, Type = plan.Parameters?[i] ?? new TypeNode.Tv("type", i),
            }).ToArray(),
            Ret = plan.Return, Body = body, BodyTerminates = true,
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
        document.Types = document.Types.OrEmpty().Append(declaration).ToArray();
        adapters[key] = name;
        return name;
    }

    static bool IsVoid(TypeNode type) => type is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };

    internal static void SelfTest() => ClrMemberResolution.TypedDelegateValueSelfTest(references =>
    {
        var sourceParameter = new TypeNode.Tv("method", 1);
        var source = new TypeNode.Fn(false, new TypeNode.Fqn("void"), new TypeNode[] { sourceParameter }, Clr: "System.Action");
        var target = new TypeNode.Fn(false, new TypeNode.Fqn("System.Object"),
            new TypeNode[] { new TypeNode.Fqn("System.Object") }, Clr: "System.Func");
        var root = new BirFileDeclaration { FileClass = "Probe.Owner", Types = Array.Empty<IBirTypeDeclaration>() };
        var original = new BirLocal { Name = "effectfulDelegate" };
        var adapter = new TypedPhysicalDelegateAdapter(references,
            () => new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("kotlin.Unit"), Name = "INSTANCE" });
        var first = (BirValueBlock)adapter.AdaptUnitDelegateValue(root, original, source, target);
        var variable = (BirVariable)first.Stmts.Value.Single();
        var conditional = (BirConditional)first.Result.Value;
        var construction = (BirNewClosure)conditional.Else.Value;
        var nullTest = (BirBinaryOperation)conditional.Cond.Value;
        if (!ReferenceEquals(variable.Init.Value, original)
            || construction.Captures.Value.Single() is not BirLocal captured
            || captured.Name.Value != variable.Name.Value || nullTest.Lhs.Value is not BirLocal tested || tested.Name.Value != variable.Name.Value
            || ReferenceEquals(captured, tested)
            || conditional.Then.Value is not BirConstant { Value.Value: BirLiteral.NullLiteral }
            || nullTest.Rhs.Value is not BirConstant { Value.Value: BirLiteral.NullLiteral })
            throw new InvalidOperationException("Typed Unit delegate adapter lost one evaluation or nullable identity");
        var declaration = (BirClassDefinition)root.Types.Value.Single();
        var invoke = declaration.Methods.Value.Single();
        var invocation = (BirDelegateInvoke)((BirExpressionStatement)invoke.Body.Value[0]).Expr.Value;
        if (declaration.TypeParams.Value.Length != 2
            || declaration.TypeParams.Value.Any(parameter => !parameter.SpecialConstraints.Value.SequenceEqual(new[] { "allowsRefStruct" }))
            || invocation.Args.Value[0] is not BirCast { Type.Value: TypeNode.Tv { Scope: "type", I: 0 } }
            || construction.TypeArgs.Value[0] != sourceParameter
            || construction.TypeArgs.Value[1] != new TypeNode.Fqn("System.Object")
            || construction.DelegateCtorRef.Value.Name.Value != ".ctor"
            || invocation.InvokeRef.Value.Name.Value != "Invoke"
            || invocation.InvokeRef.Value.DeclaringType.Value is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }] }
            || invoke.Body.Value[1] is not BirReturn { Value.Value: BirStaticFieldGet { Name.Value: "INSTANCE" } })
            throw new InvalidOperationException("Typed Unit delegate adapter lost its exact parameter representation or binding frame");
        _ = adapter.AdaptUnitDelegateValue(root, new BirLocal { Name = "other" }, source, target);
        if (root.Types.Value.Length != 1)
            throw new InvalidOperationException("Typed Unit delegate adapters did not share a shape within their document");
        var other = new BirFileDeclaration { FileClass = "Other.Owner", Types = Array.Empty<IBirTypeDeclaration>() };
        _ = adapter.AdaptUnitDelegateValue(other, new BirLocal { Name = "other" }, source, target);
        if (other.Types.Value.Length != 1 || ReferenceEquals(other.Types.Value[0], declaration))
            throw new InvalidOperationException("Typed Unit delegate adapter declarations leaked between documents");
        if (adapter.AdaptUnitDelegateValue(other, new BirLocal { Name = "unchanged" }, target, target) != null)
            throw new InvalidOperationException("Typed Unit delegate adapter changed a non-void function value");
        Console.WriteLine("[typed Unit delegate values] self-test OK (single evaluation, nulls, exact generic slot frames and document ownership)");
    });
}
