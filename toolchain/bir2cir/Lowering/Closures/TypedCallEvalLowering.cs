using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The owning realization of the call-evaluation plan. The ordering decision and
// ID allocator are shared with remaining DOM producers; graph operations are not.
static class TypedCallEvalLowering
{
    static BirGraphValue Graph(IBirExpression value) => value as BirGraphValue;
    static IBirExpression Expr(BirGraphValue value) => (IBirExpression)value;
    static OptionalField<IBirExpression> Field(IBirExpression value) => value == null
        ? OptionalField<IBirExpression>.Null : OptionalField<IBirExpression>.Of(value);

    // A physical mapping may have no value slot for a source receiver. Its
    // evaluation still precedes the new operands; only the shared unread-binding
    // rule may discard it. Do not reconstruct a field value type from its owner.
    internal static IBirExpression PreserveUnreadValueBefore(IBirExpression value,
        IBirExpression lowered, string context)
    {
        if (value == null) return lowered;
        var type = StaticTypeOf(Graph(value));
        if (type == null && value is BirStaticFieldGet)
            throw new InvalidOperationException(
                $"bir2cir: cannot type the static-field value whose evaluation must be preserved before {context}; "
                + "its producer must carry the field's exact node-local result type.");
        var binding = new BirCallBinding {
            Id = CallEvalLowering.FreshBindingId(), Expr = Field(Expr(Graph(value).DeepClone())),
            Stable = ValueStability.IsReReadable((Graph(value) as BirGraphNode)?.Tag,
                value is BirEnumValue { Underlying.HasValue: true, PhysicalValue.HasValue: true }),
        };
        if (type != null) binding.Type = type;
        var (statements, replacements) = Materialise(new[] { binding }, new[] { Graph(lowered) }, context);
        var result = Expr(Substitute(Graph(lowered), replacements));
        return statements.Length == 0 ? result : new BirValueBlock { Stmts = statements, Result = Field(result) };
    }

    internal static T Apply<T>(T root, ValueTypeOracle isValue) where T : BirGraphValue
    {
        var lowered = (T)Walk(root, isValue ?? (_ => false));
        lowered.Visit(node => {
            if (node is BirCallEvaluation or BirBindingReference
                || node is BirConstructor { DelegationBindings.IsPresent: true })
                throw new InvalidOperationException("A call-evaluation plan survived owning CallEvalLowering");
        });
        return lowered;
    }

