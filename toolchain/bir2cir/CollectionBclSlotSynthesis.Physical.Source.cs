using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionBclSlotSynthesis
{
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs,
        Func<TypeNode, TypeNode> physicalType,
        Action<BirFileDeclaration, IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> publishMethods)
    {
        ArgumentNullException.ThrowIfNull(physicalType);
        ArgumentNullException.ThrowIfNull(publishMethods);
        // Freeze the whole current module before adding slots. A base/interface
        // in a later file participates in exactly the same constructed closure.
        var definitions = SupertypeGraph.CollectSource(roots);
        var declarations = new Dictionary<string, (BirFileDeclaration File, IBirTypeDeclaration Owner)>(StringComparer.Ordinal);
        foreach (var file in roots)
        foreach (var owner in file.Types.OrEmpty())
            if (owner.Name.HasValue) declarations[owner.Name.Value] = (file, owner);

        foreach (var definition in definitions.Values)
        {
            var (file, owner) = declarations[definition.Name];
            var contracts = owner is BirClassDefinition cls && cls.DotktKotlinMutableCollectionContract.HasValue
                ? cls.DotktKotlinMutableCollectionContract.Value.Types.Select(physicalType)
                    .OfType<TypeNode.Fqn>().Select(CollectionFace).OfType<TypeNode.Fqn>().ToList()
                : new List<TypeNode.Fqn>();
            if (owner is BirClassDefinition contractOwner) contractOwner.DotktKotlinMutableCollectionContract = default;
            if (definition.Kind != "class" || contracts.Count == 0 || !owner.Interfaces.HasValue) continue;
            var owned = contracts.Select(FaceKey).ToHashSet();
            foreach (var list in contracts.Where(contract => contract.Name == IList))
                owned.Add(FaceKey(new TypeNode.Fqn(ICollection, list.Args)));
            var faces = SupertypeGraph.Reachable(new SupertypeGraph.Def { Interfaces = definition.Interfaces }, definitions, refs)
                .Where(edge => edge.isInterface).Select(edge => CollectionFace(edge.spec))
                .OfType<TypeNode.Fqn>().Where(face => owned.Contains(FaceKey(face))).ToList();
            if (faces.Count == 0) continue;
            var inherited = definition.Base == null ? new HashSet<string>()
                : SupertypeGraph.Reachable(new SupertypeGraph.Def { Base = definition.Base }, definitions, refs)
                    .Where(edge => edge.isInterface).Select(edge => CollectionFace(edge.spec))
                    .OfType<TypeNode.Fqn>().Select(FaceKey).ToHashSet();
            var interfaces = owner.Interfaces.Value.ToList();
            var stated = interfaces.OfType<TypeNode.Fqn>().Select(CollectionFace).OfType<TypeNode.Fqn>().Select(FaceKey).ToHashSet();
            TypeNode collectionElement = null, listElement = null;
            foreach (var face in faces)
            {
                if (inherited.Contains(FaceKey(face))) continue;
                if (face.Name == IList) listElement ??= face.Args[0];
                else collectionElement ??= face.Args[0];
                if (stated.Add(FaceKey(face))) interfaces.Add(BirGraphValue.CopyType(face));
            }
            owner.Interfaces = interfaces.ToArray();
            if (collectionElement == null && listElement == null) continue;
            var methods = owner.Methods.OrEmpty().ToList();
            var appended = new List<BirFunctionDeclaration>();
            void AddSlot(BirFunctionDeclaration slot, string slotOwner, TypeNode element)
            {
                var name = slot.Name.Value;
                static string Key(TypeNode type) => SupertypeGraph.TypeKey(BirTypeLowering.CanonicalPhysicalSlotType(type));
                var signature = slot.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray();
                if (methods.Any(method => method.Name.OrDefault() == name && method.Vis.OrDefault() == "public"
                    && method.Virtual.OrDefault() && !method.Static.OrDefault() && method.TypeParams.OrEmpty().Length == 0
                    && method.Params.HasValue && method.Params.Value.Length == signature.Length
                    && method.Params.Value.Select(FunctionSignatureIdentity.SignatureType).Select(Key).SequenceEqual(signature.Select(Key))
                    && Key(method.Ret.OrDefault()) == Key(slot.Ret.Value))) return;
                // A same-name overload/non-virtual member is not a native slot.
                // Keep it and state an exact private MethodImpl without a duplicate MethodDef.
                if (methods.Any(method => method.Name.OrDefault() == name))
                {
                    var fresh = "dotkt$native$" + name;
                    while (methods.Any(method => method.Name.OrDefault() == fresh)) fresh += "$";
                    slot.Name = fresh; slot.Vis = "private"; slot.ClrFinal = true;
                    slot.ClrInterfaceImpls = new[] { new BirMethodImpl {
                        Owner = new TypeNode.Fqn(slotOwner, new[] { BirGraphValue.CopyType(element) }),
                        Member = name, Arity = 0, Params = signature,
                        Ret = BirGraphValue.CopyType(slot.Ret.Value),
                    } };
                }
                methods.Add(slot); appended.Add(slot);
            }
            if (collectionElement != null)
            {
                AddSlot(QuerySlotForwardSource("Contains", collectionElement, "System.Boolean", "KotlinCollectionDefaultSlots", "dotktContains"), ICollection, collectionElement);
                AddSlot(ConstBoolGetterSource("get_IsReadOnly"), ICollection, collectionElement);
                AddSlot(CopyToSource(collectionElement), ICollection, collectionElement);
            }
            if (listElement != null)
                AddSlot(QuerySlotForwardSource("IndexOf", listElement, "System.Int32", "KotlinListDefaultSlots", "dotktIndexOf"), IList, listElement);
            owner.Methods = methods.ToArray();
            if (appended.Count != 0) publishMethods(file, owner, appended);
        }
    }

    static BirFunctionDeclaration SourceMethod(string name, BirParameter[] parameters, TypeNode result, IBirStatement[] body) => new() {
        Name = name, Static = false, Override = false, Virtual = true, Abstract = false,
        ObjectOverride = false, Vis = "public", Params = parameters, Ret = result, Body = body,
    };

    // The semantic query slot owns its ordinary/storage boundary and already
    // selected Kotlin override; do not reconstruct a lowercase target here.
    static BirFunctionDeclaration QuerySlotForwardSource(string name, TypeNode element, string result, string slotOwner, string target)
        => SourceMethod(name, new[] { new BirParameter { Name = "element", Type = BirGraphValue.CopyType(element) } },
            new TypeNode.Fqn(result), new IBirStatement[] { new BirReturn {
                Value = OptionalField<IBirExpression>.Of(new BirCallInstance {
                    OwnerType = new TypeNode.Fqn("DotKt.Runtime.CompilerServices." + slotOwner), Virtual = true,
                    Recv = new BirThis(), Method = target, Sig = new TypeNode[] { new TypeNode.Fqn("System.Object") },
                    Ret = new TypeNode.Fqn(result), Args = new IBirExpression[] { new BirCast {
                        Type = new TypeNode.Fqn("System.Object"), E = new BirLocal { Name = "element" },
                    } },
                }),
            } });

    static BirFunctionDeclaration ConstBoolGetterSource(string name)
        => SourceMethod(name, Array.Empty<BirParameter>(), new TypeNode.Fqn("System.Boolean"),
            new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(new BirConstant {
                Type = new TypeNode.Fqn("System.Boolean"), Value = new BirLiteral.BooleanLiteral(false),
            }) } });

    // E is already an exact physical CLR element at this clock. Enumerate that
    // native face, disposing even when an array write throws. Every call returns
    // fresh authored declarations/operands; no source eligibility is captured.
    internal static BirFunctionDeclaration CopyToSource(TypeNode element)
    {
        TypeNode Copy(TypeNode type) => BirGraphValue.CopyType(type);
        var iterator = new TypeNode.Fqn("System.Collections.Generic.IEnumerator", new[] { Copy(element) });
        static BirLocal Local(string name) => new() { Name = name };
        static BirCallInstance Call(TypeNode owner, string method, IBirExpression receiver, TypeNode result) => new() {
            OwnerType = BirGraphValue.CopyType(owner), Method = method, Virtual = true, Recv = OptionalField<IBirExpression>.Of(receiver),
            Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = BirGraphValue.CopyType(result),
        };
        var loop = new BirWhile {
            Cond = OptionalField<IBirExpression>.Of(Call(new TypeNode.Fqn("System.Collections.IEnumerator"), "MoveNext", Local("it"), new TypeNode.Fqn("System.Boolean"))),
            Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(new BirArraySet {
                    Array = Local("array"), Index = Local("i"), Elem = Copy(element),
                    Value = OptionalField<IBirExpression>.Of(Call(iterator, "get_Current", Local("it"), element)),
                }) },
                new BirSetLocal { Name = "i", Value = OptionalField<IBirExpression>.Of(new BirBinaryOperation {
                    Op = "+", Lhs = Local("i"), Rhs = new BirConstant { Type = new TypeNode.Fqn("System.Int32"), Value = new BirLiteral.IntegerLiteral(1) },
                }) },
            },
        };
        return SourceMethod("CopyTo", new[] {
                new BirParameter { Name = "array", Type = new TypeNode.Array(Copy(element)) },
                new BirParameter { Name = "arrayIndex", Type = new TypeNode.Fqn("System.Int32") },
            }, new TypeNode.Fqn("void"), new IBirStatement[] {
                new BirVariable { Name = "it", Type = Copy(iterator), Init = OptionalField<IBirExpression>.Of(Call(
                    new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new[] { Copy(element) }), "GetEnumerator", new BirThis(), iterator)) },
                new BirVariable { Name = "i", Type = new TypeNode.Fqn("System.Int32"), Init = OptionalField<IBirExpression>.Of(Local("arrayIndex")) },
                new BirTry { Body = new IBirStatement[] { loop }, Catches = Array.Empty<BirCatch>(), Finally = new IBirStatement[] {
                    new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(Call(new TypeNode.Fqn("System.IDisposable"), "Dispose", Local("it"), new TypeNode.Fqn("void"))) },
                } },
            });
    }
}
