using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Ordinary event callbacks stay in the caller frame. ClosureSynthesis alone
// owns their one rebind into dense physical class declarations.
static partial class ClrEventSubscriptionBinding
{
    internal static BirFileDeclaration Apply(BirFileDeclaration root, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<(string Owner, string Event), TypeNode> forwardedOwners,
        out IReadOnlyList<ClosureSynthesis.MaterializedOccurrence> materialized)
    {
        var binder = new SourceBinder(root, refs, forwardedOwners);
        materialized = binder.Apply(root);
        return root;
    }

    sealed class SourceBinder
    {
        readonly string scope;
        readonly ReferenceMetadataIndex refs;
        readonly IReadOnlyDictionary<(string Owner, string Event), TypeNode> forwardedOwners;
        readonly Dictionary<BirGraphValue, Frames> scopes = new(ReferenceEqualityComparer.Instance);
        readonly List<(BirValueBlock Root, ClosureSynthesis.CaptureContext Context)> generated = new();
        int next;
        readonly record struct Frames(BirTypeParameter[] Type, BirTypeParameter[] Method);
        static readonly BirTypeParameter[] Empty = Array.Empty<BirTypeParameter>();

        internal SourceBinder(BirFileDeclaration root, ReferenceMetadataIndex refs,
            IReadOnlyDictionary<(string Owner, string Event), TypeNode> forwardedOwners)
        {
            this.refs = refs;
            this.forwardedOwners = forwardedOwners;
            scope = string.Concat(root.FileClass.OrDefault("File").Select(character => char.IsLetterOrDigit(character) ? character : '_'));
            scopes[root] = new(Empty, Empty);
            foreach (var method in root.Methods.OrEmpty()) scopes[method] = new(Empty, method.TypeParams.OrEmpty());
            foreach (var type in root.Types.OrEmpty())
            {
                var parameters = TypeParameterFrame.CloneDeclarations(type);
                scopes[(BirGraphValue)type] = new(parameters, Empty);
                foreach (var constructor in type.Ctors.OrEmpty()) scopes[constructor] = new(parameters, Empty);
                foreach (var method in type.Methods.OrEmpty()) scopes[method] = new(parameters, method.TypeParams.OrEmpty());
            }
        }

        internal IReadOnlyList<ClosureSynthesis.MaterializedOccurrence> Apply(BirFileDeclaration root)
        {
            Walk(root, new(Empty, Empty), new(null, null, null));
            // A nested receiver moves into its new spill. Its old parent slot
            // must not be handed back after that event-read record is detached.
            var slots = new Dictionary<BirGraphValue, BirGraphSlot>(ReferenceEqualityComparer.Instance);
            var wanted = new HashSet<BirGraphValue>(generated.Select(item => (BirGraphValue)item.Root), ReferenceEqualityComparer.Instance);
            var nested = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
            void Find(BirGraphValue node, bool insideGenerated)
            {
                if (node is IBirExecutablePayload) return;
                node.VisitChildSlots(slot => {
                    var generatedChild = wanted.Contains(slot.Value);
                    if (generatedChild)
                    {
                        if (!slots.TryAdd(slot.Value, slot)) throw new InvalidOperationException("Event materialization has more than one owning occurrence");
                        if (insideGenerated) nested.Add(slot.Value);
                    }
                    Find(slot.Value, insideGenerated || generatedChild);
                });
            }
            Find(root, false);
            if (generated.Any(item => !slots.ContainsKey(item.Root)))
                throw new InvalidOperationException("Event materialization lost its owning occurrence");
            // Publish a non-overlapping frontier. Each outer subtree owns its
            // nested handles through nullable normalization and closure synthesis;
            // a child spill may clone an operand without orphaning a second work item.
            return generated.Where(item => !nested.Contains(item.Root))
                .Select(item => new ClosureSynthesis.MaterializedOccurrence(slots[item.Root], item.Context)).ToArray();
        }

        void Walk(BirGraphValue node, Frames frame, ClosureSynthesis.CaptureContext context)
        {
            if (node is IBirExecutablePayload) return;
            if (scopes.TryGetValue(node, out var indexed)) frame = indexed;
            context = ClosureSynthesis.ContextFor(node, context);
            var dense = node as BirNewSuspendLambda;
            node.VisitOwnChildSlots((field, _, slot) => {
                var childFrame = frame;
                if (dense?.TypeFrame.OrDefault() == "dense" && field is "body" or "params" or "captures" or "typeParams")
                    childFrame = new(dense.TypeParams.OrEmpty(), dense.TypeParams.OrEmpty());
                else if ((node is BirNewClosure closure && ReferenceEquals(slot.Value, closure.SynthClass.OrDefault())
                    || node is BirNewSam sam && ReferenceEquals(slot.Value, sam.SynthClass.OrDefault()))
                    && ClosureSynthesis.HasPreboundFrame(slot.Value))
                    childFrame = new(slot.Value is IBirTypeDeclaration type ? TypeParameterFrame.CloneDeclarations(type)
                        : ((BirClosureDefinition)slot.Value).TypeParams.OrEmpty().Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray(), Empty);
                Walk(slot.Value, childFrame, context);
                if (slot.Value is BirEventGet read)
                {
                    var result = Materialize(read, childFrame);
                    slot.Replace(result);
                    generated.Add((result, context));
                }
            });
        }

