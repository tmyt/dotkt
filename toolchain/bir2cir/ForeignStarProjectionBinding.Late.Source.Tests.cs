using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ForeignStarProjectionBinding
{
    internal static void SelfTestLateSource()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning foreign late binding: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] {
            typeof(object).Assembly.Location, typeof(Enumerable).Assembly.Location,
            Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location), "System.Runtime.dll"),
        });
        BeginBinding(null, new(), resetUsage: true);
        _projectValueType = (type, _) => type;
        var projectedList = new TypeNode.Fqn("System.Collections.Generic.IList`1", new TypeNode[] { new TypeNode.Star() });
        foreach (var access in new[] { "get", "set" })
        {
            var signature = access == "get" ? new[] { Int } : new[] { Int, AnyN };
            var propertyReceiver = new BirLocal { Name = "list", Sty = projectedList };
            var property = new BirConstrainedCall { Iface = projectedList, Method = "Item", Prop = access,
                Recv = propertyReceiver, Sig = signature, Args = signature.Select((type, index) =>
                    (IBirExpression)new BirLocal { Name = "argument" + index, Sty = type }).ToArray() };
            var input = ReadSourceCall(property);
            Need(input.Property == access && ReferenceEquals(input.Receiver, propertyReceiver)
                && ReferenceEquals(input.Signature, signature), "constrained property lost its accessor role or original operands");
            var selectedAccessor = SelectForeignMember(projectedList, input.Name, input.Property, input.Kind, 0, signature, refs);
            Need(selectedAccessor.Method.IsSpecialName && selectedAccessor.Method.Name == access + "_Item",
                "constrained property did not select the actual CLR accessor");
        }
        RequireRuntimeFallback();
        var ingredients = new HashSet<BirClosureDefinition>(ReferenceEqualityComparer.Instance);
        var delegateValue = new BirLocal { Name = "foreignDelegate" };
        var adapter = SourceDelegateAdapter(delegateValue,
            new TypeNode.Fn(false, String, new TypeNode[] { new TypeNode.Tv("method", 0) }), ingredients);
        var receiver = new BirCallStatic { Owner = new TypeNode.Fqn("probe.Calls"), Method = "receiver",
            Ret = AnyN, Args = Array.Empty<IBirExpression>() };
        var preferred = new TypeNode.Fqn("System.Collections.Generic.ICollection", new[] { String });
        var count = new BirCallStatic { Owner = new TypeNode.Fqn(RuntimeOwner), Method = "projectedViewCountErased",
            Args = new IBirExpression[] { receiver, SourceClassRef(preferred), SourceInteger(5) },
            Sig = new[] { AnyN, TypeN, Int }, Ret = Int };
        var statement = SourceStatement(count);
        var file = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Name = "count", Body = new IBirStatement[] { statement } } }, Types = Array.Empty<IBirTypeDeclaration>() };
        var callbacks = new List<string>();
        var before = _nextDelegateAdapter;
        var reports = ApplyLate(new[] { file }, new Dictionary<string, string>(), refs,
            (actual, types) => { Need(ReferenceEquals(actual, file) && types.Count == 0, "ordinary count unexpectedly created a declaration"); callbacks.Add("admit"); },
            types => { Need(types.Count == 0, "ordinary count projected an unrelated class"); callbacks.Add("project"); });
        Need(callbacks.SequenceEqual(new[] { "admit", "project" }) && reports.Count == 1
            && ReferenceEquals(reports[0].File, file) && reports[0].AppendedTypes.Count == 0
            && UsedRuntimeFallback && _bindProjectedHelpers && _nextDelegateAdapter == before,
            "late entry changed initial usage/adapter state or attachment/admission/projection ordering");
        Need(statement.Expr.Value is BirValueBlock block && block.Stmts.Value[0] is BirVariable actualReceiver
            && ReferenceEquals(actualReceiver.Init.Value, receiver)
            && block.Result.Value is BirConditional { Else.Value: BirCallStatic fallback }
            && ReferenceEquals(fallback, count) && fallback.Args.Value[0] is BirLocal,
            "late count did not move its actual receiver once or retain the original helper as its fallback");

        // The real reflected static helper selector is also shared by projected
        // aliases; test it with the shipped Enumerable.Count<T> declaration.
        var helperReceiver = new BirLocal { Name = "sequence" };
        var declarationSignature = new TypeNode[] { new TypeNode.Fqn("System.Collections.Generic.IEnumerable",
            new TypeNode[] { new TypeNode.Tv("method", 0) }) };
        var helper = new BirCallStatic { Owner = new TypeNode.Fqn("System.Linq.Enumerable"), Method = "Count",
            ProjectedHelperReceiver = 0, TypeArgs = new TypeNode[] { String }, Sig = declarationSignature,
            Ret = Int, Args = new IBirExpression[] { helperReceiver } };
        Need(refs.TryResolveStaticMemberSignature("System.Linq.Enumerable", "Count", 1, true,
            declarationSignature, null, out _, out var selected, out _), "shipped static Count declaration was not selectable");
        var rewritten = RewriteSourceStaticHelper(helper, refs);
        Need(rewritten is BirCast { E.Value: BirCallStatic invocation } && invocation.Method.Value == "starProjectionInvokeStaticHelper"
            && invocation.Args.Value[1] is BirConstant { Value.Value: BirLiteral.IntegerLiteral token } && token.Value == selected.MetadataToken
            && invocation.Args.Value[5] is BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 0 } }
            && invocation.Args.Value[6] is BirNewArray values && ReferenceEquals(values.Elems.Value.Single(), helperReceiver),
            "projected static helper lost its selected token/receiver index or original argument ownership");

        // Exercise the SAME materialization handoff with genuine authored
        // delegate ingredients. Admission must see attachment before projection.
        var holder = SourceStatement(adapter);
        var owner = new BirFunctionDeclaration { Name = "adapt", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "foreignDelegate", Type = AnyN } },
            Body = new IBirStatement[] { holder } };
        var adapterFile = new BirFileDeclaration { Methods = new[] { owner }, Types = Array.Empty<IBirTypeDeclaration>() };
        IReadOnlyList<BirClassDefinition> admitted = null;
        var materialized = MaterializeSourceAdapters(adapterFile, ingredients, refs,
            (actualFile, types) => {
                Need(ReferenceEquals(actualFile, adapterFile) && types.Count == 1
                    && actualFile.Types.Value.Contains(types[0], ReferenceEqualityComparer.Instance),
                    "admission received a detached/reconstructed declaration");
                admitted = types;
            }, types => Need(ReferenceEquals(types, admitted) && ReferenceEquals(types.Single(), adapterFile.Types.Value.Single()),
                "projection preceded exact-identity admission or re-collected its work"));
        Need(materialized.AppendedTypes.Count == 1 && ReferenceEquals(materialized.AppendedTypes, admitted)
            && ReferenceEquals(holder.Expr.Value, adapter) && !adapter.SynthClass.IsPresent
            && ReferenceEquals(adapter.Captures.Value.Single(), delegateValue),
            "late adapter handoff replaced its original capture or lost its attached declaration");
        BeginBinding(null, new(), resetUsage: true);
        Console.WriteLine("[owning foreign late binding] self-test OK (continued counters, exact Count/static selection, receiver move and attached adapter admission-before-projection)");
    }
}
