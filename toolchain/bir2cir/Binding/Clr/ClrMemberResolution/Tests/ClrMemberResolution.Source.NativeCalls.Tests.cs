using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void NativeCallsSourceSelfTest()
    {
        static void Need(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Typed native calls: " + reason); }
        static BirExpressionStatement Evaluate(IBirExpression expression)
            => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        static void Refused(Action action, string fragment)
        {
            try { action(); }
            catch (InvalidOperationException error) when (error.Message.Contains(fragment, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Typed native calls: missing refusal for " + fragment);
        }
        var savedReferences = _refs; var savedLocals = _localTypes;
        try
        {
            var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var localTypes = new HashSet<string> { "LocalConstruction" };
            var text = new TypeNode.Fqn("System.String"); var obj = new TypeNode.Fqn("System.Object");
            var integer = new TypeNode.Fqn("System.Int32"); var methodTv = new TypeNode.Tv("method", 0);
            var typeTv = new TypeNode.Tv("type", 0); var unit = new TypeNode.Fqn("void");
            var left = new BirLocal { Name = "left", Sty = obj }; var right = new BirLocal { Name = "right", Sty = obj };
            var concatArguments = new IBirExpression[] { left, right };
            var concat = new BirNativeStatic { Type = text, Method = "Concat", Args = concatArguments,
                ArgTypes = new TypeNode[] { obj, obj }, ResolvedMemberParams = new TypeNode[] { obj, obj },
                Ret = text, Sty = text, _resolvedMethodTypeParams = OptionalField<BirTypeParameter[]>.Null };
            var self = new BirThis();
            var clone = new BirNativeInstance { Type = obj, Method = "MemberwiseClone", Recv = self,
                Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>(), Ret = obj };
            var valueReceiver = new BirLocal { Name = "duration", Sty = new TypeNode.Fqn("System.TimeSpan") };
            var valueCall = new BirNativeInstance { Type = new TypeNode.Fqn("System.TimeSpan"), Method = "ToString",
                Recv = valueReceiver, Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>(), Ret = text };
            var arrayReceiver = new BirLocal { Name = "items", Sty = new TypeNode.Array(integer) };
            var arrayCall = new BirNativeInstance { Type = new TypeNode.Array(integer), Method = "Clone", Recv = arrayReceiver,
                Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>(), Ret = obj };

            var empty = new BirGenericStatic { Type = new TypeNode.Fqn("System.Array"), Method = "Empty",
                TypeArgs = new TypeNode[] { methodTv }, Args = Array.Empty<IBirExpression>(),
                ResolvedMemberParams = Array.Empty<TypeNode>(), Ret = new TypeNode.Array(methodTv) };
            var listOwner = new TypeNode.Fqn("System.Collections.Generic.List", new TypeNode[] { text });
            var convertDeclaration = ResolveOwnerType(listOwner, references).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(method => method.Name == "ConvertAll" && method.IsGenericMethodDefinition);
            var converter = new BirLocal { Name = "converter", Sty = new TypeNode.Fqn("System.Converter", new TypeNode[] { text, integer }) };
            var convertReceiver = new BirLocal { Name = "list", Sty = listOwner };
            var convertArguments = new IBirExpression[] { converter };
            var convert = new BirGenericInstance { Type = listOwner, Method = "ConvertAll", Recv = convertReceiver,
                TypeArgs = new TypeNode[] { integer }, Args = convertArguments,
                ResolvedMemberParams = new TypeNode[] { new TypeNode.Fqn("System.Converter", new TypeNode[] { typeTv, methodTv }) },
                Ret = new TypeNode.Fqn("System.Collections.Generic.List", new TypeNode[] { integer }) };
            var index = new BirLocal { Name = "index", Sty = integer };
            var itemReceiver = new BirLocal { Name = "list", Sty = listOwner };
            var item = new BirConstrainedCall { Iface = new TypeNode.Fqn("System.Collections.Generic.IList", new TypeNode[] { text }),
                Method = "get_Item", Recv = itemReceiver, Args = new IBirExpression[] { index },
                ResolvedMemberParams = new TypeNode[] { integer }, Sig = new TypeNode[] { integer }, Ret = text, Super = true };
            var capacity = new BirLocal { Name = "capacity", Sty = integer };
            var builderArguments = new IBirExpression[] { capacity };
            var builder = new BirNew { Type = new TypeNode.Fqn("System.Text.StringBuilder"), Args = builderArguments,
                ArgTypes = new TypeNode[] { obj }, MemberSignature = new TypeNode[] { integer },
                ConstructorDeclarationId = OptionalField<string>.Null };
            var localArguments = new IBirExpression[] { new BirLocal { Name = "local" } };
            var local = new BirNew { Type = new TypeNode.Fqn("LocalConstruction"), Args = localArguments,
                ArgTypes = new TypeNode[] { text } };

            var upperReceiver = new BirLocal { Name = "text", Sty = text };
            var upper = new BirBoundClrDelegate { ClrType = text, Method = "ToUpperInvariant", Recv = upperReceiver,
                Type = new TypeNode.Fn(false, text, Array.Empty<TypeNode>()), FuncType = new TypeNode.Fn(false, text, Array.Empty<TypeNode>()) };
            var joined = new BirClrStaticDelegate { ClrType = text, Method = "Concat",
                ArgTypes = new TypeNode[] { text, text }, Type = new TypeNode.Fn(false, text, new TypeNode[] { text, text }),
                FuncType = new TypeNode.Fn(false, text, new TypeNode[] { text, text }) };
            var bytes = new byte[] { 9, 8, 7 };
            var body = new IBirStatement[] { Evaluate(concat), Evaluate(clone), Evaluate(valueCall), Evaluate(arrayCall),
                Evaluate(empty), Evaluate(convert), Evaluate(item), Evaluate(builder), Evaluate(local), Evaluate(upper), Evaluate(joined) };
            var caller = new BirFunctionDeclaration { Name = "actual", Params = Array.Empty<BirParameter>(), Ret = unit, Body = body, InlineBir = bytes };
            var file = new BirFileDeclaration { FileClass = "NativeCalls", Methods = new[] { caller } };
            Apply(file, references, localTypes);

            Need(concat.MemberRef.Value.Name.Value == "Concat" && concat.MemberRef.Value.ParameterTypes.Value.SequenceEqual(new TypeNode[] { obj, obj })
                && concat.ResolvedMemberReturn.Value == text && concat.Ret.Value == text && concat.Sty.Value == text
                && !concat.ArgTypes.IsPresent && !concat.ResolvedMemberParams.IsPresent && !concat._resolvedMethodTypeParams.IsPresent
                && ReferenceEquals(concat.Args.Value, concatArguments) && ReferenceEquals(concat.Args.Value[0], left) && ReferenceEquals(concat.Args.Value[1], right),
                "plain selected signature, declared-return/optional presence or actual argument ownership changed");
            Need(clone.MemberRef.Value.Name.Value == "MemberwiseClone" && clone.MemberRef.Value.DeclaringType.Value is TypeNode.Fqn { Name: "System.Object" }
                && clone.Dispatch.Value == "call" && ReferenceEquals(clone.Recv.Value, self), "protected instance slot or lexical receiver was lost");
            Need(valueCall.Dispatch.Value == "constrained" && valueCall.MemberRef.Value.DeclaringType.Value is TypeNode.Fqn { Name: "System.Object" }
                && valueCall.ResolvedMemberReturn.Value == text && ReferenceEquals(valueCall.Recv.Value, valueReceiver),
                "constrained dispatch named the value override body instead of its virtual root slot");
            Need(arrayCall.Type.Value == new TypeNode.Fqn("System.Array") && arrayCall.MemberRef.Value.Name.Value == "Clone"
                && ReferenceEquals(arrayCall.Recv.Value, arrayReceiver), "array instance owner was not retargeted without duplicating its receiver");
            Need(empty.MemberRef.Value.GenericArity.Value == 1 && empty.MemberRef.Value.ReturnType.Value is TypeNode.Array { Elem: TypeNode.Tv { Scope: "method", I: 0 } }
                && empty._resolvedMethodTypeParams.Value.Length == 1 && !empty.ResolvedMemberParams.IsPresent,
                "generic static selection closed the open MethodDef or lost its actual generic facts");
            Need(convert.MemberRef.Value.ParameterTypes.Value[0] is TypeNode.Fqn { Name: "System.Converter`2", Args: [TypeNode.Tv { Scope: "type", I: 0 }, TypeNode.Tv { Scope: "method", I: 0 }] }
                && convert.ResolvedMemberReturn.Value is TypeNode.Fqn { Name: "System.Collections.Generic.List", Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
                && convert.ReceiverMode.Value == "value" && convert.Dispatch.Value == Dispatch(convertDeclaration, ResolveOwnerType(listOwner), false)
                && ReferenceEquals(convert.Recv.Value, convertReceiver) && ReferenceEquals(convert.Args.Value, convertArguments)
                && !convert.ResolvedMemberParams.IsPresent && convert._resolvedMethodTypeParams.Value.Length == 1,
                "generic instance descriptor was closed before selection, stamped from the caller return, or copied its operands");
            Need(item.MemberRef.Value.ReturnType.Value == typeTv && item.ResolvedMemberReturn.Value == typeTv
                && !item.Super.IsPresent && !item.ResolvedMemberParams.IsPresent && ReferenceEquals(item.Args.Value[0], index),
                "constrained selected signature or super/descriptor consumption changed");
            Need(builder.MemberRef.Value.Kind.Value == MemberRefNode.Kinds.Ctor && builder.MemberRef.Value.ParameterTypes.Value.SequenceEqual(new TypeNode[] { integer })
                && builder.ResolvedMemberReturn.Value == unit && !builder.ArgTypes.IsPresent && !builder.MemberSignature.IsPresent && !builder.ConstructorDeclarationId.IsPresent
                && ReferenceEquals(builder.Args.Value, builderArguments) && ReferenceEquals(builder.Args.Value[0], capacity),
                "selected constructor declaration lost priority over applicability or consumed the wrong facts");
            Need(!local.MemberRef.IsPresent && ReferenceEquals(local.Args.Value, localArguments) && local.ArgTypes.HasValue,
                "a current local constructor was rebound through reflection");
            Need(upper.MemberRef.Value.Name.Value == "ToUpperInvariant" && upper.ResolvedMemberReturn.Value == text
                && upper.DelegateCtorRef.HasValue && ReferenceEquals(upper.Recv.Value, upperReceiver)
                && joined.MemberRef.Value.ParameterTypes.Value.SequenceEqual(new TypeNode[] { text, text })
                && joined.DelegateCtorRef.HasValue && !joined.ArgTypes.IsPresent,
                "CLR delegate target/constructor role, open signature or receiver ownership changed");
            Need(ReferenceEquals(file.Methods.Value[0], caller) && ReferenceEquals(caller.Body.Value, body) && ReferenceEquals(caller.InlineBir.Value, bytes),
                "resolution replaced declarations/body or inspected opaque executable metadata");

            // The constructor view conversion moves the actual caller operand;
            // this is a fresh wrapper, not a second evaluation of the program.
            var sourceView = new TypeNode.Fqn("System.Collections.Generic.IReadOnlyList", new TypeNode[] { text });
            var actualView = new BirLocal { Name = "readonlyView", Sty = sourceView };
            var viewArguments = new IBirExpression[] { actualView };
            var collection = new BirNewClr { Type = new TypeNode.Fqn("System.Collections.ObjectModel.Collection", new TypeNode[] { text }),
                Args = viewArguments, ArgTypes = new TypeNode[] { sourceView },
                MemberSignature = new TypeNode[] { new TypeNode.Fqn("System.Collections.Generic.IList", new TypeNode[] { typeTv }) } };
            ResolveNativeCallsSource(collection);
            Need(ReferenceEquals(collection.Args.Value, viewArguments) && viewArguments[0] is BirCast cast
                && cast.Type.Value == new TypeNode.Fqn("System.Collections.Generic.IList", new TypeNode[] { text })
                && ReferenceEquals(cast.E.Value, actualView) && !cast.Sty.IsPresent
                && collection.MemberRef.Value.ParameterTypes.Value[0] is TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }] },
                "constructor collection view cloned the operand or prematurely closed its selected declaration");

            var explicitNullReference = new BirConstrainedCall { MemberRef = OptionalField<BirMemberReference>.Null,
                Super = true, ResolvedMemberParams = Array.Empty<TypeNode>() };
            ResolveNativeCallsSource(explicitNullReference);
            Need(explicitNullReference.MemberRef.IsNull && !explicitNullReference.Super.IsPresent && explicitNullReference.ResolvedMemberParams.HasValue,
                "constrained ContainsKey short-circuit was weakened to HasValue");
            var alreadyResolved = new BirGenericStatic { MemberRef = empty.MemberRef, ResolvedMemberReturn = OptionalField<TypeNode>.Null,
                ResolvedMemberParams = Array.Empty<TypeNode>() };
            ResolveNativeCallsSource(alreadyResolved);
            Need(alreadyResolved.ResolvedMemberReturn.IsNull && ReferenceEquals(alreadyResolved.MemberRef.Value, empty.MemberRef.Value)
                && !alreadyResolved.ResolvedMemberParams.IsPresent, "generic explicit-null return presence short-circuit changed");
            Refused(() => ResolveNativeCallsSource(new BirGenericStatic { ResolvedMemberReturn = OptionalField<TypeNode>.Null }),
                "declared-return stamp without its resolved memberRef");
            Refused(() => ResolveNativeCallsSource(new BirGenericStatic { Type = new TypeNode.Fqn("System.Array"), Method = "Empty",
                TypeArgs = new TypeNode[] { text }, Args = Array.Empty<IBirExpression>(), ResolvedMemberParams = OptionalField<TypeNode[]>.Null }),
                "no usable frontend-resolved parameter vector");
            Refused(() => ResolveNativeCallsSource(new BirConstrainedCall { Iface = item.Iface, Method = "get_Item" }), "requires an 'args' array");
            Refused(() => ResolveNativeCallsSource(new BirNew { Type = builder.Type,
                Args = new IBirExpression[] { new BirLocal { Name = "capacity", Sty = integer } },
                ArgTypes = new TypeNode[] { integer }, MemberSignature = new TypeNode[] { integer },
                ConstructorDeclarationId = "missing-current-selected-constructor" }), "selects 0 runtime MethodDefs");
        }
        finally { _refs = savedReferences; _localTypes = savedLocals; }
        Console.WriteLine("[source native calls] constructors, exact open declarations, dispatch roots, protected/array/delegate roles and owning operands: OK");
    }
}
