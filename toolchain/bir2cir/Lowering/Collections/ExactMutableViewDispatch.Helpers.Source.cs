using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExactMutableViewDispatch
{
    internal static bool TryCount(BirCallStatic call, ReferenceMetadataIndex refs, out IBirExpression rewritten)
    {
        rewritten = null;
        if (call.Method.OrDefault() != "projectedViewCountErased"
            || call.Owner.OrDefault() is not TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt" }
            || call.Args.OrDefault() is not { Length: 3 } arguments
            || arguments[1] is not BirClassReference { Type.Value: TypeNode.Fqn view }
            || arguments[2] is not BirConstant { Value.Value: BirLiteral.IntegerLiteral literal }
            || literal.Value is not (4 or 5 or 6) || !IsInvariant(view, refs)
            || !refs.TryForeignStarMethod(view, "get_Count", null, 0, Array.Empty<TypeNode>(),
                out _, out var declaring, out _, out _, out _, out _, out _, out var method)) return false;
        var receiver = arguments[0];
        var declaration = CloseDeclaring((TypeNode.Fqn)declaring, view.Args);
        rewritten = Wrap(receiver, view, (int)literal.Value, call.Ret.OrDefault() ?? new TypeNode.Fqn("kotlin.Int"),
            local => ClrMemberResolution.ExactInstanceCall(null, refs, method, declaration, local, Array.Empty<IBirExpression>()),
            local => {
                // Move the original receiver into Wrap's sole initializer;
                // reuse this call only after replacing that owning child.
                call.Args = new IBirExpression[] { local, arguments[1], arguments[2] };
                return call;
            });
        return true;
    }

    internal static bool TryListMutation(BirCallStatic call, ReferenceMetadataIndex refs, out IBirExpression rewritten)
    {
        rewritten = null;
        if (call.Owner.OrDefault() is not TypeNode.Fqn { Name: "kotlin.collections.ClrCollectionDefaultsKt" }
            || call.Args.OrDefault() is not { Length: >= 3 } arguments
            || arguments[1] is not BirClassReference { Type.Value: TypeNode.Fqn view }
            || !IsInvariant(view, refs)) return false;
        var operation = call.Method.OrDefault();
        var hasIndex = operation is "clrProjectedListSet" or "clrProjectedListRemoveAt" or "clrProjectedListAddAt";
        var hasElement = operation is "clrProjectedListSet" or "clrProjectedListAdd" or "clrProjectedListAddAt";
        if (!(hasIndex || hasElement) || arguments.Length != 2 + (hasIndex ? 1 : 0) + (hasElement ? 1 : 0)) return false;
        var indexType = new TypeNode.Fqn("System.Int32");
        var nativeName = operation switch {
            "clrProjectedListSet" => "set_Item", "clrProjectedListRemoveAt" => "RemoveAt",
            "clrProjectedListAddAt" => "Insert", _ => "Add",
        };
        var signature = (hasIndex ? new[] { indexType } : Array.Empty<TypeNode>())
            .Concat(hasElement ? view.Args : Array.Empty<TypeNode>()).ToArray();
        if (!refs.TryForeignStarMethod(view, nativeName, null, 0, signature,
            out _, out var declaring, out _, out _, out _, out _, out _, out var mutation)) return false;
        var mutationOwner = CloseDeclaring((TypeNode.Fqn)declaring, view.Args);
        var queryName = operation == "clrProjectedListAdd" ? "get_Count" : "get_Item";
        var needsQuery = operation != "clrProjectedListAddAt";
        System.Reflection.MethodInfo query = null;
        TypeNode queryOwner = null;
        if (needsQuery && !refs.TryForeignStarMethod(view, queryName, null, 0,
            hasIndex ? new[] { indexType } : Array.Empty<TypeNode>(),
            out _, out queryOwner, out _, out _, out _, out _, out _, out query)) return false;
        var helperArguments = call.TypeArgs.Value;
        TypeNode HelperSlot(TypeNode type) => type switch {
            TypeNode.Tv { Scope: "method" } variable => helperArguments[variable.I],
            TypeNode.Fqn { Args: { } nested } named => new TypeNode.Fqn(named.Name, nested.Select(HelperSlot).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(HelperSlot(n.Of)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(HelperSlot(o.Of)),
            TypeNode.Array a => new TypeNode.Array(HelperSlot(a.Elem), a.Rank, a.SzArray),
            _ => type,
        };
        // As in the original late owner, capture ALL supplied values before
        // any native getter/mutation. The cached guard is inside this prefix.
        var statements = new List<IBirStatement>();
        var captured = arguments.ToArray();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (index == 1) continue; // immutable compiler-owned closed classRef.
            var name = CallEvalLowering.FreshBindingId();
            statements.Add(new BirVariable { Name = name, Type = HelperSlot(call.Sig.Value[index]),
                Init = E(arguments[index]) });
            captured[index] = new BirLocal { Name = name };
        }
        IBirExpression[] NativeArguments() => captured.Skip(2).Select(CloneRead).ToArray();
        IBirExpression Query(IBirExpression receiver) => ClrMemberResolution.ExactInstanceCall(null, refs, query,
            CloseDeclaring((TypeNode.Fqn)queryOwner, view.Args), receiver,
            hasIndex ? new[] { CloneRead(captured[2]) } : Array.Empty<IBirExpression>());
        TypeNode slot = operation switch {
            "clrProjectedListAdd" => new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinMutableCollectionAddSlot"),
            "clrProjectedListSet" => new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinMutableListSetSlot"),
            "clrProjectedListRemoveAt" => new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinMutableListRemoveAtSlot"),
            _ => null,
        };
        if (!refs.TryResolveStaticMemberSignature("kotlin.collections.ClrCollectionDefaultsKt", operation,
            helperArguments.Length, true, call.Sig.Value, null, out _, out var helperDeclaration, out _)) return false;
        var helperResult = HelperSlot(ClrMemberResolution.DeclaredMethodReturn(null, refs, helperDeclaration));
        var resultType = helperResult is TypeNode.Fqn { Name: "void" or "System.Void" }
            ? helperResult : call.Ret.OrDefault() ?? helperResult;
        var dispatch = Wrap(captured[0], view, 6, resultType,
            receiver => {
                var native = ClrMemberResolution.ExactInstanceCall(null, refs, mutation, mutationOwner,
                    CloneRead(receiver), NativeArguments());
                if (!needsQuery) return native;
                var old = CallEvalLowering.FreshBindingId();
                var isAppend = operation == "clrProjectedListAdd";
                return new BirValueBlock { Stmts = new IBirStatement[] {
                    new BirVariable { Name = old, Type = isAppend ? indexType : new TypeNode.Nullable(Any),
                        Init = E(Query(CloneRead(receiver))) },
                    new BirExpressionStatement { Expr = E(native) },
                }, Result = E(isAppend
                    ? new BirBinaryOperation { Op = "!=", Lhs = E(Query(CloneRead(receiver))), Rhs = E(new BirLocal { Name = old }) }
                    : new BirLocal { Name = old }) };
            }, receiver => {
                var fallback = captured.Select(CloneRead).ToArray();
                fallback[0] = receiver;
                call.Args = fallback;
                return call;
            }, slot);
        rewritten = new BirValueBlock { Stmts = statements.ToArray(), Result = E(dispatch) };
        return true;

        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        // Only newly authored local reads or the immutable classRef are copied;
        // side-effecting/registered supplied operands moved to the prefix above.
        static IBirExpression CloneRead(IBirExpression value) => (IBirExpression)((BirGraphValue)value).DeepClone();
    }
}
