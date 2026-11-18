using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void ReferencedSourceSelfTest()
    {
        static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Source final referenced calls: " + reason); }
        var previousLocals = _localTypes;
        var refs = ReferenceMetadataIndex.ReferencedSourceTestIndex(out var selectedPropertyId);
        var text = new TypeNode.Fqn("System.String"); var obj = new TypeNode.Fqn("System.Object");
        var integer = new TypeNode.Fqn("System.Int32"); var unit = new TypeNode.Fqn("void");
        // Incoming descriptors can use primitive tokens. The selected metadata
        // declaration read through MetadataLoadContext retains its CLR names.
        var declarationText = new TypeNode.Fqn("string"); var declarationObject = new TypeNode.Fqn("object");
        var declarationInteger = new TypeNode.Fqn("int");
        var listType = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { text });
        var interfaceType = new TypeNode.Fqn("System.Collections.Generic.IList`1", new TypeNode[] { text });
        var tokenType = new TypeNode.Fqn("System.Threading.CancellationToken");
        var locals = new HashSet<string>(StringComparer.Ordinal) { "Consumer", "System.String" };
        var canonical = new HashSet<string>(StringComparer.Ordinal) { "System.String" };
        var ids = new HashSet<string>(StringComparer.Ordinal) { "local-method" };
        BirFileDeclaration File(params IBirExpression[] expressions) => new() { FileClass = "Consumer", Methods = new[] {
            new BirFunctionDeclaration { Name = "use", Ret = unit,
                TypeParams = new[] { new BirTypeParameter { Name = "Receiver", Constraints = new TypeNode[] { interfaceType } } },
                Params = new[] { new BirParameter { Name = "list", Type = listType },
                    new BirParameter { Name = "text", Type = text }, new BirParameter { Name = "subject", Type = new TypeNode.Tv("method", 0) },
                    new BirParameter { Name = "token", Type = tokenType } },
                Body = expressions.Select(expression => (IBirStatement)new BirExpressionStatement {
                    Expr = OptionalField<IBirExpression>.Of(expression) }).ToArray() },
            new BirFunctionDeclaration { Name = "local", DeclarationId = "local-method", Static = true,
                Ret = unit, Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() },
        } };
        BirCallStatic Concat() => new() { Owner = text, Method = "Concat", Sig = new TypeNode[] { declarationText, declarationText },
            Ret = text, Sty = text, Args = new IBirExpression[] {
                new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("left") },
                new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("right") } } };
        using var signatures = new ReferencedCallSignatures();
        try
        {
            var call = Concat();
            signatures.Capture(call);
            var clone = (BirCallStatic)call.DeepClone();
            // Representation erased the visible vector after source capture.
            // The same snapshot row still selects the authored string overload.
            call.Sig = new TypeNode[] { declarationObject, declarationObject }; call.ArgTypes = new TypeNode[] { declarationObject, declarationObject };
            var sibling = Concat(); signatures.Capture(sibling);
            var siblingId = sibling.DotktKotlinSigId.Value;
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
            var registration = new BirCallInstance { OwnerType = tokenType, Method = "Register",
                Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = "token", Sty = tokenType }),
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
            var root = File(call, item, generic, direct, bound, registration, stale, selectedLocal, exact, unselected);
            var methods = root.Methods.Value; var body = methods[0].Body.Value; var callArguments = call.Args.Value;
            var saved = new byte[] { 1, 2, 4 }; methods[0].InlineBir = saved;
            ResolveReferencedStaticCalls(root, refs, locals, canonical, ids, signatures.Read, signatures.ReleaseMarkers, "Consumer.kt");
            Require(call.MemberRef.Value.Name.Value == "Concat", "canonical runtime owner did not bind Concat");
            Require(call.MemberRef.Value.ParameterTypes.Value.SequenceEqual(new TypeNode[] { text, text }),
                "source snapshot selected unexpected MemberRef parameters: " + string.Join(", ", call.MemberRef.Value.ParameterTypes.Value.AsEnumerable()));
            Require(call.Sig.Value.SequenceEqual(new TypeNode[] { text, text }),
                "selected comparison signature changed: " + string.Join(", ", call.Sig.Value.AsEnumerable()));
            Require(!call.ArgTypes.IsPresent, "selected call retained its old argument descriptor");
            Require(!call.DotktKotlinSigId.IsPresent && signatures.Read(call) == null,
                "current file retained its source token");
            Require(sibling.DotktKotlinSigId.Value == siblingId && signatures.Read(sibling).Length == 2,
                "current file consumed a sibling's snapshot");
            // Resolve the independently owned clone after the original file
            // released its token. Shared source facts outlive either file.
            clone.Sig = new TypeNode[] { declarationObject, declarationObject };
            ResolveReferencedStaticCalls(File(clone), refs, locals, canonical, ids, signatures.Read, signatures.ReleaseMarkers, "Clone.kt");
            Require(clone.MemberRef.Value.ParameterTypes.Value.SequenceEqual(new TypeNode[] { text, text })
                && !clone.DotktKotlinSigId.IsPresent, "a cross-file clone lost the source overload after file release");
            Require(item.MemberRef.Value.DeclaringType.Value == receiver.Sty.Value
                && item.MemberRef.Value.ReturnType.Value == new TypeNode.Tv("type", 0)
                && item.Ret.Value == text && item.Sty.Value == text && !item.Super.IsPresent && !item.Virtual.Value
                && ReferenceEquals(item.Recv.Value, receiver) && ReferenceEquals(item.Args.Value[0], index),
                "closed owner versus open declaration frame, exact dispatch or receiver/argument identity changed");
            var arrayParameter = typeof(Array).GetMethod("Empty")!.GetGenericArguments().Single();
            Require(generic._resolvedMethodTypeParams.Value.Single().StructurallyEquals(
                    ReferenceMetadataReader.GenericParamSourceDeclaration(arrayParameter))
                && generic.MemberRef.Value.GenericArity.Value == 1
                && generic.MemberRef.Value.ReturnType.Value == new TypeNode.Array(new TypeNode.Tv("method", 0))
                && generic.TypeArgs.Value.Single() == text,
                "selected physical generic flags/frame were replaced by source parameter facts");
            Require(direct.MemberRef.Value.Name.Value == "Concat" && direct.DotktDeclarationIdentityBound.HasValue
                && !direct.DotktDeclarationIdentityBound.Value && !direct._resolvedMethodTypeParams.IsPresent,
                "static delegate declaration or optional false presence changed");
            Require(bound.MemberRef.Value.Name.Value == "StartsWith" && !bound.ArgTypes.IsPresent
                && ReferenceEquals(bound.Recv.Value, boundReceiver), "bound delegate declaration or receiver ownership changed");
            Require(argument.DeclarationId.Value == "local-method" && ReferenceEquals(registration.Args.Value[0], argument),
                "local delegate identity or argument ownership changed");
            Require(registration.MemberRef.HasValue && argument.DotktDelegateSlot.HasValue
                && argument.DotktDelegateSlot.Value == new TypeNode.Fqn("System.Action"),
                "selected delegate slot changed: " + argument.DotktDelegateSlot.OrDefault());
            Require(!stale.DeclarationId.IsPresent && !stale.MemberRef.IsPresent
                && selectedLocal.DeclarationId.Value == "local-method" && !selectedLocal.MemberRef.IsPresent
                && ReferenceEquals(exact.MemberRef.Value, exactMember) && ReferenceEquals(exact.Sig.Value, exactSignature)
                && !unselected.MemberRef.IsPresent && ReferenceEquals(root.Methods.Value, methods)
                && ReferenceEquals(methods[0].Body.Value, body) && ReferenceEquals(methods[0].InlineBir.Value, saved)
                && ReferenceEquals(call.Args.Value, callArguments),
                "local/source-bound priority, constrained ID guard or continuing graph identity changed");

            var constrainedReceiver = new BirLocal { Name = "subject", Sty = new TypeNode.Tv("method", 0) };
            var constrainedIndex = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) };
            var selectedConstrained = new BirConstrainedCall { Iface = interfaceType,
                RecvType = new TypeNode.Tv("method", 0), Recv = OptionalField<IBirExpression>.Of(constrainedReceiver),
                Method = "element", Prop = "get", DeclarationId = selectedPropertyId,
                Sig = new TypeNode[] { integer }, ArgTypes = new TypeNode[] { integer }, Args = new IBirExpression[] { constrainedIndex } };
            var selectedReceiver = new BirLocal { Name = "list", Sty = listType };
            var selectedIndex = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) };
            var inheritedSelection = new BirCallInstance { OwnerType = listType,
                Recv = OptionalField<IBirExpression>.Of(selectedReceiver), Method = "element", DeclarationId = selectedPropertyId,
                Sig = new TypeNode[] { integer }, Args = new IBirExpression[] { selectedIndex } };
            BirCallInstance Property(bool super) => new() { OwnerType = interfaceType,
                Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = "list", Sty = listType }),
                Method = "element", Prop = "get", Super = super, Virtual = false,
                Sig = new TypeNode[] { integer }, Args = new IBirExpression[] {
                    new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) } } };
            var ordinaryProperty = Property(false);
            var superProperty = Property(true);
            var selectedRoot = File(selectedConstrained, inheritedSelection, ordinaryProperty, superProperty);
            DeclarationIdentityBinding.BindReferenced(selectedRoot, refs, ids, preserveForScalarResolution: true);
            Require(selectedConstrained.Method.Value == "get_Item" && !selectedConstrained.Prop.IsPresent
                && !selectedConstrained.PropertyName.IsPresent && !selectedConstrained.PropertyAccessor.IsPresent
                && selectedConstrained.DeclarationId.Value == selectedPropertyId
                && inheritedSelection.DeclarationId.Value == selectedPropertyId,
                "selected constrained property retained its semantic role or lost its scalar-resolution identity");
            ResolveReferencedStaticCalls(selectedRoot, refs, locals, canonical, ids, signatures.Read, signatures.ReleaseMarkers, "Selected.kt");
            Require(selectedConstrained.MemberRef.Value.Name.Value == "get_Item"
                && selectedConstrained.MemberRef.Value.DeclaringType.Value == interfaceType
                && selectedConstrained.MemberRef.Value.ReturnType.Value == new TypeNode.Tv("type", 0)
                && selectedConstrained.ResolvedMemberReturn.Value == new TypeNode.Tv("type", 0)
                && selectedConstrained.DotktDeclarationIdentityBound.Value && !selectedConstrained.DeclarationId.IsPresent
                && !selectedConstrained.ArgTypes.IsPresent && !selectedConstrained.PropertyName.IsPresent
                && ReferenceEquals(selectedConstrained.Recv.Value, constrainedReceiver)
                && ReferenceEquals(selectedConstrained.Args.Value[0], constrainedIndex),
                "constrained identity selection reinterpreted a physical getter name, changed its frame or copied an operand");
            Require(inheritedSelection.MemberRef.Value.DeclaringType.Value == interfaceType
                && inheritedSelection.MemberRef.Value.ReturnType.Value == new TypeNode.Tv("type", 0)
                && inheritedSelection.DotktDeclarationIdentityBound.Value && !inheritedSelection.DeclarationId.IsPresent
                && ReferenceEquals(inheritedSelection.Recv.Value, selectedReceiver)
                && ReferenceEquals(inheritedSelection.Args.Value[0], selectedIndex),
                "selected interface MethodDef was not projected through the constructed implementing receiver");
            foreach (var property in new[] { ordinaryProperty, superProperty })
                Require(property.MemberRef.Value.Name.Value == "get_Item" && property.Method.Value == "get_Item"
                    && property.PropertyName.Value == "element" && property.PropertyAccessor.Value == "get"
                    && !property.Prop.IsPresent && property.Virtual.Value == !property.Super.Value
                    && property.MemberRef.Value.DeclaringType.Value == interfaceType,
                    "source property association changed its selected physical accessor or super dispatch");
            ResolveReferencedStaticCalls(File(sibling), refs, locals, canonical, ids, signatures.Read, signatures.ReleaseMarkers, "Sibling.kt");
            Require(!sibling.DotktKotlinSigId.IsPresent && signatures.Read(sibling) == null,
                "a sibling file lost its own snapshot lifetime");
            Console.WriteLine("[source final referenced calls] shared exact selection, snapshots, current owners, delegate slots and physical frames: OK");
        }
        finally
        {
            _localTypes = previousLocals;
        }
    }
}

sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex ReferencedSourceTestIndex(out string selectedPropertyId)
    {
        var index = Build(new[] { typeof(object).Assembly.Location });
        // Like the physical-parameter facts tests, attach a trusted source
        // association to an ACTUAL parsed reference row. Keep its exact token,
        // physical owner, signature, staticness and virtual declaration facts.
        var getter = typeof(IList<>).GetProperty("Item")!.GetMethod!;
        var source = index._membersByOwner["System.Collections.Generic.IList"]
            .Single(binding => binding.MetadataToken == getter.MetadataToken);
        selectedPropertyId = "selected-reference-property-test";
        var selected = source with { DeclarationId = selectedPropertyId,
            DeclarationSourceName = "element", SourcePropertyName = "element", AccessorKind = "get" };
        index._declarationById.Add(selectedPropertyId, selected);
        index._declarationFamilyCounts.Add(DeclarationFamilyOf(selected), 1);
        foreach (var rows in new[] { index._membersByOwner["System.Collections.Generic.IList"],
            index._membersByPhysicalOwner[getter.DeclaringType!.FullName!] })
            rows[rows.FindIndex(binding => ReferenceEquals(binding, source))] = selected;
        return index;
    }
}
