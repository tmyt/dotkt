using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void SourceLocalDelegateTargetsSelfTest()
    {
        static void Need(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Source local delegate targets: " + reason); }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var previousReferences = _refs;
        try
        {
            var integer = new TypeNode.Fqn("System.Int32"); var text = new TypeNode.Fqn("System.String");
            var erased = new TypeNode.Fqn("System.Object"); var unit = new TypeNode.Fqn("void");
            var ownerVariable = new TypeNode.Tv("type", 0); var methodVariable = new TypeNode.Tv("method", 0);
            BirFunctionDeclaration Method(string name, string identity, TypeNode result, params TypeNode[] parameters) => new() {
                Name = name, DeclarationId = identity, Ret = result,
                Params = parameters.Select((type, index) => new BirParameter { Name = "p" + index, Type = type }).ToArray(),
            };
            BirExpressionStatement Evaluate(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
            var generic = Method("physicalName", "selected-id", text, ownerVariable, methodVariable);
            generic.TypeParams = new[] { new BirTypeParameter { Name = "M" } };
            var owner = new BirClassDefinition { Kind = "class", Name = "Owner", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Methods = new[] { generic, Method("bound", "bound-id", text, ownerVariable) } };
            var receiver = new BirLocal { Name = "receiver", Sty = new TypeNode.Fqn("Owner", new TypeNode[] { text }) };
            var closedSignature = new TypeNode[] { text, integer };
            var ordinary = new BirCallInstance { OwnerType = new TypeNode.Fqn("Owner", new TypeNode[] { text }), Method = "physicalName",
                TypeArgs = new TypeNode[] { integer }, Sig = closedSignature, Ret = text,
                Recv = OptionalField<IBirExpression>.Of(receiver), Args = new IBirExpression[] {
                    new BirLocal { Name = "first", Sty = text }, new BirLocal { Name = "second", Sty = integer } } };
            var bound = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn("Owner", new TypeNode[] { text }), Method = "old-name",
                DeclarationId = "bound-id", Sig = new TypeNode[] { text }, Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = "boundReceiver" }),
                FuncType = new TypeNode.Fn(false, text, new TypeNode[] { text }) };
            var lift = Method("lift", "lift-id", methodVariable, methodVariable);
            lift.TypeParams = new[] { new BirTypeParameter { Name = "M" } };
            var direct = new BirNewDelegate { CalleeOwner = new TypeNode.Fqn("Functions"), Method = "lift",
                TypeArgs = new TypeNode[] { text }, Sig = new TypeNode[] { text }, FuncType = new TypeNode.Fn(false, text, new TypeNode[] { text }) };
            var intFunction = new TypeNode.Fn(false, integer, new TypeNode[] { integer });
            var textFunction = new TypeNode.Fn(false, text, new TypeNode[] { text });
            var intOverload = Method("choose", "int-overload", unit, erased);
            intOverload.Params.Value[0].FunctionSignatureType = intFunction;
            var textOverload = Method("choose", "text-overload", unit, erased);
            textOverload.Params.Value[0].FunctionSignatureType = textFunction;
            var discriminator = new TypeNode[] { intFunction };
            var selected = new BirCallStatic { Owner = new TypeNode.Fqn("Functions"), Method = "choose", Sig = new TypeNode[] { erased },
                FunctionCallSignature = discriminator, Args = new IBirExpression[] { new BirLocal { Name = "function", Sty = erased } } };
            var ambiguous = new BirCallStatic { Owner = new TypeNode.Fqn("Functions"), Method = "choose", Sig = new TypeNode[] { erased },
                Args = new IBirExpression[] { new BirLocal { Name = "unselected", Sty = erased } } };
            var byIdentity = new BirCallStatic { Owner = new TypeNode.Fqn("Functions"), Method = "old-selected-name", DeclarationId = "text-overload",
                Sig = new TypeNode[] { integer }, Args = new IBirExpression[] { new BirLocal { Name = "selected", Sty = erased } } };
            var action = new TypeNode.Fqn("System.Action");
            var callback = Method("callback", "callback-id", unit);
            var actualDelegate = new BirNewDelegate { CalleeOwner = new TypeNode.Fqn("Functions"), Method = "callback",
                Sig = Array.Empty<TypeNode>(), FuncType = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()),
                DotktDelegateSlot = new TypeNode.Fqn("System.Func`1", new TypeNode[] { text }) };
            var accept = Method("accept", "accept-id", unit, action);
            var caller = new BirCallStatic { CalleeOwner = new TypeNode.Fqn("Functions"), Owner = new TypeNode.Fqn("WrongOwner"),
                Method = "accept", Sig = new TypeNode[] { action }, Args = new IBirExpression[] { actualDelegate } };
            var closure = new BirNewClosure { ClosureType = new TypeNode.Fqn("Closure"), Method = "invoke",
                FuncType = new TypeNode.Fn(false, unit, new TypeNode[] { erased }), Captures = Array.Empty<IBirExpression>() };
            var closureMethod = Method("invoke", "invoke-id", unit, erased); closureMethod.Params.Value[0].FunctionSignatureType = intFunction;
            var ifaceMethod = Method("apply", "iface-id", unit, action);
            var iface = new BirInterfaceDefinition { Name = "Interface", Kind = "interface", Methods = new[] { ifaceMethod } };
            var constrainedDelegate = new BirNewDelegate { CalleeOwner = new TypeNode.Fqn("Functions"), Method = "callback",
                Sig = Array.Empty<TypeNode>(), FuncType = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()) };
            var constrainedSignature = new TypeNode[] { action };
            var constrained = new BirConstrainedCall { Iface = new TypeNode.Fqn("Interface"), RecvType = new TypeNode.Tv("method", 0),
                Method = "apply", Sig = constrainedSignature, Args = new IBirExpression[] { constrainedDelegate } };
            var body = new IBirStatement[] { Evaluate(ordinary), Evaluate(bound), Evaluate(direct), Evaluate(selected), Evaluate(ambiguous),
                Evaluate(byIdentity), Evaluate(caller), Evaluate(closure), Evaluate(constrained) };
            var use = Method("use", "use-id", unit); use.Body = body;
            use.TypeParams = new[] { new BirTypeParameter { Name = "Receiver", Constraints = new TypeNode[] { new TypeNode.Fqn("Interface") } } };
            var saved = new byte[] { 1, 3, 2 }; use.InlineBir = saved;
            var first = new BirFileDeclaration { FileClass = "Functions", Methods = new[] { lift, callback, accept, intOverload, use },
                Types = new IBirTypeDeclaration[] { owner, iface, new BirClassDefinition { Kind = "class", Name = "Closure", Methods = new[] { closureMethod } } } };
            // Facade membership merges; do not drop either same-physical-owner declaration vector.
            var second = new BirFileDeclaration { FileClass = "Functions", Methods = new[] { textOverload } };
            var sourceMethods = first.Methods.Value; var sourceOwnerMethods = owner.Methods.Value;
            ResolveLocalDelegateTargetsSource(new[] { first, second }, references);
            Need(ReferenceEquals(ordinary.Sig.Value, closedSignature) && ReferenceEquals(ordinary.Recv.Value, receiver)
                && direct.Sig.Value.SequenceEqual(new TypeNode[] { methodVariable })
                && bound.Sig.Value.SequenceEqual(new TypeNode[] { ownerVariable }) && bound.DeclarationId.Value == "bound-id",
                "open delegate declarations were confused with closed caller signatures/ID-selected current names");
            Need(selected.CalleeParams.Value.Single() == FunctionSignatureIdentity.SignatureType(intOverload.Params.Value[0])
                && byIdentity.CalleeParams.Value.Single() == FunctionSignatureIdentity.SignatureType(textOverload.Params.Value[0])
                && !ambiguous.CalleeParams.IsPresent && ReferenceEquals(selected.FunctionCallSignature.Value, discriminator)
                && selected.Sig.Value.Single() == erased && byIdentity.Sig.Value.Single() == integer,
                "function discriminator, facade merge, exact-ID priority or ambiguity changed");
            Need(ReferenceEquals(caller.Args.Value[0], actualDelegate) && actualDelegate.DotktDelegateSlot.Value == action
                && constrainedDelegate.DotktDelegateSlot.Value == action && ReferenceEquals(constrained.Sig.Value, constrainedSignature)
                && !closure.CalleeParams.IsPresent && closure.FuncType.Value == new TypeNode.Fn(false, unit, new TypeNode[] { erased }),
                "selected slot last-writer, constrained owner or initial-vs-physical closure clock changed");
            Need(ReferenceEquals(first.Methods.Value, sourceMethods) && ReferenceEquals(owner.Methods.Value, sourceOwnerMethods)
                && ReferenceEquals(use.Body.Value, body) && ReferenceEquals(use.InlineBir.Value, saved),
                "target binding cloned declarations/operands or interpreted opaque metadata");
            var once = first.DeepClone(); ResolveLocalDelegateTargetsSource(new[] { first, second }, references);
            Need(first.StructurallyEquals(once), "initial local target binding is not stable on an already-open delegate signature");
            Console.WriteLine("[source local delegate targets] IDs, exact frames, function identity, slots and clock: OK");
        }
        finally { _refs = previousReferences; }
    }
}