    static BirGraphValue Walk(BirGraphValue node, ValueTypeOracle isValue)
    {
        node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value, isValue)));
        if (node is BirCallEvaluation plan)
        {
            var call = Graph(plan.Expr.Value);
            var (statements, replacements) = Materialise(plan.Bindings.OrEmpty(), new[] { call }, "a call", isValue: isValue);
            var value = Substitute(call, replacements);
            node = statements.Length == 0 ? value : new BirValueBlock {
                Type = plan.Type, Stmts = statements, Result = Field(Expr(value)),
            };
        }
        else if (node is BirConstructor { DelegationBindings.HasValue: true } constructor)
        {
            var readers = constructor.ThisArgs.OrEmpty().Concat(constructor.BaseArgs.OrEmpty()).Select(Graph).ToArray();
            var (statements, replacements) = Materialise(constructor.DelegationBindings.Value,
                readers, "a constructor delegation", isValue: isValue);
            if (constructor.ThisArgs.HasValue)
                constructor.ThisArgs = constructor.ThisArgs.Value.Select(value => Expr(Substitute(Graph(value), replacements))).ToArray();
            if (constructor.BaseArgs.HasValue)
                constructor.BaseArgs = constructor.BaseArgs.Value.Select(value => Expr(Substitute(Graph(value), replacements))).ToArray();
            constructor.DelegationBindings = default;
            if (statements.Length != 0) constructor.PreStmts = statements;
        }
        if (node is BirValueBlock block) MergeNestedResult(block);
        return node;
    }

    static void MergeNestedResult(BirValueBlock block)
    {
        while (block.Result.OrDefault() is BirValueBlock inner)
        {
            block.Stmts = block.Stmts.OrEmpty().Concat(inner.Stmts.OrEmpty()).ToArray();
            if (!block.Type.HasValue) block.Type = inner.Type;
            block.Result = inner.Result;
        }
    }

    internal static (IBirStatement[] Stmts, Dictionary<string, IBirExpression> Repl) Materialise(
        BirCallBinding[] bindings, IReadOnlyList<BirGraphValue> readers, string site,
        ValueTypeOracle isValue = null)
        => MaterialiseCore(bindings, readers, site, isValue, null, owned: false);

    // The source planner owns these occurrences. Moving an operand into a local
    // must not clone a registered declaration nested inside that operand.
    internal static (IBirStatement[] Stmts, Dictionary<string, IBirExpression> Repl) MaterialiseOwned(
        BirCallBinding[] bindings, IReadOnlyList<BirGraphValue> readers, string site,
        bool[] force, ValueTypeOracle isValue)
        => MaterialiseCore(bindings, readers, site, isValue, force, owned: true);

    static (IBirStatement[] Stmts, Dictionary<string, IBirExpression> Repl) MaterialiseCore(
        BirCallBinding[] bindings, IReadOnlyList<BirGraphValue> readers, string site,
        ValueTypeOracle isValue, bool[] force, bool owned)
    {
        isValue ??= _ => false;
        var ids = bindings.Select(binding => binding.Id.Value).ToArray();
        var kinds = bindings.Select(binding => binding.Kind.OrDefault() ?? "value").ToArray();
        var stable = bindings.Select(binding => binding.Stable.OrDefault()).ToArray();
        var reads = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        var rank = 0;
        foreach (var reader in readers) ScanReads(reader, reads, positions, deferred, ref rank, true, owned);
        foreach (var binding in bindings) CountReads(Graph(binding.Expr.Value), reads, owned);
        var drop = bindings.Select((binding, index) => reads[ids[index]] == 0
            && ValueStability.IsDroppable((Graph(binding.Expr.Value) as BirGraphNode)?.Tag)).ToArray();
        if (force != null && force.Length != bindings.Length)
            throw new InvalidOperationException("A call-plan forcing vector does not match its bindings");
        // An owning non-re-readable occurrence cannot be copied to several
        // readers, even when a caller supplies the ordering decision.
        if (owned && force != null)
            force = force.Select((forced, index) => forced || reads[ids[index]] > 1).ToArray();
        var variables = CallEvalLowering.DecideMaterialisation(ids, kinds, stable, drop, reads, positions, deferred,
            index => LocationHasPinWork(bindings[index].Expr.Value, isValue), force);
        var statements = new List<IBirStatement>();
        var replacements = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
        for (var index = 0; index < bindings.Length; index++)
        {
            var binding = bindings[index];
            var value = Expr(owned ? SubstituteOwned(Graph(binding.Expr.Value), replacements)
                : Substitute(Graph(binding.Expr.Value), replacements));
            if (drop[index]) { replacements[ids[index]] = null; continue; }
            if (kinds[index] == "address")
            {
                if (IsLvalueFormer(value))
                {
                    PinLocationOperands(value, statements, isValue);
                    replacements[ids[index]] = value;
                }
                else
                {
                    var type = StaticTypeOf(Graph(value)) ?? throw new InvalidOperationException(
                        $"Cannot type the address evaluation at {site}");
                    var name = CallEvalLowering.FreshBindingId();
                    IBirExpression initializer = type is TypeNode.ByRef
                        ? new BirByRefOf { Inner = Field(value) } : value;
                    statements.Add(new BirVariable { Name = name, Type = type, Init = Field(initializer) });
                    replacements[ids[index]] = new BirLocal { Name = name, Sty = type };
                }
                continue;
            }
            if (!variables[index]) { replacements[ids[index]] = value; continue; }
            var declared = binding.Type.OrDefault();
            var valueType = declared != null && !CallEvalLowering.IsOpen(declared) ? declared
                : Graph(binding.Expr.Value).CallBindingTypeHint ?? declared;
            var local = CallEvalLowering.FreshBindingId();
            statements.Add(new BirVariable {
                Name = local, Type = valueType is null ? OptionalField<TypeNode>.Null : OptionalField<TypeNode>.Of(valueType),
                Init = Field(value), _callEvalTemporary = true, Role = binding.Role,
            });
            var read = new BirLocal { Name = local };
            if (valueType != null) read.Sty = valueType;
            replacements[ids[index]] = read;
        }
        return (statements.ToArray(), replacements);
    }

    static void PreservePointerReads(BirGraphValue node, IReadOnlyDictionary<string, IBirExpression> replacements)
    {
        // An address binding names a location in a ref argument slot. A pointer
        // operand reads its address instead, never the value stored there.
        IBirExpression Pointer(IBirExpression value) => value is BirBindingReference reference
            && SignatureValueTypes.Of(reference.Sty.OrDefault()) is TypeNode.ByRef
            && replacements.TryGetValue(reference.Id.Value, out var location)
            && location != null && location is not BirByRefOf
                ? new BirByRefOf { Inner = Field(value) } : value;
        if (node is BirByRefLoad { Ptr.HasValue: true } load) load.Ptr = Field(Pointer(load.Ptr.Value));
        if (node is BirByRefStore { Ptr.HasValue: true } store) store.Ptr = Field(Pointer(store.Ptr.Value));
    }

    // One independent materialization per occurrence, including repeated stable
    // reads. No ownership is transferred out of the immutable reference cache.
    internal static BirGraphValue Substitute(BirGraphValue node, IReadOnlyDictionary<string, IBirExpression> replacements)
    {
        if (node == null) return null;
        BirGraphValue Replace(BirGraphValue current)
        {
            if (current is BirBindingReference reference && replacements.TryGetValue(reference.Id.Value, out var value))
                return value == null ? null : Graph(value).DeepClone();
            PreservePointerReads(current, replacements);
            current.VisitChildSlots(slot => slot.Replace(Replace(slot.Value)));
            return current;
        }
        return Replace(node.DeepClone());
    }

    // A single reader receives the original occurrence. Repeated local/literal
    // reads receive independent leaves; repeated evaluations must have been
    // materialized by the shared decision above rather than cloning a subtree.
    internal static BirGraphValue SubstituteOwned(BirGraphValue node,
        IReadOnlyDictionary<string, IBirExpression> replacements)
    {
        if (node == null || replacements.Count == 0) return node;
        var counts = replacements.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        CountReads(node, counts, owned: true);
        foreach (var (id, count) in counts)
            if (count > 1 && replacements[id] is { } value && !IndependentRead(value))
                throw new InvalidOperationException("An owning call-plan evaluation has multiple unmaterialized readers");
        var moved = new HashSet<string>(StringComparer.Ordinal);
        BirGraphValue Replace(BirGraphValue current)
        {
            if (current is IBirExecutablePayload) return current;
            if (current is BirBindingReference reference && replacements.TryGetValue(reference.Id.Value, out var value))
                return value == null ? null : moved.Add(reference.Id.Value) ? Graph(value) : Graph(value).DeepClone();
            PreservePointerReads(current, replacements);
            foreach (var slot in OrderedChildren(current, owned: true)) slot.Replace(Replace(slot.Value));
            return current;
        }
        return Replace(node);
    }

    static bool IndependentRead(IBirExpression value) => value is BirLocal
        || ValueStability.IsReReadable((Graph(value) as BirGraphNode)?.Tag,
            value is BirEnumValue { Underlying.HasValue: true, PhysicalValue.HasValue: true });

    internal static TypeNode StaticTypeOf(BirGraphValue value) => BirNodeType.Of(value as BirGraphNode,
        node => StaticTypeOf(node), name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var element) ? element : null);

    internal static bool IsLvalueFormer(IBirExpression value) => CallEvalLowering.IsLvalueFormer(Graph(value));

    static bool StaysWithoutPin(IBirExpression value)
        => CallEvalLowering.StaysWithoutPin((Graph(value) as BirGraphNode)?.Tag);

    static bool IsStoragePathOperand(IBirExpression location, BirGraphSlot slot, ValueTypeOracle isValue)
    {
        var (owner, receiver) = location switch {
            BirFieldGet field => (field.OwnerType.OrDefault(), field.Recv.OrDefault()),
            BirPropertyGet field => (field.Type.OrDefault(), field.Recv.OrDefault()),
            _ => ((TypeNode)null, (IBirExpression)null),
        };
        return CallEvalLowering.IsStoragePathOperand((Graph(location) as BirGraphNode)?.Tag,
            ReferenceEquals(slot.Value, Graph(receiver)), IsLvalueFormer(receiver), owner, isValue);
    }

    static IEnumerable<BirGraphSlot> LocationOperands(IBirExpression location)
    {
        var slots = new List<BirGraphSlot>();
        Graph(location).VisitChildSlots(slot => { if (slot.Value is IBirExpression) slots.Add(slot); });
        return slots;
    }

    static bool LocationHasPinWork(IBirExpression location, ValueTypeOracle isValue)
    {
        if (!IsLvalueFormer(location)) return true;
        foreach (var slot in LocationOperands(location))
        {
            if (IsStoragePathOperand(location, slot, isValue))
            {
                if (LocationHasPinWork(Expr(slot.Value), isValue)) return true;
            }
            else if (!StaysWithoutPin(Expr(slot.Value))) return true;
        }
        return false;
    }

    internal static bool IsPinnedLocation(IBirExpression location, Func<IBirExpression, bool> settled,
        ValueTypeOracle isValue)
    {
        if (!IsLvalueFormer(location)) return settled(location);
        foreach (var slot in LocationOperands(location))
            if (IsStoragePathOperand(location, slot, isValue)
                ? !IsPinnedLocation(Expr(slot.Value), settled, isValue)
                : !StaysWithoutPin(Expr(slot.Value)) && !settled(Expr(slot.Value))) return false;
        return true;
    }

    static void PinLocationOperands(IBirExpression location, List<IBirStatement> statements, ValueTypeOracle isValue)
    {
        foreach (var slot in LocationOperands(location))
        {
            if (IsStoragePathOperand(location, slot, isValue)) PinLocationOperands(Expr(slot.Value), statements, isValue);
            else if (!StaysWithoutPin(Expr(slot.Value)))
            {
                var value = slot.Value;
                var type = StaticTypeOf(value) ?? throw new InvalidOperationException("Cannot type an address-location operand");
                var name = CallEvalLowering.FreshBindingId();
                statements.Add(new BirVariable { Name = name, Type = type, Init = Field(Expr(value)) });
                slot.Replace(new BirLocal { Name = name, Sty = type });
            }
        }
    }

    static void CountReads(BirGraphValue node, Dictionary<string, int> counts, bool owned = false)
    {
        if (node == null || owned && node is IBirExecutablePayload) return;
        if (node is BirBindingReference reference && counts.ContainsKey(reference.Id.Value)) counts[reference.Id.Value]++;
        foreach (var slot in OrderedChildren(node, owned)) CountReads(slot.Value, counts, owned);
    }

    static IEnumerable<BirGraphSlot> OrderedChildren(BirGraphValue node, bool owned = false)
    {
        var slots = new List<BirGraphSlot>();
        if (owned)
        {
            if (node is IBirExecutablePayload) return slots;
            var currentFrame = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
            node.VisitCurrentFrameSlots(slot => currentFrame.Add(slot.Value));
            node.VisitOwnChildSlots((_, role, slot) => {
                if (currentFrame.Contains(slot.Value) && role is not (BirGenericFrameRole.ReferencedDeclaration
                    or BirGenericFrameRole.ResolvedDeclaration or BirGenericFrameRole.OverrideDeclaration
                    or BirGenericFrameRole.SharedCaptureDeclaration or BirGenericFrameRole.LocalDeclaration
                    or BirGenericFrameRole.SyntheticDeclaration or BirGenericFrameRole.FrozenSourceFact
                    or BirGenericFrameRole.StorageSourceType)) slots.Add(slot);
            });
        }
        else node.VisitChildSlots(slots.Add);
        // callInline's argument vector is ordered before its receiver descriptor
        // by the current call-plan contract. It disappears during inline splicing;
        // retaining the explicit role here also covers plans authored by lowerings.
        if (node is BirCallInline inline && inline.Args.HasValue)
        {
            var ordered = new List<BirGraphSlot>();
            foreach (var argument in inline.Args.Value)
            {
                var index = slots.FindIndex(slot => ReferenceEquals(slot.Value, Graph(argument)));
                if (index >= 0) { ordered.Add(slots[index]); slots.RemoveAt(index); }
            }
            ordered.AddRange(slots);
            return ordered;
        }
        return slots;
    }

    static void ScanReads(BirGraphValue node, Dictionary<string, int> counts, Dictionary<string, int> positions,
        HashSet<string> deferred, ref int rank, bool eager, bool owned = false)
    {
        if (node == null || owned && node is IBirExecutablePayload) return;
        if (node is BirBindingReference reference && counts.ContainsKey(reference.Id.Value))
        {
            counts[reference.Id.Value]++;
            positions.TryAdd(reference.Id.Value, rank);
            if (!eager) deferred.Add(reference.Id.Value);
        }
        rank++;
        var eagerOperands = node is BirGraphNode expression && CallEvalLowering.IsEagerKind(expression.Tag);
        foreach (var slot in OrderedChildren(node, owned))
            ScanReads(slot.Value, counts, positions, deferred, ref rank, eager && eagerOperands, owned);
    }

    internal static void SelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        BirBindingReference Read(string id) => new() { Id = id };
        BirCallStatic Effect(string method) => new() { Method = method, Ret = integer, Args = Array.Empty<IBirExpression>() };
        BirCallBinding Binding(string id, IBirExpression expression, string kind = "value") => new() {
            Id = id, Kind = kind, Type = integer, Stable = false, Expr = Field(expression),
        };
        var call = new BirCallStatic { Method = "target", Args = new IBirExpression[] { Read("default"), Read("supplied") } };
        var (statements, replacements) = Materialise(new[] {
            Binding("supplied", Effect("supplied")), Binding("default", Effect("default")),
        }, new BirGraphValue[] { call }, "reordered default");
        var replaced = (BirCallStatic)Substitute(call, replacements);
        if (statements.Length != 1 || ((BirVariable)statements[0]).Init.Value is not BirCallStatic { Method.Value: "supplied" }
            || replaced.Args.Value[0] is not BirCallStatic { Method.Value: "default" }
            || replaced.Args.Value[1] is not BirLocal)
            throw new InvalidOperationException("Owning call plan changed supplied/default evaluation order");

        var location = new BirArrayGet { Elem = integer,
            Array = new BirCallStatic { Method = "array", Ret = new TypeNode.Array(integer), Args = Array.Empty<IBirExpression>() },
            Index = Effect("index"),
        };
        var readers = new BirCallStatic { Method = "consume", Args = new IBirExpression[] { Read("value"), Read("address") } };
        (statements, replacements) = Materialise(new[] { Binding("value", Effect("first")), Binding("address", location, "address") },
            new BirGraphValue[] { readers }, "address pin order");
        if (statements.Length != 3 || statements.Cast<BirVariable>().Select(variable => ((BirCallStatic)variable.Init.Value).Method.Value)
                .SequenceEqual(new[] { "first", "array", "index" }) != true
            || replacements["address"] is not BirArrayGet { Array.Value: BirLocal, Index.Value: BirLocal })
            throw new InvalidOperationException("Address pins overtook an earlier call value");

        var literal = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(7) };
        var ignored = new[] { Binding("ignored", literal), Binding("effect", Effect("effect")) };
        (statements, replacements) = Materialise(ignored, Array.Empty<BirGraphValue>(), "unread values");
        if (statements.Length != 1 || ((BirVariable)statements[0]).Init.Value is not BirCallStatic { Method.Value: "effect" })
            throw new InvalidOperationException("Owning call plan dropped an unread effect or retained an unread literal");

        var stable = Binding("stable", literal);
        stable.Stable = true;
        var twice = new BirCallStatic { Method = "two", Args = new IBirExpression[] { Read("stable"), Read("stable") } };
        (statements, replacements) = Materialise(new[] { stable }, new BirGraphValue[] { twice }, "independent stable reads");
        var cloned = (BirCallStatic)Substitute(twice, replacements);
        if (statements.Length != 0 || ReferenceEquals(cloned.Args.Value[0], cloned.Args.Value[1]))
            throw new InvalidOperationException("Owning call-plan stable readers share mutable nodes");

        var referenceType = new TypeNode.ByRef(integer);
        BirBindingReference ReferenceRead() => new() { Id = "address", Sty = referenceType };
        var addressBinding = Binding("address", new BirLocal { Name = "slot", Sty = integer }, "address");
        addressBinding.Type = referenceType;
        addressBinding.Stable = true;
        var defaultRead = new BirByRefLoad { Ptr = ReferenceRead(), Elem = integer };
        var refCall = new BirCallStatic { Method = "readDefault", Sig = new TypeNode[] { referenceType, integer },
            Args = new IBirExpression[] { ReferenceRead(), Read("default") } };
        (statements, replacements) = Materialise(new[] { addressBinding, Binding("default", defaultRead) },
            new BirGraphValue[] { refCall }, "default managed-reference read");
        var referenceCall = (BirCallStatic)Substitute(refCall, replacements);
        if (referenceCall.Args.Value[0] is not BirLocal { Name.Value: "slot" }
            || referenceCall.Args.Value[1] is not BirByRefLoad { Ptr.Value: BirByRefOf { Inner.Value: BirLocal { Name.Value: "slot" } } })
            throw new InvalidOperationException("Default reference read consumed a pointee value instead of its address");
        var store = new BirByRefStore { Ptr = ReferenceRead(), Elem = integer, Value = literal };
        var stored = (BirByRefStore)SubstituteOwned(store,
            new Dictionary<string, IBirExpression> { ["address"] = new BirLocal { Name = "slot", Sty = integer } });
        if (stored.Ptr.Value is not BirByRefOf { Inner.Value: BirLocal { Name.Value: "slot" } })
            throw new InvalidOperationException("Owning pointer substitution consumed a pointee instead of its address");
        var alreadyAddressed = (BirByRefLoad)Substitute(defaultRead,
            new Dictionary<string, IBirExpression> { ["address"] = new BirByRefOf { Inner = new BirLocal { Name = "slot", Sty = integer } } });
        if (alreadyAddressed.Ptr.Value is not BirByRefOf { Inner.Value: BirLocal { Name.Value: "slot" } })
            throw new InvalidOperationException("Pointer substitution addressed an already explicit pointer twice");

        var original = new BirCallEvaluation { Type = integer, Bindings = new[] { Binding("producer", Effect("once")) },
            Expr = new BirCallStatic { Method = "invoke", Ret = integer, Args = new IBirExpression[] { Read("producer") } } };
        var first = Apply(original.DeepClone(), _ => false);
        var second = Apply(original.DeepClone(), _ => false);
        if (ReferenceEquals(first, second) || first is BirCallEvaluation || second is BirCallEvaluation
            || original.Bindings.Value[0].Id.Value != "producer"
            || ((BirBindingReference)((BirCallStatic)original.Expr.Value).Args.Value[0]).Id.Value != "producer")
            throw new InvalidOperationException("Owning call-plan application mutated its source or retained a plan wrapper");
        Console.WriteLine("[owning call evaluation] self-test OK (order, address pins, unread effects, independent reads, source isolation)");
    }
}