        BirValueBlock Materialize(BirEventGet read, Frames frame)
        {
            if ((read.Sty.OrDefault() ?? read.Ret.OrDefault()) is not TypeNode.Fqn eventType
                || eventType.Name != "kotlin.clr.ClrEvent" || eventType.Args == null)
                throw new InvalidOperationException("bir2cir: an event read is missing its complete handler type");
            var arguments = refs.NullableTypeFrames.TryGetValue(eventType.Name, out var physical)
                ? physical.OrdinaryArguments(eventType.Args) : eventType.Args;
            if (arguments.Length != 1) throw new InvalidOperationException("bir2cir: an event read has no unique handler argument");
            var handler = arguments[0];
            var owner = EventOwner(read) ?? throw new InvalidOperationException("bir2cir: an event read is missing its event owner type");
            var isStatic = read.Static.OrDefault();
            if (refs.TryClrEventIsStatic((owner as TypeNode.Fqn)?.Name, read.Name.OrDefault(), out var declaredStatic)) isStatic = declaredStatic;
            var id = next++;
            var receiverLocal = $"__clrEventReceiver{id}";
            var free = FreeVariables(owner, handler);
            for (var index = 0; index < free.Count; index++)
            {
                var declaration = SourceParameter(free[index], frame);
                foreach (var dependency in FreeVariables(declaration?.Constraints.OrEmpty() ?? Array.Empty<TypeNode>()))
                    if (!free.Any(value => value.Scope == dependency.Scope && value.I == dependency.I)) free.Add(dependency);
            }
            var unit = new TypeNode.Fqn("kotlin.Unit");
            var callbackType = new TypeNode.Fn(false, unit, new[] { handler });
            BirNewClosure Callback(bool add)
            {
                var name = $"dotkt${scope}$Event{(add ? "Add" : "Remove")}{id}";
                var fields = new List<BirCaptureDescriptor>();
                var captures = new List<IBirExpression>();
                IBirExpression receiver = null;
                if (!isStatic)
                {
                    fields.Add(new BirCaptureDescriptor { Name = "__receiver", Type = Copy(owner) });
                    captures.Add(new BirLocal { Name = receiverLocal, Sty = Copy(owner) });
                    receiver = new BirFieldGet { OwnerType = new TypeNode.Fqn(name), Recv = new BirThis(), Name = "__receiver" };
                }
                IBirExpression operation = add
                    ? new BirEventAdd { Type = Copy(owner), Event = read.Name.HasValue ? read.Name : OptionalField<string>.Null,
                        Static = isStatic, Handler = new BirLocal { Name = "handler", Sty = Copy(handler) }, HandlerExact = true,
                        Recv = isStatic ? default : OptionalField<IBirExpression>.Of(receiver),
                        MemberVisibility = read.MemberVisibility.HasValue ? read.MemberVisibility : default }
                    : new BirEventRemove { Type = Copy(owner), Event = read.Name.HasValue ? read.Name : OptionalField<string>.Null,
                        Static = isStatic, Handler = new BirLocal { Name = "handler", Sty = Copy(handler) }, HandlerExact = true,
                        Recv = isStatic ? default : OptionalField<IBirExpression>.Of(receiver),
                        MemberVisibility = read.MemberVisibility.HasValue ? read.MemberVisibility : default };
                var ingredients = new BirClosureDefinition { Name = name, Fields = fields.ToArray(),
                    Params = new[] { new BirLambdaParameter { Name = "handler", Type = Copy(handler) } }, Ret = unit,
                    Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(operation) } } };
                if (free.Count > 0)
                    ingredients.TypeParams = free.Select((variable, index) => {
                        var declaration = (BirTypeParameter)SourceParameter(variable, frame).DeepClone();
                        declaration.Name = $"T{index}";
                        return declaration;
                    }).ToArray();
                var result = new BirNewClosure { ClosureType = new TypeNode.Fqn(name), Captures = captures.ToArray(),
                    Method = "invoke", FuncType = Copy(callbackType), SynthClass = ingredients };
                if (free.Count > 0) result.TypeArgs = free.Select(variable => (TypeNode)new TypeNode.Tv(variable.Scope, variable.I)).ToArray();
                return result;
            }
            var statements = isStatic ? Array.Empty<IBirStatement>() : new IBirStatement[] {
                new BirVariable { Name = receiverLocal, Type = Copy(owner),
                    Init = read.Recv.IsPresent ? read.Recv : OptionalField<IBirExpression>.Null } };
            return new BirValueBlock { Stmts = statements, Sty = Copy(eventType), Result = new BirNew { Type = Copy(eventType),
                ArgTypes = new[] { Copy(callbackType), Copy(callbackType) }, Args = new IBirExpression[] { Callback(true), Callback(false) } } };
        }

        TypeNode EventOwner(BirEventGet read)
        {
            var owner = read.Type.OrDefault();
            if (owner is TypeNode.Fqn named && read.Name.HasValue && forwardedOwners.TryGetValue((named.Name, read.Name.Value), out var forwarded))
                return SubstituteTypeVariables(forwarded, named.Args);
            return owner;
        }

        static BirTypeParameter SourceParameter(TypeNode.Tv variable, Frames frame)
        {
            var parameters = variable.Scope == "type" ? frame.Type : variable.Scope == "method" ? frame.Method : null;
            return parameters != null && variable.I >= 0 && variable.I < parameters.Length ? parameters[variable.I] : null;
        }

        static List<TypeNode.Tv> FreeVariables(params TypeNode[] types)
        {
            var result = new List<TypeNode.Tv>();
            var seen = new HashSet<(string, int)>();
            foreach (var type in types)
                if (type != null) BirGraphNameFields.VisitTypeNodes(type, node => {
                    if (node is TypeNode.Tv variable && seen.Add((variable.Scope, variable.I))) result.Add(variable);
                });
            return result;
        }

        static TypeNode Copy(TypeNode type) => BirGraphValue.CopyType(type);
    }
}
