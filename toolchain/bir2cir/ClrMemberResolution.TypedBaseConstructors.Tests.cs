using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void SelfTestTypedBaseConstructors()
    {
        var priorReferences = _refs;
        try
        {
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var text = new TypeNode.Fqn("System.String");
            var variable = new TypeNode.Tv("type", 0);
            var callerVariable = new TypeNode.Tv("type", 1);
            BirConstant Null(TypeNode type) => new() { Type = type, Value = new BirLiteral.NullLiteral() };
            BirClassDefinition Derived(string name, TypeNode owner, BirConstructor constructor) => new() {
                Name = name, Kind = "class", Base = owner, Ctors = new[] { constructor },
                TypeParams = new[] { new BirTypeParameter { Name = "First" }, new BirTypeParameter { Name = "Second" } },
            };
            var previous = new BirMemberReference { Name = "previous" };
            var body = new IBirStatement[] { new BirExpressionStatement { Expr = Null(text) } };
            var arguments = new IBirExpression[] { Null(text) };
            var exception = new BirConstructor { BaseArgs = arguments, ThisArgs = OptionalField<IBirExpression[]>.Null,
                DelegationSig = new TypeNode[] { text }, BaseCtorRef = previous, Body = body };
            var list = new BirConstructor { BaseArgs = new IBirExpression[] { Null(new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { callerVariable })) },
                DelegationSig = new TypeNode[] { new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { variable }) } };
            var natural = new TypeNode.Fn(false, new TypeNode.Fqn("void"), []);
            var delegateConstructor = new BirMemberReference { Name = ".ctor", DeclaringType = new TypeNode.Fqn("System.Action") };
            var closure = new BirNewDelegate { FuncType = natural, DotktDelegateSlot = new TypeNode.Fqn("System.Threading.ThreadStart"),
                DelegateCtorRef = delegateConstructor };
            var task = new BirConstructor { BaseArgs = new IBirExpression[] { closure },
                DelegationSig = new TypeNode[] { new TypeNode.Fqn("System.Action") } };
            var genericClosure = new BirNewDelegate { FuncType = new TypeNode.Fn(false, callerVariable, []) };
            var genericTask = new BirConstructor { BaseArgs = new IBirExpression[] { genericClosure },
                DelegationSig = new TypeNode[] { new TypeNode.Fqn("System.Func", new TypeNode[] { variable }) } };
            var thisDelegation = new BirConstructor { ThisArgs = Array.Empty<IBirExpression>(),
                BaseArgs = arguments, DelegationSig = new TypeNode[] { text }, BaseCtorRef = previous,
                DelegationDeclarationId = "this-delegation" };
            var local = new BirConstructor { BaseArgs = Array.Empty<IBirExpression>(), BaseCtorRef = previous,
                DelegationDeclarationId = "local-delegation" };
            var absent = new BirConstructor { BaseArgs = OptionalField<IBirExpression[]>.Null, BaseCtorRef = previous };
            var definitions = new IBirTypeDeclaration[] {
                Derived("DerivedException", new TypeNode.Fqn("System.Exception"), exception),
                Derived("DerivedList", new TypeNode.Fqn("System.Collections.Generic.List", new TypeNode[] { callerVariable }), list),
                Derived("DerivedTask", new TypeNode.Fqn("System.Threading.Tasks.Task"), task),
                Derived("DerivedGenericTask", new TypeNode.Fqn("System.Threading.Tasks.Task", new TypeNode[] { callerVariable }), genericTask),
                Derived("ThisDelegation", new TypeNode.Fqn("System.Exception"), thisDelegation),
                Derived("LocalDelegation", new TypeNode.Fqn("LocalBase"), local),
                Derived("NoBaseArguments", new TypeNode.Fqn("System.Exception"), absent),
            };
            var file = new BirFileDeclaration { Types = definitions };
            ResolveBaseConstructors(file, refs, new HashSet<string>(StringComparer.Ordinal) { "LocalBase" });
            var reference = exception.BaseCtorRef.Value;
            Check(ReferenceEquals(file.Types.Value, definitions) && ReferenceEquals(definitions[0].Ctors.Value[0], exception)
                && ReferenceEquals(exception.BaseArgs.Value, arguments) && ReferenceEquals(exception.Body.Value, body)
                && !ReferenceEquals(reference, previous) && reference.Kind.Value == MemberRefNode.Kinds.Ctor
                && reference.Name.Value == ".ctor" && reference.CallingConvention.Value == MemberRefNode.Instance
                && reference.ParameterTypes.Value.Single() == text && !exception.DelegationDeclarationId.IsPresent,
                "base delegation must retain its owning declaration/arguments and replace the exact selected reference");
            var listReference = list.BaseCtorRef.Value;
            Check(((TypeNode.Fqn)listReference.DeclaringType.Value).Args.Single() == callerVariable
                && ((TypeNode.Fqn)listReference.ParameterTypes.Value.Single()).Args.Single() == variable,
                "selection and MemberRef parameters must remain open while the declaring owner uses the caller frame");
            Check(closure.DotktDelegateSlot.Value is TypeNode.Fqn { Name: "System.Action" }
                && ReferenceEquals(closure.FuncType.Value, natural) && ReferenceEquals(closure.DelegateCtorRef.Value, delegateConstructor)
                && genericClosure.DotktDelegateSlot.Value is TypeNode.Fqn { Name: "System.Func", Args.Length: 1 } slot
                && slot.Args[0] == callerVariable,
                "the exact delegate target must close the base frame and win without modifying natural shape or constructor reference");
            Check(ReferenceEquals(thisDelegation.BaseCtorRef.Value, previous) && thisDelegation.DelegationDeclarationId.Value == "this-delegation"
                && ReferenceEquals(local.BaseCtorRef.Value, previous) && local.DelegationDeclarationId.Value == "local-delegation"
                && ReferenceEquals(absent.BaseCtorRef.Value, previous),
                "this delegation, local bases and absent base arguments must stay on their existing paths");
            // A later resolution replaces only the selected operand, not its owning records.
            definitions[0].Base = new TypeNode.Fqn("System.InvalidOperationException");
            ResolveBaseConstructors(file, refs, new HashSet<string>(StringComparer.Ordinal) { "LocalBase" });
            Check(exception.BaseCtorRef.Value.ParameterTypes.Value.Single() == text
                && ((TypeNode.Fqn)exception.BaseCtorRef.Value.DeclaringType.Value).Name == "System.InvalidOperationException"
                && ReferenceEquals(exception.BaseArgs.Value, arguments),
                "last selected constructor must replace the previous reference without replacing arguments");
        }
        finally { _refs = priorReferences; }
        Console.WriteLine("[typed base constructors] self-test OK (exact open signatures, closed targets, continuing declarations, exclusions)");
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Typed base constructors: " + message); }
}
