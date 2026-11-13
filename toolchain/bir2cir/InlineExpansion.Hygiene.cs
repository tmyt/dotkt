using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    static IBirExpression HygieneClone(IBirExpression expression) => expression == null ? null
        : (IBirExpression)((BirGraphValue)expression).DeepClone();

    internal void FreshenLabelsJoint(params BirGraphValue[] roots)
    {
        var labels = new Dictionary<int, int>();
        void Collect(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (node is BirLabel { Id.HasValue: true } label) labels.TryAdd(label.Id.Value, 0);
            WithoutSyntheticDeclarationSlots(node, slot => Collect(slot.Value));
        }
        foreach (var root in roots) Collect(root);
        foreach (var label in labels.Keys.ToArray()) labels[label] = Pass.NextLabel();
        void Apply(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            switch (node)
            {
                case BirLabel { Id.HasValue: true } value when labels.TryGetValue(value.Id.Value, out var next): value.Id = next; break;
                case BirGoto { Id.HasValue: true } value when labels.TryGetValue(value.Id.Value, out var next): value.Id = next; break;
                case BirBranchIf { Id.HasValue: true } value when labels.TryGetValue(value.Id.Value, out var next): value.Id = next; break;
            }
            WithoutSyntheticDeclarationSlots(node, slot => Apply(slot.Value));
        }
        foreach (var root in roots) Apply(root);
    }

    internal void PrefixLocalsJoint(string prefix, params BirGraphValue[] roots)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        void Collect(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload or BirNewSuspendLambda) return;
            if (node is BirInlineLambda lambda)
            {
                foreach (var statement in lambda.Body.OrEmpty()) Collect((BirGraphValue)statement);
                if (lambda.Result.HasValue) Collect((BirGraphValue)lambda.Result.Value);
                return;
            }
            switch (node)
            {
                case BirVariable { Name.HasValue: true } value: declared.Add(value.Name.Value); break;
                case BirForIn { Var.HasValue: true } value: declared.Add(value.Var.Value); break;
                case BirForArray { Var.HasValue: true } value: declared.Add(value.Var.Value); break;
                case BirRepeat { Var.HasValue: true } value: declared.Add(value.Var.Value); break;
                case BirCatch { Var.HasValue: true } value: declared.Add(value.Var.Value); break;
            }
            WithoutSyntheticDeclarationSlots(node, slot => Collect(slot.Value));
        }
        foreach (var root in roots) Collect(root);
        foreach (var root in roots) Prefix(root, declared);

        void Prefix(BirGraphValue node, HashSet<string> names)
        {
            if (node == null || node is IBirExecutablePayload || names.Count == 0) return;
            if (node is BirNewSuspendLambda suspend)
            {
                var inner = SuspendLambdaInnerScope(suspend);
                foreach (var capture in suspend.Captures.OrEmpty())
                    if (capture.Name.HasValue && names.Contains(capture.Name.Value) && !inner.Contains(capture.Name.Value)
                        && !capture.Outer.OrDefault()) capture.Name = prefix + capture.Name.Value;
                var bodyNames = new HashSet<string>(names, StringComparer.Ordinal);
                bodyNames.ExceptWith(inner);
                foreach (var statement in suspend.Body.OrEmpty()) Prefix((BirGraphValue)statement, bodyNames);
                foreach (var value in suspend.CapValues.OrEmpty()) if (value != null) Prefix((BirGraphValue)value, names);
                return;
            }
            if (node is BirInlineLambda lambda)
            {
                var parameters = InlineLambdaParamNames(lambda);
                foreach (var capture in lambda.Captures.OrEmpty())
                {
                    if (capture.Name.HasValue && names.Contains(capture.Name.Value) && !parameters.Contains(capture.Name.Value)
                        && !capture.Outer.OrDefault()) capture.Name = prefix + capture.Name.Value;
                    if (capture.Value.HasValue) Prefix((BirGraphValue)capture.Value.Value, names);
                }
                var bodyNames = new HashSet<string>(names, StringComparer.Ordinal);
                bodyNames.ExceptWith(parameters);
                foreach (var statement in lambda.Body.OrEmpty()) Prefix((BirGraphValue)statement, bodyNames);
                if (lambda.Result.HasValue) Prefix((BirGraphValue)lambda.Result.Value, bodyNames);
                return;
            }
            string Rename(string name) => name != null && names.Contains(name) ? prefix + name : name;
            switch (node)
            {
                case BirVariable { Name.HasValue: true } value: value.Name = Rename(value.Name.Value); break;
                case BirLocal { Name.HasValue: true } value: value.Name = Rename(value.Name.Value); break;
                case BirSetLocal { Name.HasValue: true } value: value.Name = Rename(value.Name.Value); break;
                case BirForIn { Var.HasValue: true } value: value.Var = Rename(value.Var.Value); break;
                case BirForArray { Var.HasValue: true } value: value.Var = Rename(value.Var.Value); break;
                case BirRepeat { Var.HasValue: true } value: value.Var = Rename(value.Var.Value); break;
                case BirCatch { Var.HasValue: true } value: value.Var = Rename(value.Var.Value); break;
            }
            WithoutSyntheticDeclarationSlots(node, slot => Prefix(slot.Value, names));
        }
    }

    internal BirGraphValue RewriteLocalRefs(BirGraphValue node,
        IReadOnlyDictionary<string, IBirExpression> substitutions, Func<string, IBirExpression, IBirExpression> pin = null)
    {
        if (node == null || substitutions.Count == 0 || node is IBirExecutablePayload) return node;
        if (node is BirNewSuspendLambda suspend)
        {
            var inner = SuspendLambdaInnerScope(suspend);
            var repinned = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
            foreach (var capture in suspend.Captures.OrEmpty())
                if (capture.Name.HasValue && !inner.Contains(capture.Name.Value) && !capture.Outer.OrDefault()
                    && substitutions.TryGetValue(capture.Name.Value, out var bound))
                    capture.Name = DescriptorName(capture.Name.Value, bound, pin, repinned, "a payload newSuspendLambda captures");
            var bodySubstitutions = NestedBindings(substitutions, inner, repinned);
            if (suspend.Body.HasValue)
                for (var index = 0; index < suspend.Body.Value.Length; index++)
                    suspend.Body.Value[index] = (IBirStatement)RewriteLocalRefs((BirGraphValue)suspend.Body.Value[index], bodySubstitutions, pin);
            if (suspend.CapValues.HasValue)
                for (var index = 0; index < suspend.CapValues.Value.Length; index++)
                    if (suspend.CapValues.Value[index] is { } value)
                        suspend.CapValues.Value[index] = (IBirExpression)RewriteLocalRefs((BirGraphValue)value, substitutions, pin);
            return node;
        }
        if (node is BirInlineLambda lambda)
        {
            var parameters = InlineLambdaParamNames(lambda);
            var repinned = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
            foreach (var capture in lambda.Captures.OrEmpty())
            {
                if (capture.Name.HasValue && !parameters.Contains(capture.Name.Value) && !capture.Outer.OrDefault()
                    && substitutions.TryGetValue(capture.Name.Value, out var bound))
                    capture.Name = DescriptorName(capture.Name.Value, bound, pin, repinned, "a nested inlineLambda carrier captures");
                if (capture.Value.HasValue)
                    capture.Value = OptionalField<IBirExpression>.Of((IBirExpression)RewriteLocalRefs((BirGraphValue)capture.Value.Value, substitutions, pin));
            }
            var bodySubstitutions = NestedBindings(substitutions, parameters, repinned);
            if (lambda.Body.HasValue)
                for (var index = 0; index < lambda.Body.Value.Length; index++)
                    lambda.Body.Value[index] = (IBirStatement)RewriteLocalRefs((BirGraphValue)lambda.Body.Value[index], bodySubstitutions, pin);
            if (lambda.Result.HasValue)
                lambda.Result = OptionalField<IBirExpression>.Of((IBirExpression)RewriteLocalRefs((BirGraphValue)lambda.Result.Value, bodySubstitutions, pin));
            return node;
        }
        IBirExpression Pointer(IBirExpression pointer) => pointer is BirLocal { Name.HasValue: true } local
            && substitutions.TryGetValue(local.Name.Value, out var binding)
            ? new BirByRefOf { Inner = OptionalField<IBirExpression>.Of(HygieneClone(binding)) } : pointer;
        if (node is BirByRefLoad load && load.Ptr.HasValue) load.Ptr = OptionalField<IBirExpression>.Of(Pointer(load.Ptr.Value));
        if (node is BirByRefStore store && store.Ptr.HasValue) store.Ptr = OptionalField<IBirExpression>.Of(Pointer(store.Ptr.Value));
        WithoutSyntheticDeclarationSlots(node, slot => slot.Replace(RewriteLocalRefs(slot.Value, substitutions, pin)));
        if (node is BirLocal { Name.HasValue: true } read && substitutions.TryGetValue(read.Name.Value, out var replacement))
            return (BirGraphValue)HygieneClone(replacement);
        if (node is BirSetLocal { Name.HasValue: true } write && substitutions.TryGetValue(write.Name.Value, out var target))
            write.Name = DescriptorName(write.Name.Value, target, null, null, "the payload assigns to");
        return node;
    }

    static IReadOnlyDictionary<string, IBirExpression> NestedBindings(IReadOnlyDictionary<string, IBirExpression> original,
        HashSet<string> shadowed, Dictionary<string, IBirExpression> repinned)
    {
        if (shadowed.Count == 0 && repinned.Count == 0) return original;
        var result = original.Where(pair => !shadowed.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in repinned) result[pair.Key] = pair.Value;
        return result;
    }

    static string DescriptorName(string name, IBirExpression bound, Func<string, IBirExpression, IBirExpression> pin,
        Dictionary<string, IBirExpression> repinned, string what)
    {
        if (bound is BirLocal { Name.HasValue: true } local) return local.Name.Value;
        if (pin == null) throw new NotSupportedException(
            $"inline splice: {what} '{name}' but the splice binds it to a non-local expression — a capture descriptor can only name a local/temp");
        var pinned = pin(name, bound);
        repinned?.Add(name, pinned);
        return (pinned as BirLocal)?.Name.OrDefault();
    }

    internal HashSet<string> AssignedLocals(params BirGraphValue[] roots)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (node is BirSetLocal { Name.HasValue: true } write) found.Add(write.Name.Value);
            WithoutSyntheticDeclarationSlots(node, slot => Walk(slot.Value));
        }
        foreach (var root in roots) Walk(root);
        return found;
    }

    internal BirGraphValue RewriteThis(BirGraphValue node, string thisTemp, TypeNode thisType)
    {
        if (node == null || node is IBirExecutablePayload) return node;
        if (node is BirNewSuspendLambda suspend)
        {
            if (suspend.CapValues.HasValue)
                for (var index = 0; index < suspend.CapValues.Value.Length; index++)
                    if (suspend.CapValues.Value[index] is { } value)
                        suspend.CapValues.Value[index] = (IBirExpression)RewriteThis((BirGraphValue)value, thisTemp, thisType);
            return node;
        }
        if (node is BirThis) return new BirLocal { Name = thisTemp, Sty = BirGraphValue.CopyType(thisType) };
        WithoutSyntheticDeclarationSlots(node, slot => slot.Replace(RewriteThis(slot.Value, thisTemp, thisType)));
        if (node is BirCallEvaluation evaluation)
            foreach (var binding in evaluation.Bindings.OrEmpty())
                if (binding.Expr.OrDefault() is BirLocal local && local.Name.OrDefault() == thisTemp)
                {
                    binding.Type = BirGraphValue.CopyType(thisType);
                    void Stamp(BirGraphValue child)
                    {
                        if (child is BirBindingReference read && read.Id.OrDefault() == binding.Id.OrDefault())
                            read.Sty = BirGraphValue.CopyType(thisType);
                        else child.VisitChildren(Stamp);
                    }
                    Stamp(evaluation);
                }
        return node;
    }
}
