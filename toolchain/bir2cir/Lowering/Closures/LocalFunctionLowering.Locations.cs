using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class LocalFunctionLowering
{
    // Solve alias obligations before hoisting mutates lexical declarations.
    // Edges are actual callLocal IDs and borrowed parameter/source records, not
    // generated names. Value arguments do not alias their source locations.
    static HashSet<BirGraphValue> NativeCaptureLocations(BirFileDeclaration file)
    {
        var locals = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
        file.Visit(node => {
            if (node is BirLocalFunction { Id.HasValue: true, Decl.HasValue: true } local)
                locals.Add(local.Id.Value, local.Decl.Value);
        });
        var native = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        if (locals.Count == 0) return native;
        var aliases = new Dictionary<BirGraphValue, List<BirGraphValue>>(ReferenceEqualityComparer.Instance);
        void Link(BirGraphValue left, BirGraphValue right)
        {
            if (left == null || right == null) return;
            if (!aliases.TryGetValue(left, out var forward)) aliases[left] = forward = new();
            if (!aliases.TryGetValue(right, out var reverse)) aliases[right] = reverse = new();
            forward.Add(right);
            reverse.Add(left);
        }
        void Walk(BirGraphValue node, Dictionary<string, BirGraphValue> scope,
            Dictionary<string, BirGraphValue> evaluations)
        {
            if (node == null || node is IBirExecutablePayload) return;
            IEnumerable<(string Name, BirGraphValue Declaration)> parameters = node switch {
                BirFunctionDeclaration method => method.Params.OrEmpty().Select(p => (p.Name.OrDefault(), (BirGraphValue)p)),
                BirConstructor constructor => constructor.Params.OrEmpty().Select(p => (p.Name.OrDefault(), (BirGraphValue)p)),
                BirClosureDefinition closure => closure.Params.OrEmpty().Select(p => (p.Name.OrDefault(), (BirGraphValue)p)),
                BirNewSuspendLambda suspend => suspend.Params.OrEmpty().Select(p => (p.Name.OrDefault(), (BirGraphValue)p)),
                _ => null,
            };
            if (parameters != null)
            {
                scope = new(StringComparer.Ordinal);
                evaluations = new(StringComparer.Ordinal);
                foreach (var (name, declaration) in parameters)
                    if (name != null) scope[name] = declaration;
            }
            BirGraphValue Location(BirGraphValue expression) => expression switch {
                BirLocal { Name.HasValue: true } local => scope.GetValueOrDefault(local.Name.Value),
                BirByRefOf address => Location(address.Inner.OrDefault() as BirGraphValue),
                BirBindingReference { Id.HasValue: true } binding => evaluations.GetValueOrDefault(binding.Id.Value),
                _ => null,
            };
            if (node is BirCallEvaluation plan)
            {
                evaluations = new(evaluations, StringComparer.Ordinal);
                foreach (var binding in plan.Bindings.OrEmpty())
                {
                    Walk(binding.Expr.OrDefault() as BirGraphValue, scope, evaluations);
                    var location = Location(binding.Expr.OrDefault() as BirGraphValue);
                    if (binding.Id.HasValue && location != null) evaluations[binding.Id.Value] = location;
                    if (binding.Kind.OrDefault() == "address" && location != null) native.Add(location);
                }
                Walk(plan.Expr.OrDefault() as BirGraphValue, scope, evaluations);
                return;
            }
            if (node is BirCallLocal call && call.Id.HasValue && locals.TryGetValue(call.Id.Value, out var target))
            {
                var arguments = call.Args.OrEmpty();
                var selected = target.Params.OrEmpty();
                for (var index = 0; index < Math.Min(arguments.Length, selected.Length); index++)
                    if (IsBorrowedCapture(selected[index]))
                        Link(selected[index], Location(arguments[index] as BirGraphValue));
            }
            if (node is BirByRefOf addressOf && Location(addressOf) is { } addressed) native.Add(addressed);
            var signature = ErasedElementValues.SourceAddressParameters(node);
            var argumentIndex = 0;
            node.VisitSourceChildren((field, _, child) => {
                if (field == "args")
                {
                    if (signature != null && argumentIndex < signature.Length
                        && SignatureValueTypes.Of(signature[argumentIndex]) is TypeNode.ByRef
                        && Location(child.Value) is { } location) native.Add(location);
                    argumentIndex++;
                }
                Walk(child.Value, scope, evaluations);
            }, region => {
                var block = new Dictionary<string, BirGraphValue>(scope, StringComparer.Ordinal);
                foreach (var statement in region.Statements) Walk((BirGraphValue)statement, block, evaluations);
                if (region.Result is { } result) Walk(result.Value, block, evaluations);
            });
            if (node is BirVariable { Name.HasValue: true } variable) scope[variable.Name.Value] = variable;
        }
        Walk(file, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        var pending = new Queue<BirGraphValue>(native);
        while (pending.TryDequeue(out var location))
            foreach (var alias in aliases.GetValueOrDefault(location) ?? [])
                if (native.Add(alias)) pending.Enqueue(alias);
        return native;
    }
}
