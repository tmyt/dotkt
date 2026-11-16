using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PropertyReferenceFunctionLowering
{
    public static IReadOnlyDictionary<(string Owner, string Name), TypeNode> CollectFieldSlots(
        IEnumerable<BirFileDeclaration> roots)
    {
        var result = new Dictionary<(string Owner, string Name), TypeNode>();
        foreach (var file in roots)
        {
            Add(file.FileClass.OrDefault(), file.Fields.OrEmpty());
            foreach (var type in file.Types.OrEmpty().OfType<IBirTypeDeclaration>())
                Add(type.Name.OrDefault(), type.Fields.OrEmpty());
        }
        return result;

        void Add(string owner, IEnumerable<BirFieldDeclaration> fields)
        {
            if (owner == null) return;
            foreach (var field in fields)
                if (field.Name.HasValue && field.Type.HasValue)
                    result[(owner, field.Name.Value)] = BirGraphValue.CopyType(field.Type.Value);
        }
    }

    public static BirFileDeclaration Apply(BirFileDeclaration root,
        IReadOnlyDictionary<(string Owner, string Name), TypeNode> moduleFields)
    {
        new SourcePass(root, moduleFields).Apply();
        return root;
    }

    // The declared slot environment is deliberately captured for the whole
    // callable before rewriting its body, rather than inferred from an operand
    // or changed to the preceding-statement clock used by other source passes.
    sealed partial class SourcePass
    {
        readonly BirFileDeclaration file;
        readonly Dictionary<(string Owner, string Name), TypeNode> fields;
        readonly List<BirClassDefinition> adapters = new();
        readonly HashSet<string> typeNames = new(StringComparer.Ordinal);
        readonly string scope;
        int nextAdapter;

        internal SourcePass(BirFileDeclaration file,
            IReadOnlyDictionary<(string Owner, string Name), TypeNode> moduleFields)
        {
            this.file = file;
            fields = new(moduleFields);
            scope = string.Concat((file.FileClass.OrDefault() ?? "File")
                .Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        }

        internal void Apply()
        {
            foreach (var type in file.Types.OrEmpty().OfType<IBirTypeDeclaration>())
            {
                if (!type.Name.HasValue) continue;
                typeNames.Add(type.Name.Value);
                Index(type.Name.Value, type.Fields.OrEmpty());
            }
            Index(file.FileClass.OrDefault(), file.Fields.OrEmpty());
            foreach (var field in file.Fields.OrEmpty()) ProcessField(field);
            foreach (var method in file.Methods.OrEmpty()) ProcessMethod(method);
            foreach (var type in file.Types.OrEmpty().OfType<IBirTypeDeclaration>()) ProcessType(type);
            StripMarkers(file);
            if (adapters.Count != 0)
                file.Types = file.Types.OrEmpty().Concat<IBirTypeDeclaration>(adapters).ToArray();
        }

        void Index(string owner, BirFieldDeclaration[] declarations)
        {
            if (owner == null) return;
            foreach (var field in declarations)
                if (field.Name.HasValue && field.Type.HasValue)
                    fields[(owner, field.Name.Value)] = field.Type.Value;
        }

        void ProcessType(IBirTypeDeclaration type)
        {
            foreach (var field in type.Fields.OrEmpty()) ProcessField(field);
            foreach (var ctor in type.Ctors.OrEmpty()) ProcessConstructor(ctor);
            foreach (var method in type.Methods.OrEmpty()) ProcessMethod(method);
        }

        void ProcessField(BirFieldDeclaration field)
        {
            if (field.Type.HasValue && field.Init.HasValue)
                field.Init = Of(RewriteValue(field.Init.Value, field.Type.Value, new Context()));
        }

        void ProcessMethod(BirFunctionDeclaration method)
            => ProcessBody(method.Params.OrEmpty().Select(p => (p.Name.OrDefault(), p.Type.OrDefault())),
                method.Ret.OrDefault(), method.Body.OrDefault());

        void ProcessConstructor(BirConstructor ctor)
        {
            var context = ProcessBody(ctor.Params.OrEmpty().Select(p => (p.Name.OrDefault(), p.Type.OrDefault())),
                null, ctor.Body.OrDefault());
            var parameters = ctor.DelegationSig.OrDefault();
            RewriteArguments(ctor.BaseArgs.OrDefault(), parameters, null, null, context);
            RewriteArguments(ctor.ThisArgs.OrDefault(), parameters, null, null, context);
        }

        Context ProcessBody(IEnumerable<(string Name, TypeNode Type)> parameters, TypeNode ret, IBirStatement[] body)
        {
            var context = new Context { Ret = ret };
            foreach (var (name, type) in parameters)
                if (name != null && type != null) context.Locals[name] = type;
            if (body == null) return context;
            foreach (var statement in body) CollectLocals((BirGraphValue)statement, context.Locals);
            for (var i = 0; i < body.Length; i++) body[i] = (IBirStatement)ProcessNode((BirGraphValue)body[i], context);
            return context;
        }

        static void CollectLocals(BirGraphValue node, Dictionary<string, TypeNode> into)
        {
            if (node == null || node is IBirExecutablePayload or BirLocalFunction or BirClosureDefinition or IBirTypeDeclaration)
                return;
            if (node is BirVariable variable && variable.Name.HasValue && variable.Type.HasValue)
                into[variable.Name.Value] = variable.Type.Value;
            node.VisitChildren(child => CollectLocals(child, into));
        }

        BirGraphValue ProcessNode(BirGraphValue node, Context context)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            switch (node)
            {
                case BirLocalFunction local:
                    if (local.Decl.HasValue) ProcessMethod(local.Decl.Value);
                    return local;
                case BirFunctionDeclaration method: ProcessMethod(method); return method;
                case BirConstructor ctor: ProcessConstructor(ctor); return ctor;
                case IBirTypeDeclaration type: ProcessType(type); return node;
                case BirClosureDefinition closure:
                    ProcessBody(closure.Params.OrEmpty().Select(p => (p.Name.OrDefault(), p.Type.OrDefault())),
                        closure.Ret.OrDefault(), closure.Body.OrDefault());
                    return closure;
            }
            if (node is BirCallInstance call && FunctionSlotInvoke(call, context) is { } invoke) node = invoke;

            var targeted = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
            void Target(OptionalField<IBirExpression> child, TypeNode type,
                Action<OptionalField<IBirExpression>> replace)
            {
                if (!child.HasValue || type == null) return;
                var result = RewriteValue(child.Value, type, context);
                replace(Of(result));
                targeted.Add((BirGraphValue)result);
            }
            switch (node)
            {
                case BirSamConversion value: Target(value.E, value.FuncType.OrDefault(), x => value.E = x); break;
                case BirVariable value: Target(value.Init, value.Type.OrDefault(), x => value.Init = x); break;
                case BirSetLocal value when value.Name.HasValue && context.Locals.TryGetValue(value.Name.Value, out var type):
                    Target(value.Value, type, x => value.Value = x); break;
                case BirSetField value:
                    Target(value.Value, FieldType(value.OwnerType.OrDefault() ?? value.Type.OrDefault(),
                        value.Name.OrDefault(), value.MemberType.OrDefault()), x => value.Value = x); break;
                case BirFieldSetExpression value:
                    Target(value.Value, FieldType(value.OwnerType.OrDefault() ?? value.Type.OrDefault(),
                        value.Name.OrDefault(), value.MemberType.OrDefault()), x => value.Value = x); break;
                case BirStaticFieldSet value:
                    Target(value.Value, FieldType(value.OwnerType.OrDefault(), value.Name.OrDefault(),
                        value.MemberType.OrDefault()), x => value.Value = x); break;
                case BirReturn value: Target(value.Value, context.Ret, x => value.Value = x); break;
                case BirReturnExpression value: Target(value.Value, context.Ret, x => value.Value = x); break;
                case BirArraySet value: Target(value.Value, value.Elem.OrDefault(), x => value.Value = x); break;
                case BirNewArray value when value.Elem.HasValue && value.Elems.HasValue:
                    foreach (var result in RewriteElements(value.Elems.Value, value.Elem.Value, context)) targeted.Add(result);
                    break;
                case BirNewArrayInit value: Target(value.Init, value.FuncType.OrDefault(), x => value.Init = x); break;
                case BirPropertySet value:
                    var vector = SelectParameters(value.ResolvedMemberParams.OrDefault(), null, null, value.Sig.OrDefault());
                    Target(value.Value, vector is { Length: > 0 }
                        ? CloseParameter(vector[^1], value.Type.OrDefault(), null) : null, x => value.Value = x);
                    break;
            }
            var arguments = Arguments(node);
            if (arguments.Args != null && arguments.Parameters?.Length == arguments.Args.Length)
            {
                RewriteArguments(arguments.Args, arguments.Parameters, arguments.Owner, arguments.MethodArguments, context);
                foreach (var argument in arguments.Args) targeted.Add((BirGraphValue)argument);
            }
            // Targeted children already saw their slot. Revisiting without it
            // could consume a property marker before its enclosing adaptation.
            node.VisitChildSlots(slot => {
                if (!targeted.Contains(slot.Value)) slot.Replace(ProcessNode(slot.Value, context));
            });
            return node;
        }

        IEnumerable<BirGraphValue> RewriteElements(IBirExpression[] elements, TypeNode target, Context context)
        {
            for (var i = 0; i < elements.Length; i++)
            {
                elements[i] = RewriteValue(elements[i], target, context);
                yield return (BirGraphValue)elements[i];
            }
        }

        void RewriteArguments(IBirExpression[] arguments, TypeNode[] parameters, TypeNode owner,
            TypeNode[] methodArguments, Context context)
        {
            if (arguments == null || parameters?.Length != arguments.Length) return;
            for (var i = 0; i < arguments.Length; i++)
                arguments[i] = RewriteValue(arguments[i], CloseParameter(parameters[i], owner, methodArguments), context);
        }

        static TypeNode CloseParameter(TypeNode parameter, TypeNode owner, TypeNode[] methodArguments)
            => parameter == null ? null : Substitute(parameter,
                (owner as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>(), methodArguments ?? Array.Empty<TypeNode>());

        TypeNode FieldType(TypeNode owner, string name, TypeNode memberType)
        {
            if (owner is not TypeNode.Fqn type || name == null || !fields.TryGetValue((type.Name, name), out var slot))
                return memberType;
            return type.Args is { Length: > 0 } ? Substitute(slot, type.Args, Array.Empty<TypeNode>()) : slot;
        }

        IBirExpression RewriteValue(IBirExpression expression, TypeNode target, Context context)
        {
            if (expression == null) return null;
            if (target == null) return (IBirExpression)ProcessNode((BirGraphValue)expression, context);
            var function = FunctionTarget(target);
            if (function is { Suspend: false } && PropertyInterface(expression, context) is { } iface)
                return (IBirExpression)ProcessNode(Adapt(expression, iface, function), context);
            switch (expression)
            {
                case BirValueBlock block:
                    if (block.Stmts.HasValue)
                        for (var i = 0; i < block.Stmts.Value.Length; i++)
                            block.Stmts.Value[i] = (IBirStatement)ProcessNode((BirGraphValue)block.Stmts.Value[i], context);
                    if (block.Result.HasValue) block.Result = Of(RewriteValue(block.Result.Value, target, context));
                    return block;
                case BirConditional conditional:
                    if (conditional.Cond.HasValue) conditional.Cond = Of((IBirExpression)ProcessNode((BirGraphValue)conditional.Cond.Value, context));
                    if (conditional.Then.HasValue) conditional.Then = Of(RewriteValue(conditional.Then.Value, target, context));
                    if (conditional.Else.HasValue) conditional.Else = Of(RewriteValue(conditional.Else.Value, target, context));
                    return conditional;
                case BirCast cast:
                    if (cast.Type.HasValue && cast.E.HasValue) cast.E = Of(RewriteValue(cast.E.Value, cast.Type.Value, context));
                    else return (IBirExpression)ProcessNode(cast, context);
                    return cast;
                case BirNewArray array when ArrayTarget(target) is { } element && array.Elems.HasValue:
                    foreach (var unused in RewriteElements(array.Elems.Value, element, context)) { }
                    break;
            }
            return (IBirExpression)ProcessNode((BirGraphValue)expression, context);
        }

        static BirDelegateInvoke FunctionSlotInvoke(BirCallInstance call, Context context)
        {
            // Flow narrowing may expose KProperty here; the declared function
            // storage remains authoritative for the actual invocation ABI.
            if (call.Method.OrDefault() != "invoke") return null;
            var local = call.Recv.OrDefault() switch {
                BirLocal value => value,
                BirCast { E.HasValue: true } cast => cast.E.Value as BirLocal,
                _ => null,
            };
            if (local == null || !local.Name.HasValue || !context.Locals.TryGetValue(local.Name.Value, out var declared)
                || FunctionTarget(declared) is not TypeNode.Fn function
                || call.OwnerType.OrDefault() is not TypeNode.Fqn owner || !IsPropertyInterface(owner)
                || !call.Args.HasValue || call.Args.Value.Length != function.DelegateParams.Length) return null;
            return new BirDelegateInvoke {
                FuncType = BirGraphValue.CopyType(function), Recv = Of((IBirExpression)local.DeepClone()),
                Args = call.Args.Value.Select(x => (IBirExpression)((BirGraphValue)x).DeepClone()).ToArray(),
                Sty = call.Sty,
            };
        }

        static TypeNode.Fqn PropertyInterface(IBirExpression value, Context context)
        {
            if (value is BirNew { PropertyFunction.HasValue: true } construction
                && construction.PropertyFunction.Value.Iface.OrDefault() is TypeNode.Fqn carried) return carried;
            var type = BirGraphStaticType.Surface(value, context.Locals);
            while (type is TypeNode.Nullable nullable) type = nullable.Of;
            while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
            return type is TypeNode.Fqn iface && IsPropertyInterface(iface) ? iface : null;
        }

        static void StripMarkers(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (node is BirNew construction) construction.PropertyFunction = default;
            node.VisitChildren(StripMarkers);
        }
    }

    static OptionalField<IBirExpression> Of(IBirExpression expression) => OptionalField<IBirExpression>.Of(expression);
    static bool IsPropertyInterface(TypeNode.Fqn type)
        => type.Name.StartsWith("kotlin.reflect.KProperty", StringComparison.Ordinal)
            || type.Name.StartsWith("kotlin.reflect.KMutableProperty", StringComparison.Ordinal);

    static TypeNode[] SelectParameters(TypeNode[] resolved, TypeNode[] shape, TypeNode[] arguments, TypeNode[] signature)
        => resolved ?? shape ?? arguments ?? signature;
}
