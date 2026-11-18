using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void SourceLocalConstructorsSelfTest()
    {
        static void Need(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Source local constructors: " + reason); }
        static BirConstructor Constructor(string id, params TypeNode[] parameters) => new() {
            DeclarationId = id,
            Params = parameters.Select((type, index) => new BirParameter { Name = "p" + index, Type = type }).ToArray(),
        };
        static BirExpressionStatement Evaluate(IBirExpression expression) => new() {
            Expr = OptionalField<IBirExpression>.Of(expression),
        };
        static BirNew Construct(TypeNode type, TypeNode[] signature, params IBirExpression[] arguments) => new() {
            Type = type, ArgTypes = signature, Args = arguments,
        };
        var previousTypes = _localTypes;
        try
        {
            var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            _localTypes = new HashSet<string>();
            var integer = new TypeNode.Fqn("System.Int32"); var text = new TypeNode.Fqn("System.String");
            var erased = new TypeNode.Fqn("System.Object"); var unit = new TypeNode.Fqn("void");
            var ownerT = new TypeNode.Tv("type", 0); var ownerU = new TypeNode.Tv("type", 1);
            var intCtor = Constructor("box-int", integer); var genericCtor = Constructor("box-generic", ownerT);
            var box = new BirClassDefinition { Kind = "class", Name = "Box",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ctors = new[] { intCtor, genericCtor } };
            var parameterVector = genericCtor.Params.Value;
            var selected = Construct(new TypeNode.Fqn("Box", new TypeNode[] { integer }), new TypeNode[] { integer },
                new BirLocal { Name = "selected", Sty = integer });
            selected.MemberSignature = new TypeNode[] { ownerT };
            var byIdentity = Construct(new TypeNode.Fqn("Box", new TypeNode[] { integer }), new TypeNode[] { integer },
                new BirLocal { Name = "identified", Sty = integer });
            byIdentity.MemberSignature = new TypeNode[] { integer }; byIdentity.ConstructorDeclarationId = "box-generic";
            var useSite = Construct(new TypeNode.Fqn("Box", new TypeNode[] { text }), new TypeNode[] { text },
                new BirLocal { Name = "text", Sty = text });

            // Both physical parameter vectors are Object. The selected open
            // function signature, not the storage carrier, distinguishes them.
            var intFunction = new TypeNode.Fn(false, integer, new TypeNode[] { integer });
            var textFunction = new TypeNode.Fn(false, text, new TypeNode[] { text });
            var intFunctionCtor = Constructor("function-int", erased);
            var textFunctionCtor = Constructor("function-text", erased);
            intFunctionCtor.Params.Value[0].FunctionSignatureType = intFunction;
            textFunctionCtor.Params.Value[0].FunctionSignatureType = textFunction;
            var functions = new BirClassDefinition { Kind = "class", Name = "FunctionBox",
                Ctors = new[] { intFunctionCtor, textFunctionCtor } };
            var functionValue = new BirLocal { Name = "function", Sty = erased };
            var byFunction = Construct(new TypeNode.Fqn("FunctionBox"), new TypeNode[] { erased }, functionValue);
            byFunction.MemberSignature = new TypeNode[] { textFunction };

            var baseCtor = Constructor("base-generic", ownerT);
            var baseType = new BirClassDefinition { Kind = "class", Name = "Base",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ctors = new[] { baseCtor } };
            var baseArgument = new BirLocal { Name = "baseArgument", Sty = ownerU };
            var baseArguments = new IBirExpression[] { baseArgument };
            var delegated = Constructor("derived-zero");
            delegated.ThisArgs = OptionalField<IBirExpression[]>.Null;
            delegated.BaseArgs = baseArguments; delegated.DelegationSig = new TypeNode[] { ownerT };
            var throughThis = Constructor("derived-one", integer);
            throughThis.ThisArgs = Array.Empty<IBirExpression>();
            throughThis.BaseArgs = new IBirExpression[] { new BirLocal { Name = "unusedBase", Sty = ownerU } };
            throughThis.DelegationSig = Array.Empty<TypeNode>();
            throughThis.DelegationDeclarationId = "derived-zero";
            var derived = new BirClassDefinition { Kind = "class", Name = "Derived",
                TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" } },
                Base = new TypeNode.Fqn("Base", new TypeNode[] { ownerU }), Ctors = new[] { delegated, throughThis } };

            var mutable = new TypeNode.Fqn(CollectionViewFaces.IList, new TypeNode[] { text });
            var readOnly = new TypeNode.Fqn(CollectionViewFaces.IReadOnlyList, new TypeNode[] { text });
            var listCtor = Constructor("list-view", mutable);
            var listType = new BirClassDefinition { Kind = "class", Name = "ListBox", Ctors = new[] { listCtor } };
            var evaluatedOnce = new BirCallStatic { Owner = new TypeNode.Fqn("Fixture"), Method = "ProduceView",
                Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = readOnly };
            var view = Construct(new TypeNode.Fqn("ListBox"), new TypeNode[] { readOnly }, evaluatedOnce);
            view.MemberSignature = new TypeNode[] { mutable };

            var action = new TypeNode.Fqn("System.Action");
            var callbackCtor = Constructor("callback", action);
            var callbacks = new BirClassDefinition { Kind = "class", Name = "CallbackBox", Ctors = new[] { callbackCtor } };
            var callback = new BirNewDelegate { CalleeOwner = new TypeNode.Fqn("Fixture"), Method = "Run",
                Sig = Array.Empty<TypeNode>(), FuncType = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()) };
            var callbackConstruction = Construct(new TypeNode.Fqn("CallbackBox"), new TypeNode[] { action }, callback);

            var leafCtor = Constructor("leaf");
            var leaf = new BirClassDefinition { Kind = "class", Name = "Leaf", Ctors = new[] { leafCtor } };
            var parentCtor = Constructor("parent", new TypeNode.Fqn("Leaf"));
            var parent = new BirClassDefinition { Kind = "class", Name = "Parent", Ctors = new[] { parentCtor } };
            var inner = Construct(new TypeNode.Fqn("Leaf"), Array.Empty<TypeNode>());
            var outer = Construct(new TypeNode.Fqn("Parent"), new TypeNode[] { new TypeNode.Fqn("Leaf") }, inner);
            var body = new IBirStatement[] { Evaluate(selected), Evaluate(byIdentity), Evaluate(useSite), Evaluate(byFunction),
                Evaluate(view), Evaluate(callbackConstruction), Evaluate(outer) };
            var method = new BirFunctionDeclaration { Name = "Use", Params = Array.Empty<BirParameter>(), Ret = unit, Body = body };
            var first = new BirFileDeclaration { FileClass = "Fixture", Methods = new[] { method },
                Types = new IBirTypeDeclaration[] { box, functions, baseType, derived, listType, callbacks, leaf, parent } };
            // Genuine shared declaration copies have identical ordered ctor
            // vectors. Pin which actual record supplies the first-wins index.
            var second = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { (BirClassDefinition)box.DeepClone() } };
            var roots = new[] { first, second };
            Need(ReferenceEquals(LocalConstructorDefinitions(roots)["Box"], box), "shared type index is not first-wins");
            var types = first.Types.Value; var constructors = box.Ctors.Value; var methods = first.Methods.Value;
            ResolveLocalConstructors(references, roots);
            Need(selected.LocalCtorIndex.Value == 1 && byIdentity.LocalCtorIndex.Value == 1 && useSite.LocalCtorIndex.Value == 1
                && selected.ArgTypes.Value.Single() == integer && useSite.ArgTypes.Value.Single() == text
                && !selected.MemberSignature.IsPresent && !byIdentity.ConstructorDeclarationId.IsPresent,
                "selected declaration ID/open signature/use-site owner frame or original ordinal changed");
            Need(byFunction.LocalCtorIndex.Value == 1 && byFunction.ArgTypes.Value.Single() == erased
                && ReferenceEquals(byFunction.Args.Value[0], functionValue)
                && textFunctionCtor.Params.Value[0].FunctionSignatureType.Value == textFunction,
                "function signature identity was replaced by its physical carrier");
            Need(delegated.LocalCtorIndex.Value == 0 && delegated.DelegationSig.Value.Single() == ownerU
                && delegated.ThisArgs.IsNull && ReferenceEquals(delegated.BaseArgs.Value, baseArguments)
                && ReferenceEquals(delegated.BaseArgs.Value[0], baseArgument) && !delegated.DelegationDeclarationId.IsPresent,
                "base delegation confused target declaration TVs with derived-owner arguments or cloned its operands");
            Need(throughThis.LocalCtorIndex.Value == 0 && throughThis.ThisArgs.Value.Length == 0
                && throughThis.DelegationSig.Value.Length == 0 && !throughThis.DelegationDeclarationId.IsPresent,
                "present empty thisArgs did not take precedence over baseArgs");
            Need(view.Args.Value[0] is BirCast cast && cast.Type.Value == mutable && ReferenceEquals(cast.E.Value, evaluatedOnce)
                && view.ArgTypes.Value.Single() == mutable && ReferenceEquals(outer.Args.Value[0], inner)
                && inner.LocalCtorIndex.Value == 0 && outer.LocalCtorIndex.Value == 0,
                "postorder construction binding or actual view operand MOVE changed");
            Need(callback.DotktDelegateSlot.Value == action && ReferenceEquals(callbackConstruction.Args.Value[0], callback),
                "selected constructor's physical delegate parameter was not stamped on the actual argument");
            Need(ReferenceEquals(first.Types.Value, types) && ReferenceEquals(box.Ctors.Value, constructors)
                && ReferenceEquals(first.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body)
                && ReferenceEquals(genericCtor.Params.Value, parameterVector) && genericCtor.DeclarationId.Value == "box-generic"
                && genericCtor.Params.Value[0].Type.Value == ownerT,
                "constructor binding replaced declarations, parameter vectors or their semantic identities");

            // The common planner must not break ties by declaration order, and
            // a declaration-frame delegation must not accidentally close TVs.
            var ambiguous = new[] {
                new LocalConstructorCandidate(0, "a", new TypeNode[] { erased }, new TypeNode[] { erased }),
                new LocalConstructorCandidate(1, "b", new TypeNode[] { erased }, new TypeNode[] { erased }),
            };
            Need(SelectLocalConstructors(ambiguous, 1, new TypeNode[] { erased }, null, [], false, true).Length == 2
                && SelectLocalConstructors(ambiguous, 1, new TypeNode[] { erased }, "b", [], false, true).Single().Index == 1,
                "exact selection silently chose an ambiguous ordinal or ignored selected identity");
            var openCandidate = new[] { new LocalConstructorCandidate(0, "open", new TypeNode[] { ownerT }, new TypeNode[] { ownerT }) };
            Need(SelectLocalConstructors(openCandidate, 1, new TypeNode[] { ownerT }, null, new TypeNode[] { ownerU }, true, false).Length == 1
                && SelectLocalConstructors(openCandidate, 1, new TypeNode[] { ownerU }, null, new TypeNode[] { ownerU }, true, false).Length == 0,
                "declaration-frame matching was incorrectly owner-substituted");
            Console.WriteLine("[source local constructors] exact IDs, ordinals, frames, presence, owned views and delegate slots: OK");
        }
        finally { _localTypes = previousTypes; }
    }
}
