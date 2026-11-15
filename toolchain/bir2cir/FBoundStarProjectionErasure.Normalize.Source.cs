using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void NormalizeInnerFactoryLocalsSource(BirGraphValue declaration, SourceRewriteState state)
    {
        var body = SourceCallableBody(declaration);
        if (!body.HasValue || body.Value.Length == 0) return;

        bool Matches(TypeNode logical, TypeNode physical)
        {
            if (logical is TypeNode.Array logicalArray && physical is TypeNode.Array physicalArray)
                return Matches(logicalArray.Elem is TypeNode.Projection projection ? projection.Of : logicalArray.Elem,
                    physicalArray.Elem);
            return StripProjectionShell(logical) is TypeNode.Fqn source
                && StripProjectionShell(physical) is TypeNode.Fqn target
                && (state.Owners.TryGetValue(source.Name, out var local) && local.ErasedName == target.Name
                    || state.References.TryExistentialPhysicalOwner(source.Name, out var referenced) && referenced == target.Name);
        }
        bool TryCarrier(TypeNode type, out TypeNode.Fqn result)
        {
            result = StripProjectionShell(type) as TypeNode.Fqn;
            if (result != null && (state.Carriers.Contains(result.Name)
                    || state.References.IsExistentialPhysicalOwner(result.Name))) return true;
            result = null;
            return false;
        }
        TypeNode Physical(TypeNode type)
        {
            if (TryCarrier(type, out var carrier)) return carrier;
            return type is TypeNode.Array array && TryCarrier(array.Elem, out var element)
                ? new TypeNode.Array(element, array.Rank, array.SzArray) : null;
        }

        Dictionary<string, TypeNode> locals = null;
        Dictionary<string, TypeNode.Fqn> projectedLocals = null;
        Dictionary<string, TypeNode> projectedArrays = null;
        var pending = new List<BirGraphSlot>();
        void Collect(BirGraphSlot slot)
        {
            var node = slot.Value;
            if (node is IBirExecutablePayload) return;
            if (node is BirVariable variable && variable.Name.HasValue && variable.Type.HasValue
                && Physical(SourceCarrierExpressionType(variable.Init.OrDefault() as BirGraphValue)) is TypeNode result)
            {
                var projected = state.ProjectedType(variable.Init.OrDefault() as BirGraphValue);
                var arrayProjection = state.ArrayProjection(variable.Init.OrDefault() as BirGraphValue);
                if (Matches(variable.Type.Value, result) || projected != null || arrayProjection != null)
                {
                    (locals ??= new(StringComparer.Ordinal))[variable.Name.Value] = result;
                    if (projected != null) (projectedLocals ??= new(StringComparer.Ordinal))[variable.Name.Value] = projected;
                    if (arrayProjection != null) (projectedArrays ??= new(StringComparer.Ordinal))[variable.Name.Value] = arrayProjection;
                }
            }
            node.VisitChildSlots(Collect);
            // Preserve the original post-order worklist. Replacement children
            // are visited only when the next fixed-point iteration rebuilds it.
            if (node is BirVariable or BirLocal or BirCast or BirInstanceOrNull or BirArrayGet
                or BirCallStatic or BirCallInstance or BirNewBoundDelegate
                or BirFieldGet or BirLateinitGet or BirFieldSetExpression or BirSetField) pending.Add(slot);
        }
        void Normalize(BirGraphSlot slot)
        {
            var node = slot.Value;
            var name = node switch { BirVariable variable => variable.Name.OrDefault(), BirLocal local => local.Name.OrDefault(), _ => null };
            if (name != null && locals?.TryGetValue(name, out var carrier) == true)
            {
                if (node is BirVariable variable) variable.Type = BirGraphValue.CopyType(carrier);
                else if (node is BirLocal local)
                {
                    local.Sty = BirGraphValue.CopyType(carrier);
                    if (projectedLocals?.TryGetValue(name, out var projected) == true) state.ProjectionFact(local, projected);
                    if (projectedArrays?.TryGetValue(name, out var arrayProjection) == true) state.ArrayProjection(local, arrayProjection);
                }
            }
            var (target, operand, exact) = node switch {
                BirCast cast => (cast.Type.OrDefault(), cast.E.OrDefault(), cast._exactBridgeCast.OrDefault()),
                BirInstanceOrNull test => (test.Type.OrDefault(), test.E.OrDefault(), false),
                _ => ((TypeNode)null, (IBirExpression)null, false),
            };
            if (!exact && StripSourceNullability(target) is TypeNode.Fqn { Args: { } } castType
                && TryCarrier(SourceCarrierExpressionType(operand as BirGraphValue), out var operandCarrier)
                && state.Projection.TryExistentialCarrier(castType.Name, out var castCarrier)
                && operandCarrier.Name == castCarrier)
            {
                var physical = ReplaceProjectionCore(target, castType, new TypeNode.Fqn(castCarrier));
                if (node is BirCast cast)
                {
                    cast.Type = BirGraphValue.CopyType(physical);
                    if (cast.Sty.HasValue) cast.Sty = BirGraphValue.CopyType(physical);
                }
                else ((BirInstanceOrNull)node).Type = BirGraphValue.CopyType(physical);
            }
            slot.Replace(BindSourceNormalizedNode(node, state));
        }
        while (true)
        {
            var priorLocals = locals?.Count ?? 0;
            var priorProjections = projectedLocals?.Count ?? 0;
            pending.Clear();
            for (var index = 0; index < body.Value.Length; index++)
            {
                var at = index;
                Collect(new BirGraphSlot(() => (BirGraphValue)body.Value[at], value => body.Value[at] = (IBirStatement)value));
            }
            foreach (var slot in pending) Normalize(slot);
            if ((locals?.Count ?? 0) == priorLocals && (projectedLocals?.Count ?? 0) == priorProjections) break;
        }

        bool CanNormalizeReturn(TypeNode type) => type is TypeNode.Array array
            ? CanNormalizeReturn(array.Elem is TypeNode.Projection projection ? projection.Of : array.Elem)
            : StripProjectionShell(type) is TypeNode.Fqn named
                && (state.Owners.ContainsKey(named.Name) || state.References.TryExistentialPhysicalOwner(named.Name, out _));
        if (declaration is not BirFunctionDeclaration method || !method.Ret.HasValue
            || SourceNativeSignature(method, state) || !CanNormalizeReturn(method.Ret.Value)) return;

        TypeNode Result(BirGraphValue expression)
        {
            if (Physical(SourceCarrierExpressionType(expression)) is TypeNode direct) return direct;
            if (expression is BirLocal local && local.Name.HasValue && locals?.TryGetValue(local.Name.Value, out var carrier) == true)
                return carrier;
            if (expression is BirValueBlock block) return Result(block.Result.OrDefault() as BirGraphValue);
            if (expression is BirConditional conditional)
            {
                var left = Result(conditional.Then.OrDefault() as BirGraphValue);
                var right = Result(conditional.Else.OrDefault() as BirGraphValue);
                if (left != null && (conditional.Else.OrDefault() is BirThrowExpression || right == left)) return left;
                if (right != null && conditional.Then.OrDefault() is BirThrowExpression) return right;
            }
            return null;
        }
        var returned = new List<TypeNode>();
        var nonCarrier = false;
        void Returns(BirGraphValue node)
        {
            if (node is IBirExecutablePayload or BirLocalFunction) return;
            if (node is BirReturn result)
            {
                if (Result(result.Value.OrDefault() as BirGraphValue) is TypeNode carrier) returned.Add(carrier);
                else nonCarrier = true;
                return;
            }
            node.VisitChildren(Returns);
        }
        foreach (var statement in body.Value) Returns((BirGraphValue)statement);
        var distinct = returned.Distinct().ToArray();
        if (!nonCarrier && distinct.Length == 1 && method.Ret.Value != distinct[0] && Matches(method.Ret.Value, distinct[0]))
            PreserveNormalizedSourceReturn(method, method.Ret.Value, distinct[0], state);
    }

    internal static void NormalizeSourceFixedPoint(IReadOnlyList<BirFileDeclaration> roots, SourceRewriteState state,
        IReadOnlyList<SharedSyntheticSynthesis.CellStorage> cells)
    {
        var localCarriers = cells is { Count: > 0 } ? state.Owners.Values.Where(owner => owner.Needed)
            .ToDictionary(owner => owner.Name, owner => owner.ErasedName, StringComparer.Ordinal) : null;
        void AlignCells()
        {
            if (cells is { Count: > 0 }) ExistentialSharedCellAlignment.ApplyAll(roots, cells, localCarriers, state.References);
        }
        var normalized = new NormalizedReturnBindings();
        void Walk(BirGraphSlot slot)
        {
            var node = slot.Value;
            if (node is IBirExecutablePayload) return;
            var (id, owner, member) = node switch {
                BirCallStatic call => (call.DeclarationId.OrDefault(), call.CalleeOwner.OrDefault() as TypeNode.Fqn, call.Method.OrDefault()),
                // Instance calls carry their selected declaration ID, not a
                // calleeOwner fact. Do not recover one from ownerType/name.
                BirCallInstance call => (call.DeclarationId.OrDefault(), (TypeNode.Fqn)null, call.Method.OrDefault()),
                _ => (null, null, null),
            };
            TypeNode result = null;
            if (id != null) normalized.ByDeclaration.TryGetValue(id, out result);
            if (result == null && owner != null && member != null)
                normalized.ByPhysicalMethod.TryGetValue(PhysicalMethodKey(owner.Name, member), out result);
            if (result != null)
            {
                var semantic = SourceCarrierExpressionType(node);
                // This pass publishes sty even if absent. Other result stamps
                // retain their authored absence/null, unlike general call binding.
                if (node is BirCallStatic call)
                {
                    call.Sty = BirGraphValue.CopyType(result);
                    if (call.Ret.HasValue) call.Ret = BirGraphValue.CopyType(result);
                    if (call.DynRet.HasValue) call.DynRet = BirGraphValue.CopyType(result);
                }
                else if (node is BirCallInstance instance)
                {
                    instance.Sty = BirGraphValue.CopyType(result);
                    if (instance.Ret.HasValue) instance.Ret = BirGraphValue.CopyType(result);
                    if (instance.DynRet.HasValue) instance.DynRet = BirGraphValue.CopyType(result);
                }
                if (result is TypeNode.Array { Elem: var element }
                    && IsExistentialPhysicalCarrier(element, state.Owners, state.References)
                    && semantic is TypeNode.Array semanticArray)
                    state.ArrayProjection(node, semanticArray.Elem is TypeNode.Projection projection ? projection.Of : semanticArray.Elem);
                else if (semantic != null && IsExistentialPhysicalCarrier(result, state.Owners, state.References))
                    state.ProjectionFact(node, semantic);
            }
            node.VisitChildSlots(Walk);
            node = BindSourceConsumerNode(node, state);
            slot.Replace(node);
            if (SourceCallableBody(node).HasValue && SourceCallableHasParameters(node)) NormalizeInnerFactoryLocalsSource(node, state);
        }
        void Consumers()
        {
            foreach (var root in roots)
                Walk(new BirGraphSlot(() => root, replacement => {
                    if (!ReferenceEquals(replacement, root)) throw new InvalidOperationException("Carrier normalization replaced a file declaration");
                }));
        }
        bool Collect()
        {
            var found = false;
            void Methods(IEnumerable<BirFunctionDeclaration> methods, string owner)
            {
                foreach (var method in methods)
                {
                    if (!state.TakeNormalized(method) || !method.Body.HasValue || !method.Params.HasValue
                        || !method.Name.HasValue || !method.Ret.HasValue
                        || !ContainsPhysicalExistential(method.Ret.Value, state.Owners, state.References)) continue;
                    var table = method.DeclarationId.HasValue ? normalized.ByDeclaration : normalized.ByPhysicalMethod;
                    var key = method.DeclarationId.OrDefault() ?? PhysicalMethodKey(owner, method.Name.Value);
                    if (table.TryGetValue(key, out var prior) && prior != method.Ret.Value)
                        throw new InvalidOperationException(method.DeclarationId.HasValue
                            ? $"normalized declaration '{key}' has conflicting physical return carriers"
                            : $"normalized method '{owner}.{method.Name.Value}' has conflicting physical return carriers");
                    table[key] = method.Ret.Value;
                    found = true;
                }
            }
            foreach (var root in roots)
            {
                if (root.FileClass.HasValue) Methods(root.Methods.OrEmpty(), root.FileClass.Value);
                foreach (var type in root.Types.OrEmpty())
                    if (type.Name.HasValue) Methods(type.Methods.OrEmpty(), type.Name.Value);
            }
            return found;
        }
        AlignCells();
        Consumers();
        while (Collect()) Consumers();
        if (cells is { Count: > 0 }) { AlignCells(); Consumers(); }
    }
}
