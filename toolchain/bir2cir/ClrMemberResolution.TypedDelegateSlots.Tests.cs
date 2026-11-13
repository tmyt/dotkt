using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void SelfTestTypedDelegateSlots() => TypedDelegateValueSelfTest(refs =>
    {
        var priorTypes = _localTypes;
        var priorCarriers = _localCarriers;
        try
        {
            var voidType = new TypeNode.Fqn("void");
            var objectType = new TypeNode.Fqn("System.Object");
            var action = new TypeNode.Fn(false, voidType, Array.Empty<TypeNode>(), Clr: "System.Action");
            var func = new TypeNode.Fn(false, objectType, Array.Empty<TypeNode>(), Clr: "System.Func");
            var valueSlot = new TypeNode.Fqn("System.Func`1", new TypeNode[] { objectType });
            var voidSlot = new TypeNode.Fqn("System.Action");
            var nominalSlot = new TypeNode.Fqn("System.Threading.ThreadStart");
            var selected = new BirMemberReference { Name = "SelectedTarget" };

            BirGraphValue[] Marked(TypeNode natural, TypeNode slot) => new BirGraphValue[] {
                new BirNewDelegate { Method = "target", FuncType = natural, DotktDelegateSlot = slot, MemberRef = selected },
                new BirNewClosure { Method = "invoke", ClosureType = new TypeNode.Fqn("Closure"), FuncType = natural, DotktDelegateSlot = slot },
                new BirNewBoundDelegate { Method = "target", OwnerType = objectType, Recv = new BirThis(),
                    FuncType = natural, DotktDelegateSlot = slot, MemberRef = selected, InvokeRef = selected },
                new BirBoundClrDelegate { Method = "target", ClrType = objectType, Recv = new BirThis(),
                    FuncType = natural, DotktDelegateSlot = slot, MemberRef = selected },
                new BirClrStaticDelegate { Method = "target", ClrType = objectType,
                    FuncType = natural, DotktDelegateSlot = slot, MemberRef = selected },
            };
            BirFileDeclaration File(params BirGraphValue[] expressions) => new() {
                FileClass = "Construction.Owner",
                Types = Array.Empty<IBirTypeDeclaration>(),
                Methods = new[] { new BirFunctionDeclaration {
                    Name = "test", Ret = voidType,
                    Body = expressions.Select(e => (IBirStatement)new BirExpressionStatement {
                        Expr = OptionalField<IBirExpression>.Of((IBirExpression)e),
                    }).ToArray(),
                } },
            };
            static BirGraphValue Expression(BirFileDeclaration file, int index)
                => (BirGraphValue)((BirExpressionStatement)file.Methods.Value[0].Body.Value[index]).Expr.Value;
            void Apply(BirFileDeclaration file)
                => MaterializeDelegateSlots(file, refs, new HashSet<string>());
            static void Require(bool value, string message)
            {
                if (!value) throw new InvalidOperationException("Typed delegate slots: " + message);
            }

            var retargeted = Marked(action, nominalSlot);
            var root = File(retargeted);
            var method = root.Methods.Value[0];
            Apply(root);
            Require(ReferenceEquals(method, root.Methods.Value[0]) && root.Types.Value.Length == 0,
                "retargeting replaced a declaration or added an unnecessary adapter");
            foreach (var construction in retargeted)
            {
                var view = Construction(construction);
                Require(!view.Slot.IsPresent && SameDelegate(view.Natural, nominalSlot), "one of five marked shapes was not retargeted");
            }
            Require(((BirNewDelegate)retargeted[0]).DelegateCtorRef.Value.DeclaringType.Value.Equals(nominalSlot)
                && ((BirNewBoundDelegate)retargeted[2]).InvokeRef.Value == selected
                && ((BirClrStaticDelegate)retargeted[4]).MemberRef.Value == selected,
                "retargeting lost exact constructor or selected callable bindings");

            var adapted = Marked(action, valueSlot);
            root = File(adapted);
            Apply(root);
            Require(root.Types.Value.Length == 1, "equivalent construction adapters were not cached per file");
            for (var i = 0; i < adapted.Length; i++)
            {
                var wrapper = (BirNewClosure)Expression(root, i);
                Require(ReferenceEquals(wrapper.Captures.Value.Single(), adapted[i])
                    && !Construction(adapted[i]).Slot.IsPresent
                    && wrapper.TypeArgs.HasValue && wrapper.TypeArgs.Value.Length == 0
                    && wrapper.DelegateCtorRef.Value.Name.Value == ".ctor",
                    "Unit construction lost its natural callable, marker removal, empty arguments, or exact constructor");
            }
            var firstAdapter = root.Types.Value.Single().Name.Value;
            var otherFile = File(Marked(action, valueSlot)[0]);
            Apply(otherFile);
            Require(otherFile.Types.Value.Single().Name.Value != firstAdapter,
                "construction adapter cache leaked across documents or global names collided");

            var nested = (BirNewClosure)Marked(action, valueSlot)[1];
            var nestedChild = Marked(action, valueSlot)[0];
            nested.Captures = new[] { (IBirExpression)nestedChild };
            root = File(nested);
            Apply(root);
            Require(root.Types.Value.Length == 1
                && ReferenceEquals(((BirNewClosure)Expression(root, 0)).Captures.Value.Single(), nested)
                && nested.Captures.Value[0] is BirNewClosure inner
                && ReferenceEquals(inner.Captures.Value.Single(), nestedChild),
                "nested constructions were not materialized children first");

            var namedNatural = new BirNewDelegate { FuncType = valueSlot, DotktDelegateSlot = voidSlot };
            var nullMark = new BirNewDelegate { FuncType = action, DotktDelegateSlot = OptionalField<TypeNode>.Null };
            var noMark = new BirNewDelegate { FuncType = action, DelegateCtorRef = OptionalField<BirMemberReference>.Null };
            root = File(namedNatural, nullMark, noMark);
            Apply(root);
            Require(!namedNatural.DotktDelegateSlot.IsPresent && namedNatural.FuncType.Value == valueSlot
                && !nullMark.DotktDelegateSlot.IsPresent && noMark.DelegateCtorRef.IsNull
                && root.Types.Value.Length == 0,
                "marker presence or already-named delegate semantics changed");

            var samValue = new BirLocal { Name = "source" };
            var samVoid = new BirLocal { Name = "voidSource" };
            root = File(new BirSamConversion { Type = valueSlot, FuncType = func, E = samValue },
                new BirSamConversion { Type = voidSlot, FuncType = action, E = samVoid });
            Apply(root);
            var samAdapter = (BirNewClosure)Expression(root, 0);
            Require(root.Types.Value.Length == 2 && samAdapter.TypeArgs.HasValue
                && samAdapter.Captures.Value.Single() is BirCast cast && cast.Type.Value == func
                && ReferenceEquals(cast.E.Value, samValue), "explicit SAM conversion did not retain its function-view boundary");
            var voidInvoke = ((BirClassDefinition)root.Types.Value[1]).Methods.Value.Single();
            Require(voidInvoke.Body.Value is [BirExpressionStatement, BirReturn { Value.IsPresent: false }],
                "void-to-void SAM adapter returned a Unit value");
            Console.WriteLine("[typed delegate construction] self-test OK (five shapes, SAM, exact bindings, bottom-up ownership and cache scope)");
        }
        finally { _localTypes = priorTypes; _localCarriers = priorCarriers; }
    });
}
