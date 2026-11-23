using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The final value-flow owner reuses the construction owner's representation
// plan and exact CLR bindings. Only declaration and expression construction is
// different: every occurrence below is an owning typed value, never a DOM view.
sealed class TypedPhysicalDelegateAdapter
{
    readonly ReferenceMetadataIndex references;
    readonly IReadOnlySet<string> localTypes;
    readonly Func<IBirExpression> unitValue;
    readonly PhysicalDelegateDeclarations declarations;
    readonly Dictionary<string, string> adapters = new(StringComparer.Ordinal);
    BirFileDeclaration document;
    string scope;

    internal TypedPhysicalDelegateAdapter(IReadOnlySet<string> localTypes, ReferenceMetadataIndex references, Func<IBirExpression> unitValue,
        PhysicalDelegateDeclarations declarations)
    {
        this.references = references;
        this.localTypes = localTypes;
        this.unitValue = unitValue;
        this.declarations = declarations;
    }

    internal IBirExpression AdaptUnitDelegateValue(IReadOnlyDictionary<string, string> localCarriers,
        BirFileDeclaration document, IBirExpression expression,
        TypeNode source, TypeNode target)
    {
        var plan = ClrMemberResolution.UnitDelegateValuePlan(references, localCarriers, source, target, out var slot);
        if (plan == null) return null;
        return AdaptValue(document, expression, source, target, slot, plan, null);
    }

    internal IBirExpression AdaptFunctionView(BirFileDeclaration document, IBirExpression view, TypeNode.Fn selected,
        TypeNode.Fqn slot, TypeNode.Fn knownView = null)
    {
        var plan = ClrMemberResolution.FunctionViewPlan(references, localTypes, selected, slot, knownView, out var natural);
        if (plan == null) return new BirCast { Type = slot, E = OptionalField<IBirExpression>.Of(view) };
        return AdaptValue(document, view, new TypeNode.Fqn("System.Object"), slot, slot, plan, natural);
    }

    IBirExpression AdaptValue(BirFileDeclaration document, IBirExpression expression, TypeNode source,
        TypeNode target, TypeNode.Fqn slot, ClrMemberResolution.DelegateAdapterPlan plan, TypeNode.Fn view)
    {
        var temporary = declarations.FreshValueTemporary();
        BirLocal Read() => new BirLocal { Name = temporary };
        var adapter = AdapterClass(document, plan);
        var adapted = new BirNewClosure {
            ClosureType = new TypeNode.Fqn(adapter), Method = "invoke",
            Captures = new IBirExpression[] { view == null ? Read() : new BirCast { Type = view, E = Read() } }, FuncType = slot,
            DelegateCtorRef = BirMemberReference.FromPhysical(
                ClrMemberResolution.DelegateConstructorReference(localTypes, references, slot)),
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
                    Op = "==", Lhs = Read(), Rhs = Null(),
                },
                Then = Null(), Else = view == null ? adapted : OptionalField<IBirExpression>.Of(new BirConditional {
                    Type = target, Cond = new BirIsInstance { Type = slot, E = Read() },
                    Then = new BirCast { Type = slot, E = Read() }, Else = adapted }),
            },
        };
    }

    static BirConstant Null() => new() {
        Type = new TypeNode.Fqn("System.Object"), Value = new BirLiteral.NullLiteral(),
    };

    // Construction and value-flow owners use separate instances, retaining their
    // per-file caches while sharing this exact declaration builder and allocator.
    internal string AdapterClass(BirFileDeclaration document, ClrMemberResolution.DelegateAdapterPlan plan)
    {
        if (!ReferenceEquals(this.document, document))
        {
            this.document = document;
            adapters.Clear();
            scope = string.Concat((document.FileClass.OrDefault() ?? "File")
                .Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        }
        var key = ClrMemberResolution.DelegateAdapterKey(plan);
        if (adapters.TryGetValue(key, out var existing)) return existing;
        var declaration = declarations.CreateAdapter(localTypes, scope, plan, references, unitValue);
        var name = declaration.Name.Value;
        document.Types = document.Types.OrEmpty().Append(declaration).ToArray();
        adapters[key] = name;
        return name;
    }

    internal static void SelfTest() => ClrMemberResolution.TypedDelegateValueSelfTest(references =>
    {
        var sourceParameter = new TypeNode.Tv("method", 1);
        var source = new TypeNode.Fn(false, new TypeNode.Fqn("void"), new TypeNode[] { sourceParameter }, Clr: "System.Action");
        var target = new TypeNode.Fn(false, new TypeNode.Fqn("System.Object"),
            new TypeNode[] { new TypeNode.Fqn("System.Object") }, Clr: "System.Func");
        var root = new BirFileDeclaration { FileClass = "Probe.Owner", Types = Array.Empty<IBirTypeDeclaration>() };
        var original = new BirLocal { Name = "effectfulDelegate" };
        var adapter = new TypedPhysicalDelegateAdapter(null, references,
            () => new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("kotlin.Unit"), Name = "INSTANCE" },
            new PhysicalDelegateDeclarations());
        var first = (BirValueBlock)adapter.AdaptUnitDelegateValue(null, root, original, source, target);
        var variable = (BirVariable)first.Stmts.Value.Single();
        var conditional = (BirConditional)first.Result.Value;
        var construction = (BirNewClosure)conditional.Else.Value;
        var nullTest = (BirBinaryOperation)conditional.Cond.Value;
        if (!ReferenceEquals(variable.Init.Value, original)
            || construction.Captures.Value.Single() is not BirLocal captured
            || captured.Name.Value != variable.Name.Value || nullTest.Lhs.Value is not BirLocal tested || tested.Name.Value != variable.Name.Value
            || ReferenceEquals(captured, tested)
            || conditional.Then.Value is not BirConstant { Value.Value: BirLiteral.NullLiteral }
            || nullTest.Op.OrDefault() != "==" || nullTest.Rhs.Value is not BirConstant { Value.Value: BirLiteral.NullLiteral })
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
        _ = adapter.AdaptUnitDelegateValue(null, root, new BirLocal { Name = "other" }, source, target);
        if (root.Types.Value.Length != 1)
            throw new InvalidOperationException("Typed Unit delegate adapters did not share a shape within their document");
        var other = new BirFileDeclaration { FileClass = "Other.Owner", Types = Array.Empty<IBirTypeDeclaration>() };
        _ = adapter.AdaptUnitDelegateValue(null, other, new BirLocal { Name = "other" }, source, target);
        if (other.Types.Value.Length != 1 || ReferenceEquals(other.Types.Value[0], declaration))
            throw new InvalidOperationException("Typed Unit delegate adapter declarations leaked between documents");
        if (adapter.AdaptUnitDelegateValue(null, other, new BirLocal { Name = "unchanged" }, target, target) != null)
            throw new InvalidOperationException("Typed Unit delegate adapter changed a non-void function value");
        Console.WriteLine("[typed Unit delegate values] self-test OK (single evaluation, nulls, exact generic slot frames and document ownership)");
    });
}
