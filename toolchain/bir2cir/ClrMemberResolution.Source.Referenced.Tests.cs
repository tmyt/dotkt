using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void ReferencedSourceSelfTest()
    {
        static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Source final referenced calls: " + reason); }
        var previousReferences = _refs; var previousLocals = _localTypes;
        var previousCanonical = _externalCanonicalTypes; var previousIds = _localDeclarationIds;
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("System.String"); var obj = new TypeNode.Fqn("System.Object");
        var integer = new TypeNode.Fqn("System.Int32"); var unit = new TypeNode.Fqn("void");
        // The selected comparison signature uses RMI's primitive vocabulary;
        // its exact MemberRef separately keeps the defining CLR type names.
        var declarationText = new TypeNode.Fqn("string"); var declarationObject = new TypeNode.Fqn("object");
        var declarationInteger = new TypeNode.Fqn("int");
        var listType = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { text });
        var interfaceType = new TypeNode.Fqn("System.Collections.Generic.IList`1", new TypeNode[] { text });
        var locals = new HashSet<string>(StringComparer.Ordinal) { "Consumer", "System.String" };
        var canonical = new HashSet<string>(StringComparer.Ordinal) { "System.String" };
        var ids = new HashSet<string>(StringComparer.Ordinal) { "local-method" };
        BirFileDeclaration File(params IBirExpression[] expressions) => new() { FileClass = "Consumer", Methods = new[] {
            new BirFunctionDeclaration { Name = "use", Ret = unit,
                TypeParams = new[] { new BirTypeParameter { Name = "Receiver", Constraints = new TypeNode[] { interfaceType } } },
                Params = new[] { new BirParameter { Name = "list", Type = listType },
                    new BirParameter { Name = "text", Type = text }, new BirParameter { Name = "subject", Type = new TypeNode.Tv("method", 0) } },
                Body = expressions.Select(expression => (IBirStatement)new BirExpressionStatement {
                    Expr = OptionalField<IBirExpression>.Of(expression) }).ToArray() },
            new BirFunctionDeclaration { Name = "local", DeclarationId = "local-method", Static = true,
                Ret = unit, Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() },
        } };
        BirCallStatic Concat() => new() { Owner = text, Method = "Concat", Sig = new TypeNode[] { declarationText, declarationText },
            Ret = text, Sty = text, Args = new IBirExpression[] {
                new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("left") },
                new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("right") } } };
        var snapshotIds = new List<int>();
        try
        {
            var call = Concat();
            CaptureReferencedStaticCallSignatures(call);
            snapshotIds.Add(call.DotktKotlinSigId.Value);
            // Representation erased the visible vector after source capture.
            // The same snapshot row still selects the authored string overload.
            call.Sig = new TypeNode[] { declarationObject, declarationObject }; call.ArgTypes = new TypeNode[] { declarationObject, declarationObject };
            var sibling = Concat(); CaptureReferencedStaticCallSignatures(sibling);
            var siblingId = sibling.DotktKotlinSigId.Value; snapshotIds.Add(siblingId);
            var receiver = new BirLocal { Name = "list", Sty = listType };
            var index = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) };
            var item = new BirCallInstance { OwnerType = receiver.Sty, Method = "get_Item", Ret = text, Sty = text,
                Sig = new TypeNode[] { declarationInteger }, Args = new IBirExpression[] { index },
                Recv = OptionalField<IBirExpression>.Of(receiver), Virtual = false };
            var generic = new BirCallStatic { Owner = new TypeNode.Fqn("System.Array"), Method = "Empty",
                TypeArgs = new TypeNode[] { text }, Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(),
                Ret = new TypeNode.Array(text) };
            var direct = new BirNewDelegate { CalleeOwner = text, Method = "Concat", Sig = new TypeNode[] { declarationText, declarationText },
                FuncType = new TypeNode.Fn(false, text, new TypeNode[] { text, text }), DotktDeclarationIdentityBound = false };
            var boundReceiver = new BirLocal { Name = "text", Sty = text };
            var bound = new BirNewBoundDelegate { OwnerType = text, Method = "StartsWith", Sig = new TypeNode[] { declarationText },
                ArgTypes = new TypeNode[] { text }, Recv = OptionalField<IBirExpression>.Of(boundReceiver),
                FuncType = new TypeNode.Fn(false, new TypeNode.Fqn("System.Boolean"), new TypeNode[] { text }) };
            var argument = new BirNewDelegate { CalleeOwner = new TypeNode.Fqn("Consumer"), Method = "local",
                DeclarationId = "local-method", Sig = Array.Empty<TypeNode>(), FuncType = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()) };
            var task = new BirCallStatic { Owner = new TypeNode.Fqn("System.Threading.Tasks.Task"), Method = "Run",
                Sig = new TypeNode[] { new TypeNode.Fn(false, unit, Array.Empty<TypeNode>(), Clr: "System.Action") },
                Args = new IBirExpression[] { argument } };
            var stale = new BirCallStatic { Owner = new TypeNode.Fqn("Consumer"), Method = "local", DeclarationId = "referenced-wrapper",
                Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() };
            var selectedLocal = new BirCallStatic { Owner = text, Method = "local", DeclarationId = "local-method",
                Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() };
            var concat = typeof(string).GetMethod("Concat", new[] { typeof(object), typeof(object) })!;
            var exact = ExactStaticCall(refs, concat, text, new IBirExpression[] {
                new BirConstant { Type = obj, Value = new BirLiteral.NullLiteral() },
                new BirConstant { Type = obj, Value = new BirLiteral.NullLiteral() } });
            var exactMember = exact.MemberRef.Value; var exactSignature = exact.Sig.Value;
            var unselected = new BirConstrainedCall { Iface = interfaceType, RecvType = new TypeNode.Tv("method", 0),
                Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = "subject", Sty = new TypeNode.Tv("method", 0) }),
                Method = "get_Item", Sig = new TypeNode[] { integer }, Args = new IBirExpression[] {
                    new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) } } };
            var root = File(call, item, generic, direct, bound, task, stale, selectedLocal, exact, unselected);
            var methods = root.Methods.Value; var body = methods[0].Body.Value; var callArguments = call.Args.Value;
            var saved = new byte[] { 1, 2, 4 }; methods[0].InlineBir = saved;
            ResolveReferencedStaticCalls(root, refs, locals, canonical, ids, "Consumer.kt");
            Require(call.MemberRef.Value.Name.Value == "Concat"
                && call.MemberRef.Value.ParameterTypes.Value.SequenceEqual(new TypeNode[] { text, text })
                && call.Sig.Value.SequenceEqual(new TypeNode[] { declarationText, declarationText }) && !call.ArgTypes.IsPresent
                && !call.DotktKotlinSigId.IsPresent && !KotlinSigSnapshots.ContainsKey(snapshotIds[0])
                && sibling.DotktKotlinSigId.Value == siblingId && KotlinSigSnapshots.ContainsKey(siblingId),
                "source signature priority, canonical runtime owner or per-file snapshot consumption changed");
            Require(item.MemberRef.Value.DeclaringType.Value == receiver.Sty.Value
                && item.MemberRef.Value.ReturnType.Value == new TypeNode.Tv("type", 0)
                && item.Ret.Value == text && item.Sty.Value == text && !item.Super.IsPresent && !item.Virtual.Value
                && ReferenceEquals(item.Recv.Value, receiver) && ReferenceEquals(item.Args.Value[0], index),
                "closed owner versus open declaration frame, exact dispatch or receiver/argument identity changed");
            var arrayParameter = typeof(Array).GetMethod("Empty")!.GetGenericArguments().Single();
            Require(generic._resolvedMethodTypeParams.Value.Single().StructurallyEquals(
                    ReferenceMetadataIndex.GenericParamSourceDeclaration(arrayParameter))
                && generic.MemberRef.Value.GenericArity.Value == 1
                && generic.MemberRef.Value.ReturnType.Value == new TypeNode.Array(new TypeNode.Tv("method", 0))
                && generic.TypeArgs.Value.Single() == text,
                "selected physical generic flags/frame were replaced by source parameter facts");
            Require(direct.MemberRef.Value.Name.Value == "Concat" && direct.DotktDeclarationIdentityBound.HasValue
                && !direct.DotktDeclarationIdentityBound.Value && !direct._resolvedMethodTypeParams.IsPresent
                && bound.MemberRef.Value.Name.Value == "StartsWith" && !bound.ArgTypes.IsPresent
                && ReferenceEquals(bound.Recv.Value, boundReceiver) && argument.DeclarationId.Value == "local-method"
                && ReferenceEquals(task.Args.Value[0], argument) && argument.DotktDelegateSlot.Value == new TypeNode.Fqn("System.Action"),
                "delegate declaration/slot binding, optional false presence or operand ownership changed");
            Require(!stale.DeclarationId.IsPresent && !stale.MemberRef.IsPresent
                && selectedLocal.DeclarationId.Value == "local-method" && !selectedLocal.MemberRef.IsPresent
                && ReferenceEquals(exact.MemberRef.Value, exactMember) && ReferenceEquals(exact.Sig.Value, exactSignature)
                && !unselected.MemberRef.IsPresent && ReferenceEquals(root.Methods.Value, methods)
                && ReferenceEquals(methods[0].Body.Value, body) && ReferenceEquals(methods[0].InlineBir.Value, saved)
                && ReferenceEquals(call.Args.Value, callArguments),
                "local/source-bound priority, constrained ID guard or continuing graph identity changed");
            ResolveReferencedStaticCalls(File(sibling), refs, locals, canonical, ids, "Sibling.kt");
            Require(!sibling.DotktKotlinSigId.IsPresent && !KotlinSigSnapshots.ContainsKey(siblingId),
                "a sibling file lost its own snapshot lifetime");
            Console.WriteLine("[source final referenced calls] shared exact selection, snapshots, current owners, delegate slots and physical frames: OK");
        }
        finally
        {
            foreach (var id in snapshotIds) KotlinSigSnapshots.Remove(id);
            _refs = previousReferences; _localTypes = previousLocals;
            _externalCanonicalTypes = previousCanonical; _localDeclarationIds = previousIds;
        }
    }
}
