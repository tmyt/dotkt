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
    static IBirExpression Copy(IBirExpression value) => value == null ? null : Expr(Graph(value).DeepClone());

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
        bool[] force = null, ValueTypeOracle isValue = null)
    {
        isValue ??= _ => false;
        var ids = bindings.Select(binding => binding.Id.Value).ToArray();
        var kinds = bindings.Select(binding => binding.Kind.OrDefault() ?? "value").ToArray();
        var stable = bindings.Select(binding => binding.Stable.OrDefault()).ToArray();
        var reads = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        var rank = 0;
        foreach (var reader in readers) ScanReads(reader, reads, positions, deferred, ref rank, true);
        foreach (var binding in bindings) CountReads(Graph(binding.Expr.Value), reads);
        var drop = bindings.Select((binding, index) => reads[ids[index]] == 0
            && ValueStability.IsDroppable((Graph(binding.Expr.Value) as BirGraphNode)?.Tag)).ToArray();
        var variables = CallEvalLowering.DecideMaterialisation(ids, kinds, stable, drop, reads, positions, deferred,
            index => LocationHasPinWork(bindings[index].Expr.Value, isValue), force);
        var statements = new List<IBirStatement>();
        var replacements = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
        for (var index = 0; index < bindings.Length; index++)
        {
            var binding = bindings[index];
            var value = Expr(Substitute(Graph(binding.Expr.Value), replacements));
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

    // One independent materialization per occurrence, including repeated stable
    // reads. No ownership is transferred out of the immutable reference cache.
    internal static BirGraphValue Substitute(BirGraphValue node, IReadOnlyDictionary<string, IBirExpression> replacements)
    {
        if (node == null) return null;
        BirGraphValue Replace(BirGraphValue current)
        {
            if (current is BirBindingReference reference && replacements.TryGetValue(reference.Id.Value, out var value))
                return value == null ? null : Graph(value).DeepClone();
            current.VisitChildSlots(slot => slot.Replace(Replace(slot.Value)));
            return current;
        }
        return Replace(node.DeepClone());
    }

    internal static void FreshenPlanIds(BirGraphValue root)
    {
        void Rename(BirGraphValue host, BirCallBinding[] bindings)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                var old = binding.Id.Value;
                var fresh = CallEvalLowering.FreshBindingId();
                map[old] = fresh;
                binding.Id = fresh;
            }
            host.Visit(node => {
                if (node is BirBindingReference reference && map.TryGetValue(reference.Id.Value, out var fresh)) reference.Id = fresh;
            });
        }
        void WalkPlan(BirGraphValue node)
        {
            if (node is BirCallEvaluation plan) Rename(plan, plan.Bindings.OrEmpty());
            else if (node is BirConstructor { DelegationBindings.HasValue: true } constructor)
                Rename(constructor, constructor.DelegationBindings.Value);
            node.VisitChildren(WalkPlan);
        }
        WalkPlan(root);
    }

    internal static TypeNode StaticTypeOf(BirGraphValue value) => BirNodeType.Of(value as BirGraphNode,
        node => StaticTypeOf(node), name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var element) ? element : null);

    internal static IBirExpression PreserveUnreadValueBefore(IBirExpression value, IBirExpression lowered, string site)
    {
        if (value == null) return lowered;
        var type = StaticTypeOf(Graph(value));
        if (type == null && value is BirStaticFieldGet)
            throw new InvalidOperationException("A static-field value whose evaluation is retained has no exact result type");
        var binding = new BirCallBinding {
            Id = CallEvalLowering.FreshBindingId(), Expr = Field(Copy(value)),
            Stable = ValueStability.IsReReadable((Graph(value) as BirGraphNode)?.Tag,
                value is BirEnumValue { Underlying.HasValue: true, PhysicalValue.HasValue: true }),
        };
        if (type != null) binding.Type = type;
        var (statements, replacements) = Materialise(new[] { binding }, new[] { Graph(lowered) }, site);
        var result = Expr(Substitute(Graph(lowered), replacements));
        return statements.Length == 0 ? result : new BirValueBlock { Stmts = statements, Result = Field(result) };
    }

    internal static bool IsLvalueFormer(IBirExpression value) => value switch {
        BirLocal or BirThis or BirStaticFieldGet or BirArrayGet or BirStackGet or BirByRefLoad => true,
        BirFieldGet field => field.Member.OrDefault() != "accessor",
        BirPropertyGet field => field.Member.OrDefault() == "field",
        _ => false,
    };

    static bool StaysWithoutPin(IBirExpression value) => value is BirConstant or BirThis or BirLocal
        or BirBindingReference or BirDefaultValue or BirClassReference or BirEnumValue;

    static bool IsStoragePathOperand(IBirExpression location, BirGraphSlot slot, ValueTypeOracle isValue)
    {
        var (owner, receiver) = location switch {
            BirFieldGet field => (field.OwnerType.OrDefault(), field.Recv.OrDefault()),
            BirPropertyGet field => (field.Type.OrDefault(), field.Recv.OrDefault()),
            _ => ((TypeNode)null, (IBirExpression)null),
        };
        var valueOwner = owner switch {
            TypeNode.Fqn named => isValue(named), TypeNode.Nullable => true,
            TypeNode.Oblivious oblivious => oblivious.Of is TypeNode.Nullable
                || oblivious.Of is TypeNode.Fqn named && isValue(named),
            _ => false,
        };
        return valueOwner && ReferenceEquals(slot.Value, Graph(receiver)) && IsLvalueFormer(receiver);
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

    static void CountReads(BirGraphValue node, Dictionary<string, int> counts) => node.Visit(value => {
        if (value is BirBindingReference reference && counts.ContainsKey(reference.Id.Value)) counts[reference.Id.Value]++;
    });

    static IEnumerable<BirGraphSlot> OrderedChildren(BirGraphValue node)
    {
        var slots = new List<BirGraphSlot>();
        node.VisitChildSlots(slots.Add);
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
        HashSet<string> deferred, ref int rank, bool eager)
    {
        if (node == null) return;
        if (node is BirBindingReference reference && counts.ContainsKey(reference.Id.Value))
        {
            counts[reference.Id.Value]++;
            positions.TryAdd(reference.Id.Value, rank);
            if (!eager) deferred.Add(reference.Id.Value);
        }
        rank++;
        var eagerOperands = node is BirGraphNode expression && CallEvalLowering.IsEagerKind(expression.Tag);
        foreach (var slot in OrderedChildren(node))
            ScanReads(slot.Value, counts, positions, deferred, ref rank, eager && eagerOperands);
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
        (statements, replacements) = Materialise(ignored, Array.Empty<BirGraphValue>(), "unread values", force: new[] { false, false });
        if (statements.Length != 1 || ((BirVariable)statements[0]).Init.Value is not BirCallStatic { Method.Value: "effect" })
            throw new InvalidOperationException("Owning call plan dropped an unread effect or retained an unread literal");

        var stable = Binding("stable", literal);
        stable.Stable = true;
        var twice = new BirCallStatic { Method = "two", Args = new IBirExpression[] { Read("stable"), Read("stable") } };
        (statements, replacements) = Materialise(new[] { stable }, new BirGraphValue[] { twice }, "independent stable reads");
        var cloned = (BirCallStatic)Substitute(twice, replacements);
        if (statements.Length != 0 || ReferenceEquals(cloned.Args.Value[0], cloned.Args.Value[1]))
            throw new InvalidOperationException("Owning call-plan stable readers share mutable nodes");

        var original = new BirCallEvaluation { Type = integer, Bindings = new[] { Binding("producer", Effect("once")) },
            Expr = new BirCallStatic { Method = "invoke", Ret = integer, Args = new IBirExpression[] { Read("producer") } } };
        var first = (BirCallEvaluation)original.DeepClone();
        var second = (BirCallEvaluation)original.DeepClone();
        FreshenPlanIds(first); FreshenPlanIds(second);
        if (first.Bindings.Value[0].Id.Value == second.Bindings.Value[0].Id.Value
            || ((BirBindingReference)((BirCallStatic)first.Expr.Value).Args.Value[0]).Id.Value != first.Bindings.Value[0].Id.Value
            || original.Bindings.Value[0].Id.Value != "producer")
            throw new InvalidOperationException("Owning call-plan freshening lost declaration/read edges or source independence");
        _ = Apply<BirGraphValue>(first, _ => false);
        Console.WriteLine("[owning call evaluation] self-test OK (order, address pins, unread effects, independent reads, fresh ids)");
    }
}
