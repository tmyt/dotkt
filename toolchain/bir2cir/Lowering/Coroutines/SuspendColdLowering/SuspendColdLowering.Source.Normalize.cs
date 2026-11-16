using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    static bool IsHoistableTrySource(BirTry region) =>
        region.Catches.OrEmpty().Any(handler => HasOwnSuspensionSource(handler.Body.OrEmpty()))
        || HasOwnSuspensionSource(region.Finally.OrEmpty());

    internal sealed partial class SourceFunGen
    {
        static bool IsStructuredLoop(BirGraphValue node) => node is BirForArray or BirForEach or BirFor or BirForRange or BirRepeat or BirWhile;
        static bool IsFlattenableLoop(BirGraphValue node) => node switch {
            BirForArray loop => HasOwnSuspensionSource(loop.Body.OrEmpty()),
            BirForEach loop => HasOwnSuspensionSource(loop.Body.OrEmpty()),
            BirFor loop => HasOwnSuspensionSource(loop.Body.OrEmpty()),
            _ => false,
        };

        // Regions are actual typed statement-vector owners. Resizing changes
        // only that vector, never the identities of its continuing descendants.
        BirGraphValue NormalizeChildren(BirGraphValue node, Func<IBirStatement[], IBirStatement[]> statements)
        {
            if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node)) return node;
            node.VisitSourceChildren(slot => slot.Replace(NormalizeChildren(slot.Value, statements)), region => {
                var rewritten = statements(region.Statements);
                if (!ReferenceEquals(rewritten, region.Statements))
                    (region.ReplaceStatements ?? throw new InvalidOperationException("A suspend normalization region has no owning statement setter"))(rewritten);
                if (region.Result is { } result) result.Replace(NormalizeChildren(result.Value, statements));
            });
            return node;
        }

        static bool SourceAny(BirGraphValue node, Func<BirGraphValue, bool> predicate, bool skipNested = true)
        {
            if (node == null || node is IBirExecutablePayload || skipNested && IsSourceOtherFrame(node)) return false;
            if (predicate(node)) return true;
            var found = false; node.VisitChildren(child => { if (!found) found = SourceAny(child, predicate, skipNested); }); return found;
        }
        static int MaxLabelId(IEnumerable<IBirStatement> body)
        {
            var maximum = 0;
            void Walk(BirGraphValue node)
            {
                if (node == null || node is IBirExecutablePayload) return;
                var id = node switch { BirLabel label => label.Id.OrDefault(), BirGoto jump => jump.Id.OrDefault(),
                    BirBranchIf branch => branch.Id.OrDefault(), _ => 0 };
                maximum = Math.Max(maximum, id); node.VisitChildren(Walk);
            }
            foreach (var statement in body) Walk((BirGraphValue)statement);
            return maximum;
        }
        static bool HasSuspendingFinally(IEnumerable<IBirStatement> body) => body.Any(statement => SourceAny((BirGraphValue)statement,
            node => node is BirTry region && region.Finally.OrEmpty().Length > 0 && HasOwnSuspensionSource(region.Body.OrEmpty()), skipNested: false));

        IBirStatement[] FlattenSuspendingLoops(IBirStatement[] body)
        {
            if (!body.Any(statement => SourceAny((BirGraphValue)statement, IsFlattenableLoop))) return body;
            return FlattenStmtList(body);
        }
        IBirStatement[] FlattenStmtList(IBirStatement[] body)
        {
            var output = new List<IBirStatement>();
            foreach (var statement in body)
            {
                var current = NormalizeChildren((BirGraphValue)statement, FlattenStmtList);
                if (IsFlattenableLoop(current)) EmitFlatLoop(current, output);
                else output.Add((IBirStatement)current);
            }
            return output.ToArray();
        }
        void EmitFlatLoop(BirGraphValue node, List<IBirStatement> output)
        {
            var (variable, loopLabel, body) = node switch {
                BirFor loop => (loop.Var.OrDefault(), loop.Label.OrDefault(), loop.Body.OrEmpty()),
                BirForArray loop => (loop.Var.OrDefault(), loop.Label.OrDefault(), loop.Body.OrEmpty()),
                BirForEach loop => (loop.Var.OrDefault(), loop.Label.OrDefault(), loop.Body.OrEmpty()),
                _ => throw new InvalidOperationException("Not a flattenable loop"),
            };
            var cont = NextLabel(); var end = NextLabel(); var start = NextLabel();
            for (var index = 0; index < body.Length; index++)
                body[index] = (IBirStatement)RewriteBreakContinue((BirGraphValue)body[index], loopLabel, cont, end, false);
            var count = ++_loopCounter;
            if (node is BirFor counted)
            {
                var comparison = counted.Cmp.OrDefault();
                if (comparison is not ("<=" or "<" or ">=")) throw new NotSupportedException(
                    $"suspend-lowering: counted for in '{(_ownerClass ?? _fileClass)}.{_name}' carries unsupported cmp '{comparison}'.");
                if (!counted.Step.HasValue) throw new NotSupportedException(
                    $"suspend-lowering: counted for in '{(_ownerClass ?? _fileClass)}.{_name}' carries no integer step.");
                output.Add(Var(variable, IntTn, counted.From.OrDefault())); output.Add(Label(start));
                output.Add(BrIf(Bin(comparison, Local(variable), counted.To.OrDefault()), false, end)); output.AddRange(body);
                output.Add(Label(cont)); output.Add(SetLocal(variable, Bin("+", Local(variable), IntConst(counted.Step.Value))));
                output.Add(Goto(start)); output.Add(Label(end)); return;
            }
            if (node is BirForArray array)
            {
                var element = array.Elem.OrDefault() ?? throw new NotSupportedException(
                    $"suspend-lowering: forArray in '{(_ownerClass ?? _fileClass)}.{_name}' carries no `elem` — cannot flatten (ArrayConstructionLowering should have stamped it before the cold pass).");
                var arrayName = "__arr$" + count; var indexName = "__i$" + count;
                output.Add(Var(arrayName, new TypeNode.Array(element), array.Array.OrDefault()));
                output.Add(Var(indexName, IntTn, IntConst(0))); output.Add(Label(start));
                output.Add(BrIf(Bin("<", Local(indexName), new BirArrayLength { Array = Local(arrayName) }), false, end));
                output.Add(Var(variable, element, new BirArrayGet { Elem = element, Array = Local(arrayName), Index = Local(indexName) }));
                output.AddRange(body); output.Add(Label(cont)); output.Add(SetLocal(indexName, Bin("+", Local(indexName), IntConst(1))));
                output.Add(Goto(start)); output.Add(Label(end)); return;
            }
            var each = (BirForEach)node;
            var eachElement = each.Elem.OrDefault() ?? AnyTn;
            var enumerator = "__en$" + count;
            var enumerableType = new TypeNode.Fqn("System.Collections.IEnumerable");
            var enumeratorType = new TypeNode.Fqn("System.Collections.IEnumerator");
            output.Add(Var(enumerator, enumeratorType, new BirNativeInstance { Type = enumerableType, Method = "GetEnumerator",
                ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = enumeratorType,
                Recv = new BirCast { Type = enumerableType, E = each.Src } }));
            output.Add(Label(start));
            output.Add(BrIf(new BirNativeInstance { Type = enumeratorType, Method = "MoveNext", ArgTypes = Array.Empty<TypeNode>(),
                Args = Array.Empty<IBirExpression>(), Recv = Local(enumerator), Ret = BoolTn }, false, end));
            output.Add(Var(variable, eachElement, new BirCast { Type = eachElement,
                E = new BirNativeInstance { Type = enumeratorType, Method = "get_Current", ArgTypes = Array.Empty<TypeNode>(),
                    Args = Array.Empty<IBirExpression>(), Recv = Local(enumerator), Ret = AnyTn } }));
            output.AddRange(body); output.Add(Label(cont)); output.Add(Goto(start)); output.Add(Label(end));
        }

        BirGraphValue RewriteBreakContinue(BirGraphValue node, string loopLabel, int cont, int end, bool insideNestedLoop)
        {
            if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node)) return node;
            if (node is BirBreak or BirContinue)
            {
                var label = node is BirBreak breaking ? breaking.Label.OrDefault() : ((BirContinue)node).Label.OrDefault();
                if (label != null ? label == loopLabel : !insideNestedLoop) return Goto(node is BirBreak ? end : cont);
                return node;
            }
            var nested = insideNestedLoop || IsStructuredLoop(node);
            node.VisitChildSlots(slot => slot.Replace(RewriteBreakContinue(slot.Value, loopLabel, cont, end, nested)));
            return node;
        }

        IBirStatement[] HoistSuspendingCatches(IBirStatement[] body)
        {
            if (!body.Any(statement => SourceAny((BirGraphValue)statement, node => node is BirTry region && IsHoistableTrySource(region)))) return body;
            return HoistStmtList(body);
        }
        IBirStatement[] HoistStmtList(IBirStatement[] body)
        {
            var output = new List<IBirStatement>();
            foreach (var statement in body)
            {
                var current = NormalizeChildren((BirGraphValue)statement, HoistStmtList);
                if (current is BirTry region && IsHoistableTrySource(region)) EmitHoistedTry(region, output);
                else output.Add((IBirStatement)current);
            }
            return output.ToArray();
        }
        void EmitHoistedTry(BirTry region, List<IBirStatement> output)
        {
            var catches = new List<BirCatch>(); var tail = new List<IBirStatement>();
            foreach (var handler in region.Catches.OrEmpty())
            {
                var body = handler.Body.OrEmpty();
                if (!HasOwnSuspensionSource(body)) { catches.Add(handler); continue; }
                var count = ++_excCounter; var exception = "__exc$" + count; var caught = "__caught$" + count;
                var sourceName = handler.Var.OrDefault(); var exceptionType = handler.ExcType.OrDefault() ?? AnyTn;
                var nullable = new TypeNode.Nullable(exceptionType);
                output.Add(Var(exception, nullable, NullConst(nullable)));
                // The original handler descendants move into the hoisted tail;
                // only the small recorder is a new authored catch declaration.
                catches.Add(new BirCatch { ExcType = handler.ExcType, Var = caught,
                    Body = new IBirStatement[] { SetLocal(exception, Local(caught)) } });
                var skip = NextLabel(); tail.Add(BrIf(Bin("===", Local(exception), NullConst(AnyTn)), true, skip));
                foreach (var statement in body)
                {
                    if (sourceName != null) SubstLocalName((BirGraphValue)statement, sourceName, exception);
                    tail.Add(statement);
                }
                tail.Add(Label(skip));
            }
            var originalBody = region.Body.OrEmpty(); var cleanup = region.Finally.OrEmpty();
            region.Catches = catches.ToArray(); region.Finally = default; region.SuspendFnTypePre = default;
            if (cleanup.Length == 0) { output.Add(region); output.AddRange(tail); return; }
            var protectedBody = new List<IBirStatement>();
            if (catches.Count != 0) protectedBody.Add(region); else protectedBody.AddRange(originalBody);
            protectedBody.AddRange(tail);
            if (!HasOwnSuspensionSource(cleanup))
            {
                output.Add(new BirTry { Type = region.Type, Body = protectedBody.ToArray(), Catches = Array.Empty<BirCatch>(), Finally = cleanup });
                return;
            }
            EmitSuspendingFinally(protectedBody.ToArray(), cleanup, region.Type, output);
        }

        void EmitSuspendingFinally(IBirStatement[] body, IBirStatement[] cleanup, OptionalField<TypeNode> type, List<IBirStatement> output)
        {
            var count = ++_excCounter; var pending = "__pendingExc$" + count; var caught = "__pendingCaught$" + count;
            var route = NextLabel(); var throwable = new TypeNode.Fqn("kotlin.Throwable");
            output.Add(Var(pending, new TypeNode.Nullable(throwable), NullConst(new TypeNode.Nullable(throwable))));
            var labels = new HashSet<int>();
            foreach (var statement in body) CollectProtectedLabels((BirGraphValue)statement, labels);
            var outgoing = new Dictionary<int, int>(); var loopExit = "__loopExit$" + count;
            for (var index = 0; index < body.Length; index++) body[index] = (IBirStatement)RouteProtectedLoopExits((BirGraphValue)body[index], labels, outgoing, loopExit, route);
            if (outgoing.Count != 0) output.Add(Var(loopExit, IntTn, IntConst(0)));
            var hasReturn = body.Any(statement => SourceAny((BirGraphValue)statement, node => node is BirReturn or BirReturnExpression));
            var returning = "__returning$" + count; var returnValue = "__returnValue$" + count;
            if (hasReturn)
            {
                var valueType = IsUnitTn(_resultType) ? AnyTn : _resultType;
                output.Add(Var(returning, BoolTn, BoolConst(false))); output.Add(Var(returnValue, valueType, DefaultOf(valueType)));
                body = RouteFunctionReturns(body, returning, returnValue, valueType, route);
            }
            var protectedTry = new BirTry { Body = body, Catches = new[] { new BirCatch { ExcType = throwable, Var = caught,
                Body = new IBirStatement[] { SetLocal(pending, Local(caught)) } } } };
            if (type.HasValue) protectedTry.Type = type;
            output.Add(protectedTry); output.Add(Label(route)); output.AddRange(cleanup);
            var noPending = NextLabel(); output.Add(BrIf(Bin("===", Local(pending), NullConst(AnyTn)), true, noPending));
            output.Add(new BirThrow { Value = Local(pending) }); output.Add(Label(noPending));
            if (hasReturn)
            {
                var noReturn = NextLabel(); output.Add(BrIf(Local(returning), false, noReturn)); output.Add(Ret(Local(returnValue))); output.Add(Label(noReturn));
            }
            foreach (var (target, completion) in outgoing)
            {
                var next = NextLabel(); output.Add(BrIf(Bin("==", Local(loopExit), IntConst(completion)), false, next));
                output.Add(Goto(target)); output.Add(Label(next));
            }
        }

        static void CollectProtectedLabels(BirGraphValue node, HashSet<int> labels)
        {
            if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node)) return;
            if (node is BirLabel { Id.HasValue: true } label) labels.Add(label.Id.Value);
            node.VisitChildren(child => CollectProtectedLabels(child, labels));
        }
        BirGraphValue RouteProtectedLoopExits(BirGraphValue node, HashSet<int> localLabels,
            Dictionary<int, int> outgoing, string completion, int route)
        {
            if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node)) return node;
            var target = node switch { BirGoto jump => jump.Id, BirBranchIf branch => branch.Id, _ => default };
            if (target.HasValue && !localLabels.Contains(target.Value))
            {
                if (!outgoing.TryGetValue(target.Value, out var state)) outgoing.Add(target.Value, state = outgoing.Count + 1);
                var statements = new List<IBirStatement>(); var skip = node is BirBranchIf ? NextLabel() : -1;
                if (node is BirBranchIf branch) statements.Add(BrIf((IBirExpression)RouteProtectedLoopExits((BirGraphValue)branch.Cond.OrDefault(),
                    localLabels, outgoing, completion, route), !branch.On.Value, skip));
                statements.Add(SetLocal(completion, IntConst(state))); statements.Add(Goto(route));
                if (node is BirBranchIf) statements.Add(Label(skip));
                return new BirBlock { Body = statements.ToArray() };
            }
            node.VisitChildSlots(slot => slot.Replace(RouteProtectedLoopExits(slot.Value, localLabels, outgoing, completion, route)));
            return node;
        }
        IBirStatement[] RouteFunctionReturns(IBirStatement[] body, string returning, string value, TypeNode valueType, int route)
        {
            var output = new List<IBirStatement>();
            foreach (var statement in body)
            {
                if (statement is BirReturn result)
                {
                    output.Add(SetLocal(value, result.Value.HasValue ? (IBirExpression)RouteReturnsNode((BirGraphValue)result.Value.Value,
                        returning, value, valueType, route) : DefaultOf(valueType)));
                    output.Add(SetLocal(returning, BoolConst(true))); output.Add(Goto(route));
                }
                else output.Add((IBirStatement)RouteReturnsNode((BirGraphValue)statement, returning, value, valueType, route));
            }
            return output.ToArray();
        }
        BirGraphValue RouteReturnsNode(BirGraphValue node, string returning, string value, TypeNode valueType, int route)
        {
            if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node)) return node;
            if (node is BirReturnExpression expression)
                return new BirValueBlock { Stmts = RouteFunctionReturns(new IBirStatement[] { new BirReturn { Value = expression.Value } },
                    returning, value, valueType, route), Result = new BirThrowExpression { Value = NullConst(AnyTn) } };
            node.VisitSourceChildren(slot => slot.Replace(RouteReturnsNode(slot.Value, returning, value, valueType, route)), region => {
                var statements = RouteFunctionReturns(region.Statements, returning, value, valueType, route);
                (region.ReplaceStatements ?? throw new InvalidOperationException("A suspend return region has no owning statement setter"))(statements);
                if (region.Result is { } result) result.Replace(RouteReturnsNode(result.Value, returning, value, valueType, route));
            });
            return node;
        }
        static void SubstLocalName(BirGraphValue node, string original, string replacement)
        {
            if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node)) return;
            if (node is BirLocal local && local.Name.OrDefault() == original) local.Name = replacement;
            if (node is BirSetLocal store && store.Name.OrDefault() == original) store.Name = replacement;
            node.VisitChildren(child => SubstLocalName(child, original, replacement));
        }
        static BirVariable Var(string name, TypeNode type, IBirExpression value) => new() {
            Name = name, Type = BirGraphValue.CopyType(type), Init = OptionalField<IBirExpression>.Of(value) };
        static BirSetLocal SetLocal(string name, IBirExpression value) => new() { Name = name, Value = OptionalField<IBirExpression>.Of(value) };
        static BirBinaryOperation Bin(string operation, IBirExpression left, IBirExpression right) => new() {
            Op = operation, Lhs = OptionalField<IBirExpression>.Of(left), Rhs = OptionalField<IBirExpression>.Of(right) };

        void AssertLocalsResolved(IBirStatement[] invoke)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal) { ResumeResultParameter }; var used = new List<string>();
            void Collect(BirGraphValue node)
            {
                if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node)) return;
                if (node is BirVariable { Name.HasValue: true } variable) declared.Add(variable.Name.Value);
                if (node is BirCatch { Var.HasValue: true } handler) declared.Add(handler.Var.Value);
                var binder = node switch { BirForArray loop => loop.Var.OrDefault(), BirForEach loop => loop.Var.OrDefault(),
                    BirFor loop => loop.Var.OrDefault(), BirForRange loop => loop.Var.OrDefault(), BirRepeat loop => loop.Var.OrDefault(), _ => null };
                if (binder != null) declared.Add(binder);
                if (node is BirLocal { Name.HasValue: true } local) used.Add(local.Name.Value);
                if (node is BirSetLocal { Name.HasValue: true } store) used.Add(store.Name.Value);
                node.VisitChildren(Collect);
            }
            foreach (var statement in invoke) Collect((BirGraphValue)statement);
            foreach (var name in used)
                if (!declared.Contains(name)) throw new InvalidOperationException(
                    $"bir2cir suspend-lowering: SM '{_smType}' invokeSuspend for '{(_ownerClass ?? _fileClass)}.{_name}' references unspilled local '{name}' "
                    + "(neither the resume-result param, an SM `var`, nor a catch/loop var). A splice-generated local crossing a resume point was not spilled into an SM field — it would reach ilemit as `load unknown var`.");
        }
    }
}
