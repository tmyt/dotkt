using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DelegateTargetSlotAlignment
{
    internal static bool Apply(BirFileDeclaration root, ValueTypeOracle isValue,
        Action<BirFunctionDeclaration, TypeNode, TypeNode> returnChanged)
    {
        _isValue = isValue ?? (_ => false);
        var statics = new Dictionary<string, Demand>(StringComparer.Ordinal);
        var closures = new Dictionary<string, Demand>(StringComparer.Ordinal);
        CollectSource(root, statics, closures);
        if (statics.Count == 0 && closures.Count == 0) return false;
        _moved = false;
        AlignSourceMethods(root.Methods.OrEmpty(), root.FileClass.OrDefault(), statics, returnChanged);
        foreach (var owner in root.Types.OrEmpty())
        {
            AlignSourceMethods(owner.Methods.OrEmpty(), owner.Name.OrDefault(), statics, returnChanged);
            if (owner.Name.HasValue && closures.TryGetValue(owner.Name.Value, out var demand))
                foreach (var method in owner.Methods.OrEmpty())
                    if (method.Name.OrDefault() == "invoke") AlignSource(method, demand, returnChanged);
        }
        return _moved;
    }

    static void CollectSource(BirGraphValue node, Dictionary<string, Demand> statics, Dictionary<string, Demand> closures)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirNewDelegate { Sig.HasValue: false, FuncType.HasValue: true, FuncType.Value: TypeNode.Fn { Suspend: false } function } target
            && target.Method.HasValue && target.CalleeOwner.OrDefault() is TypeNode.Fqn owner)
            Demanded(statics, owner.Name + "|" + target.Method.Value + "|" + AnySig, function);
        else if (node is BirNewClosure { FuncType.HasValue: true, FuncType.Value: TypeNode.Fn { Suspend: false } closureFunction } closure
            && closure.ClosureType.OrDefault() is TypeNode.Fqn closureType)
            Demanded(closures, closureType.Name, closureFunction);
        node.VisitGenericFrame((_, _) => { }, (role, child) => {
            if (!NullableRepresentationViews.Frozen(role)
                && role is not (BirGenericFrameRole.ReferencedDeclaration or BirGenericFrameRole.ResolvedDeclaration
                    or BirGenericFrameRole.OverrideDeclaration)) CollectSource(child, statics, closures);
        });
    }

    static void AlignSourceMethods(BirFunctionDeclaration[] methods, string owner, Dictionary<string, Demand> demands,
        Action<BirFunctionDeclaration, TypeNode, TypeNode> returnChanged)
    {
        if (owner == null) return;
        foreach (var (method, demand) in SelectedTargets(methods, owner, demands,
            method => method.Name.OrDefault(), method => method.SemanticOwner.OrDefault(),
            method => method.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray()))
            AlignSource(method, demand, returnChanged);
    }

    static void AlignSource(BirFunctionDeclaration method, Demand demand,
        Action<BirFunctionDeclaration, TypeNode, TypeNode> returnChanged)
    {
        var parameters = method.Params.OrEmpty();
        var plan = Plan(parameters.Select(parameter => parameter.Type.OrDefault()).ToArray(), method.Ret.OrDefault(), demand);
        List<IBirStatement> prologue = null;
        foreach (var index in plan.Parameters)
        {
            var parameter = parameters[index];
            var sourceType = parameter.Type.Value;
            if (method.Body.HasValue && parameter.Name.HasValue)
            {
                var sourceName = parameter.Name.Value;
                var physicalName = CallEvalLowering.FreshBindingId();
                parameter.Name = physicalName;
                (prologue ??= new()).Add(new BirVariable { Name = sourceName, Type = BirGraphValue.CopyType(sourceType),
                    Init = new BirCast { Type = BirGraphValue.CopyType(sourceType), _exactBridgeCast = true,
                        FunctionValueRestoration = true, E = new BirLocal { Name = physicalName, Sty = ObjFqn } } });
            }
            parameter.Type = ObjFqn;
            _moved = true;
        }
        if (prologue != null) method.Body = prologue.Concat(method.Body.Value).ToArray();
        if (plan.Return)
        {
            var before = method.Ret.Value;
            method.Ret = ObjFqn;
            returnChanged?.Invoke(method, before, ObjFqn);
            _moved = true;
        }
    }
}
