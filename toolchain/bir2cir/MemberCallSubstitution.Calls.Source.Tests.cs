using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MemberCallSubstitution
{
    internal static void SourceCallsSelfTest()
    {
        var refs = ReferenceMetadataIndex.SourceCallsTestIndex();
        var oldAttribute = _attributeTopLevelOwner;
        var oldValue = _isValue;
        var oldProperties = _localPropertyAccessors;
        var oldOwners = _localPropertyOwners;
        var oldFactories = _localFactories;
        try
        {
            _attributeTopLevelOwner = true;
            _isValue = _ => false;
            _localFactories = new Dictionary<string, LocalFactory>();
            _localPropertyAccessors = new Dictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>>();
            _localPropertyOwners = new HashSet<string>(StringComparer.Ordinal);
            var context = new CollectionHelperBinding.CollectionSourceContext();
            var integer = new TypeNode.Fqn("kotlin.Int");
            var text = new TypeNode.Fqn("kotlin.String");
            var boolean = new TypeNode.Fqn("kotlin.Boolean");
            var real = new TypeNode.Fqn("kotlin.Double");
            BirCallStatic Effect(string name, TypeNode result) => new() { Method = name, Ret = result, Sty = result };
            void Require(bool condition, string message)
            { if (!condition) throw new InvalidOperationException("Typed member calls: " + message); }

            var parseArguments = new IBirExpression[] { Effect("TextOnce", text), new BirLocal { Name = "result", Sty = integer } };
            var selected = new BirCallStatic { Method = "sameName", DeclarationId = "selected-parse", DeclarationReferencedIntrinsic = true,
                Sig = new TypeNode[] { text, integer }, Args = parseArguments, Ret = boolean };
            var parsed = TransformCall(selected, refs, context) as BirNativeStatic;
            Require(parsed != null && parsed.Method.Value == "TryParse" && parsed.Type.Value is TypeNode.Fqn { Name: "System.Int32" }
                && parsed.ArgTypes.Value[1] is TypeNode.ByRef { Of: TypeNode.Fqn { Name: "kotlin.Int" } }
                && parsed.Args.Value[0] is BirCallStatic { Method.Value: "TextOnce" }
                && !ReferenceEquals(parsed.Args.Value[0], parseArguments[0])
                && selected.Sig.Value[1] == integer && ReferenceEquals(selected.Args.Value, parseArguments),
                "the selected declaration must own its intrinsic/byref vector without replacing source operands");

            var receiver = Effect("CompanionOnce", new TypeNode.Fqn("Probe.Companion"));
            var property = new BirCallInstance { OwnerType = new TypeNode.Fqn("Probe.Companion"), Recv = receiver,
                Method = "pi", DeclarationId = "selected-pi", Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = real };
            var propertyResult = TransformCall(property, refs, context);
            Require(propertyResult is BirValueBlock block
                && block.Stmts.Value.Single() is BirExpressionStatement { Expr.Value: BirCallStatic { Method.Value: "CompanionOnce" } }
                && block.Result.Value is BirPropertyGet { Name.Value: "PI", Static.Value: true, Ret.Value: TypeNode.Fqn { Name: "kotlin.Double" } },
                "mapping a companion must preserve its unread receiver before the native property read");

            var slice = new BirCallInstance { OwnerType = new TypeNode.Fqn("Probe.String"), Recv = Effect("ReceiverOnce", text),
                Method = "slice", DeclarationId = "selected-slice", Sig = new TypeNode[] { integer, integer }, Ret = text,
                Args = new IBirExpression[] { Effect("StartOnce", integer), Effect("EndOnce", integer) } };
            var adapted = TransformCall(slice, refs, context);
            var effects = new Dictionary<string, int>(StringComparer.Ordinal);
            void Count(BirGraphValue node)
            {
                if (node is BirCallStatic { Method.HasValue: true } call)
                    effects[call.Method.Value] = effects.GetValueOrDefault(call.Method.Value) + 1;
                node.VisitChildren(Count);
            }
            Count(adapted);
            Require(adapted is BirValueBlock evaluated && evaluated.Result.Value is BirNativeInstance native
                && native.Method.Value == "Substring" && native.Args.Value[1] is BirBinaryOperation { Op.Value: "-" }
                && effects.GetValueOrDefault("ReceiverOnce") == 1 && effects.GetValueOrDefault("StartOnce") == 1
                && effects.GetValueOrDefault("EndOnce") == 1
                && evaluated.Stmts.Value[0] is BirVariable { Init.Value: BirCallStatic { Method.Value: "ReceiverOnce" } }
                && evaluated.Stmts.Value[1] is BirVariable { Init.Value: BirCallStatic { Method.Value: "StartOnce" } },
                "exclusive-end adaptation must evaluate receiver/start/end once in source order");

            var variable = new TypeNode.Tv("method", 0);
            context.Variables["destination"] = variable;
            context.Constraints["method:0"] = new() { new TypeNode.Fqn("Probe.Collection", new TypeNode[] { integer }) };
            var source = new SourceCall(new BirCallInstance { Recv = new BirLocal { Name = "destination" } });
            var nativeClear = new BirNativeInstance { Type = new TypeNode.Fqn("System.Collections.Generic.ICollection`1", new TypeNode[] { integer }),
                Method = "Clear", Recv = new BirLocal { Name = "destination" }, Args = Array.Empty<IBirExpression>() };
            Require(Constrainify(nativeClear, source, refs, context, "Probe.Collection") is BirConstrainedCall constrained
                && constrained.RecvType.Value == variable
                && constrained.Iface.Value is TypeNode.Fqn { Name: "System.Collections.Generic.ICollection`1", Args: [TypeNode.Fqn { Name: "kotlin.Int" }] },
                "local generic receivers must use the declaration's interface constraint frame");
            source = new SourceCall(new BirCallInstance { Recv = new BirCallInstance { Ret = variable, Method = "Read" } });
            Require(ReferenceEquals(Constrainify(nativeClear, source, refs, context, "Probe.Collection"), nativeClear),
                "non-compareTo constrained dispatch must not broaden to expression return shapes");
            var compare = nativeClear with { Method = "CompareTo", Type = new TypeNode.Fqn("System.IComparable`1", new TypeNode[] { variable }) };
            Require(Constrainify(compare, source, refs, context, "Probe.Comparable") is BirConstrainedCall comparison
                && ((TypeNode.Fqn)comparison.Iface.Value).Args.Single() == variable,
                "compareTo must retain its independently permitted expression receiver shape");

            var facts = new BirCallStatic { Ret = integer, DynRet = real, Sty = boolean };
            Require(InferArgType(facts) == integer && RetToken(facts) == real
                && InferArgType(new BirLocal { Sty = text }) is TypeNode.Fqn { Name: "object" },
                "argument/result readers must retain their distinct exact priorities and ignore Sty inference");
            var localProperty = new BirCallInstance { OwnerType = new TypeNode.Fqn("Own"), Method = "size", Prop = "get",
                Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>() };
            _localPropertyAccessors = new Dictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>> {
                [new("Own", "size", "get", 0, 0)] = new[] { new LocalPropertyAccessor("allocated", Array.Empty<TypeNode>()) },
            };
            Require(TransformCall(localProperty, refs, context) == null && localProperty.Method.Value == "size" && localProperty.Prop.Value == "get",
                "the owning local property must remain selected without predicting its final allocated name");

            var projected = new BirCast { Type = new TypeNode.Fqn("kotlin.collections.List"), DotktProjectedCollection = true };
            var nestedArgument = new BirCallStatic { Args = new IBirExpression[] { (BirCast)projected.DeepClone() } };
            var enclosing = new BirFunctionDeclaration { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
                new BirVariable { Name = "projected", Type = new TypeNode.Fqn("kotlin.collections.List"), Init = projected },
                new BirVariable { Name = "ordinary", Type = new TypeNode.Fqn("kotlin.collections.List"), Init = nestedArgument },
            } };
            var extended = context.Extend(enclosing);
            Require(extended.ProjectedCollectionVars.Contains("projected") && !extended.ProjectedCollectionVars.Contains("ordinary"),
                "projected provenance follows only the producing value spine, not an unrelated nested argument");
        }
        finally
        {
            refs.DisposeNet();
            _attributeTopLevelOwner = oldAttribute; _isValue = oldValue; _localPropertyAccessors = oldProperties;
            _localPropertyOwners = oldOwners; _localFactories = oldFactories;
        }
        Console.WriteLine("[typed member calls] self-test OK (exact IDs/byref, companion effects, ordered count adaptation, constrained frames, property ownership, scalar readers)");
    }
}

sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex SourceCallsTestIndex()
    {
        var index = Build(new[] { typeof(object).Assembly.Location });
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var real = new TypeNode.Fqn("kotlin.Double");
        foreach (var (owner, physical, kind) in new[] {
            ("Probe.String", "System.String", "class"), ("Probe.Math", "System.Math", "class"),
            ("Probe.Collection", "System.Collections.Generic.ICollection`1", "interface"),
            ("Probe.Comparable", "System.IComparable`1", "interface"),
        }) { index._ownerAlias[owner] = physical; index._ownerAliasKind[owner] = kind; }
        index._companionPhysicalOwnerBySemanticType["Probe.Companion"] = "Probe.Carrier";
        index._semanticOwnerByCompanionCarrier["Probe.Carrier"] = "Probe.Math";
        index._declarationById["selected-parse"] = new MemberBinding("Probe.Functions", "sameName", 2, "System.Int32.TryParse", true, true,
            ByrefPositions: new[] { 1 }, ParamTypeNodes: new TypeNode[] { text, integer }, ReturnTypeNode: new TypeNode.Fqn("kotlin.Boolean"));
        index._declarationById["selected-pi"] = new MemberBinding("Probe.Carrier", "pi", 0, null, true, false,
            PropertyAccess: 1, PropertyName: "PI", ParamTypeNodes: Array.Empty<TypeNode>(), ReturnTypeNode: real);
        index._declarationById["selected-slice"] = new MemberBinding("Probe.String", "slice", 2, "Substring", true, false,
            ByrefPositions: Array.Empty<int>(), ParamTypeNodes: new TypeNode[] { integer, integer }, ReturnTypeNode: text, CountStart: 0, CountEnd: 1);
        // A same-name, different binding must not override the already selected declaration.
        index._topLevelIntrinsics["sameName"] = "System.Double.Parse";
        return index;
    }
}
