using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    static void ResolveNativeTemplatesSource(BirGraphValue node)
    {
        switch (node)
        {
            case BirNewList value: ResolveNativeList(value); break;
            case BirSpreadConcat value: ResolveNativeSpread(value); break;
            case BirForEach value: ResolveNativeEnumeration(value); break;
            case BirEventAccessorBody value: ResolveNativeEventBody(value); break;
            case BirNullableNull value:
                if (!value.CtorRef.IsPresent) value.CtorRef = NativeNullableReference(value.Elem.OrDefault(), "nullableNull");
                break;
            case BirNullableWrap value:
                if (!value.CtorRef.IsPresent) value.CtorRef = NativeNullableReference(value.Elem.OrDefault(), "nullableWrap");
                break;
            case BirNullableHasValue value:
                if (!value.HasValueRef.IsPresent) value.HasValueRef = NativeNullableReference(value.Elem.OrDefault(), "nullableHasValue");
                break;
            case BirNullableValue value:
                if (!value.ValueRef.IsPresent) value.ValueRef = NativeNullableReference(value.Elem.OrDefault(), "nullableValue");
                break;
            case BirSafeValueCast value:
                if (!value.CtorRef.IsPresent) value.CtorRef = NativeNullableReference(value.Elem.OrDefault(), "nullableWrap");
                break;
            case BirDelegateInvoke value:
                if (!value.InvokeRef.IsPresent)
                    value.InvokeRef = NativeInvokeReference(value.FuncType.OrDefault(), "funcType");
                break;
            case BirNewArrayInit value:
                if (!value.InvokeRef.IsPresent)
                    value.InvokeRef = NativeInvokeReference(value.FuncType.OrDefault()
                        ?? NativeInitializerFunction(value.Init.OrDefault() as BirGraphValue), "init");
                break;
            case BirNewDelegate value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(value.FuncType.OrDefault(), "funcType");
                break;
            case BirNewClosure value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(value.FuncType.OrDefault(), "funcType");
                break;
            case BirNewBoundDelegate value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(value.FuncType.OrDefault(), "funcType");
                if (!value.InvokeRef.IsPresent)
                    value.InvokeRef = NativeInvokeReference(value.FuncType.OrDefault(), "funcType");
                break;
            case BirBoundClrDelegate value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(value.FuncType.OrDefault()
                        ?? value.ClrType.OrDefault() ?? value.Type.OrDefault(), "type");
                break;
            case BirClrStaticDelegate value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(value.FuncType.OrDefault()
                        ?? value.ClrType.OrDefault() ?? value.Type.OrDefault(), "type");
                break;
        }
    }

    static void ResolveNativeList(BirNewList node)
    {
        if (node.CtorRef.IsPresent) return;
        var template = CollectionTemplates.First(template => template.Kind == "newList");
        var element = node.Elem.OrDefault() ?? throw new InvalidOperationException(
            "bir2cir: newList states no readable elem and so names no constructed type");
        var arguments = new[] { element };
        var open = ResolveOwnerType(new TypeNode.Fqn(template.Owner, arguments))
            ?? throw new InvalidOperationException(
                $"bir2cir: newList constructs '{template.Owner}', which does not resolve to a .NET type");
        var constructors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(candidate => candidate.GetParameters().Length == 0).ToList();
        var constructor = ClrDeclarationMatching.TryPickUniqueCtor(_refs, constructors, new List<TypeNode>(), arguments)
            ?? throw new InvalidOperationException(
                $"bir2cir: '{template.Owner}' has no unique parameterless constructor for newList");
        node.CtorRef = NativeReference(constructor, MemberRefNode.Kinds.Ctor, open, arguments);
        // Add(!0) is the declaration's own vector, not an inferred value type.
        var signature = Enumerable.Range(0, template.Arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToList();
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == template.Accumulator && method.GetParameters().Length == signature.Count).ToList();
        var accumulator = ClrDeclarationMatching.TryPickUnique(_refs, candidates, signature, arguments)
            ?? throw new InvalidOperationException(
                $"bir2cir: '{template.Owner}.{template.Accumulator}' does not resolve to one declaration for newList");
        node.AddRef = NativeReference(accumulator, MemberRefNode.Kinds.Method, open, arguments);
    }

    static void ResolveNativeSpread(BirSpreadConcat node)
    {
        if (node.CtorRef.IsPresent) return;
        var element = node.Elem.OrDefault() ?? throw new InvalidOperationException(
            "bir2cir: spreadConcat states no readable elem and so names no accumulator type");
        var arguments = new[] { element };
        var open = ResolveOwnerType(new TypeNode.Fqn(SpreadOwner, arguments))
            ?? throw new InvalidOperationException(
                $"bir2cir: spreadConcat accumulates into '{SpreadOwner}', which does not resolve to a .NET type");
        var constructors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(candidate => candidate.GetParameters().Length == 0).ToList();
        node.CtorRef = NativeReference(ClrDeclarationMatching.TryPickUniqueCtor(_refs, constructors, new List<TypeNode>(), arguments)
            ?? throw Missing(SpreadOwner, ".ctor"), MemberRefNode.Kinds.Ctor, open, arguments);
        var slot = new TypeNode.Tv("type", 0);
        node.AddRef = NativeSpreadMember(open, arguments, "Add", new List<TypeNode> { slot });
        node.AddRangeRef = NativeSpreadMember(open, arguments, "AddRange", new List<TypeNode> {
            new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { slot }),
        });
        node.ToArrayRef = NativeSpreadMember(open, arguments, "ToArray", new List<TypeNode>());
    }

    static BirMemberReference NativeSpreadMember(Type open, TypeNode[] arguments, string name, List<TypeNode> signature)
    {
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == signature.Count).ToList();
        return NativeReference(ClrDeclarationMatching.TryPickUnique(_refs, candidates, signature, arguments) ?? throw Missing(SpreadOwner, name),
            MemberRefNode.Kinds.Method, open, arguments);
    }

    static void ResolveNativeEnumeration(BirForEach node)
    {
        if (node.MoveNextRef.IsPresent) return;
        var element = node.Elem.OrDefault() ?? throw new InvalidOperationException(
            "bir2cir: forEachInline states no readable elem and so names no enumerator protocol");
        // Both physical arms are required: the emitter chooses between exact
        // named operands, never between competing reflection declarations.
        node.EnumerableGetRef = NativeProtocolReference("System.Collections.Generic.IEnumerable", new[] { element }, "GetEnumerator");
        node.CurrentRef = NativeProtocolReference("System.Collections.Generic.IEnumerator", new[] { element }, "get_Current");
        node.EnumerableGetErasedRef = NativeProtocolReference("System.Collections.IEnumerable", Array.Empty<TypeNode>(), "GetEnumerator");
        node.CurrentErasedRef = NativeProtocolReference("System.Collections.IEnumerator", Array.Empty<TypeNode>(), "get_Current");
        node.MoveNextRef = NativeProtocolReference("System.Collections.IEnumerator", Array.Empty<TypeNode>(), "MoveNext");
    }

    static BirMemberReference NativeProtocolReference(string name, TypeNode[] arguments, string member)
    {
        var owner = new TypeNode.Fqn(name, arguments.Length == 0 ? null : arguments);
        var open = ResolveOwnerType(owner) ?? throw new InvalidOperationException(
            $"bir2cir: forEachInline walks '{name}', which does not resolve to a .NET type");
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == member && method.GetParameters().Length == 0).ToList();
        var selected = ClrDeclarationMatching.TryPickUnique(_refs, candidates, new List<TypeNode>(), arguments) ?? throw new InvalidOperationException(
            $"bir2cir: '{name}.{member}' does not resolve to one declaration for forEachInline");
        return NativeReference(selected, MemberRefNode.Kinds.Method, open, arguments);
    }

    static void ResolveNativeEventBody(BirEventAccessorBody node)
    {
        if (node.Kind.OrDefault() == "raise")
        {
            if (!node.InvokeRef.IsPresent) node.InvokeRef = NativeInvokeReference(node.DelegateType.OrDefault(), "delegateType");
            return;
        }
        if (node.CombineRef.IsPresent || node.Kind.OrDefault() is not ("add" or "remove") || !node.DelegateType.HasValue) return;
        var delegateType = new TypeNode.Fqn("System.Delegate");
        var open = ResolveOwnerType(delegateType) ?? throw new InvalidOperationException(
            "bir2cir: 'System.Delegate' does not resolve to a .NET type");
        var signature = new List<TypeNode> { delegateType, delegateType };
        BirMemberReference Select(string name)
        {
            var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == name && method.GetParameters().Length == 2).ToList();
            var selected = ClrDeclarationMatching.TryPickUnique(_refs, candidates, signature, Array.Empty<TypeNode>()) ?? throw new InvalidOperationException(
                $"bir2cir: 'System.Delegate.{name}(Delegate, Delegate)' does not resolve to one declaration");
            return NativeReference(selected, MemberRefNode.Kinds.Method, open, Array.Empty<TypeNode>());
        }
        node.CombineRef = Select("Combine");
        node.RemoveRef = Select("Remove");
        var interlocked = ResolveOwnerType(new TypeNode.Fqn("System.Threading.Interlocked"))
            ?? throw new InvalidOperationException("bir2cir: 'System.Threading.Interlocked' does not resolve");
        var exchange = interlocked.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "CompareExchange" && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 3).ToList();
        if (exchange.Count != 1) throw new InvalidOperationException(
            $"bir2cir: 'Interlocked.CompareExchange<T>' resolves to {exchange.Count} declarations, not one");
        node.CompareExchangeRef = NativeReference(exchange[0], MemberRefNode.Kinds.Method, interlocked, Array.Empty<TypeNode>());
    }

    static OptionalField<BirMemberReference> NativeNullableReference(TypeNode element, string kind)
    {
        if (element == null) return default;
        var arguments = new[] { element };
        var open = ResolveOwnerType(new TypeNode.Fqn(NullableFqn, arguments)) ?? throw new InvalidOperationException(
            $"bir2cir: {kind} wraps '{NullableFqn}', which does not resolve to a .NET type");
        if (kind is "nullableNull" or "nullableWrap")
        {
            var constructors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => candidate.GetParameters().Length == 1).ToList();
            var selected = ClrDeclarationMatching.TryPickUniqueCtor(_refs, constructors, new List<TypeNode> { new TypeNode.Tv("type", 0) }, arguments)
                ?? throw new InvalidOperationException($"bir2cir: '{NullableFqn}' has no unique one-argument constructor for {kind}");
            return NativeReference(selected, MemberRefNode.Kinds.Ctor, open, arguments);
        }
        var name = kind == "nullableHasValue" ? "get_HasValue" : "get_Value";
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == 0).ToList();
        var accessor = ClrDeclarationMatching.TryPickUnique(_refs, candidates, new List<TypeNode>(), arguments) ?? throw new InvalidOperationException(
            $"bir2cir: '{NullableFqn}.{name}' does not resolve to one declaration for {kind}");
        return NativeReference(accessor, MemberRefNode.Kinds.PropertyAccessor, open, arguments);
    }

    static BirMemberReference NativeInvokeReference(TypeNode type, string key)
        => type == null ? throw new InvalidOperationException(
            $"bir2cir: a function-type call states no type under funcType/clrType/{key}")
            : BirMemberReference.FromPhysical(DelegateInvokeReference(type));

    static BirMemberReference NativeDelegateConstructor(TypeNode type, string key)
        => type == null ? throw new InvalidOperationException(
            $"bir2cir: a delegate construction states no function type under funcType/clrType/{key}")
            : BirMemberReference.FromPhysical(DelegateConstructorReference(type));

    // This is solely the old array-initializer's own funcType -> own sty read.
    // It is not a general expression type inference or executable subtree walk.
    static TypeNode NativeInitializerFunction(BirGraphValue initializer)
    {
        TypeNode function = null, stamp = null;
        initializer?.VisitOwnTypeFields((field, type) => {
            if (field == "funcType") function = type;
            if (field == "sty") stamp = type;
        });
        return function ?? stamp;
    }

    static BirMemberReference NativeReference(MethodBase selected, string kind, Type owner, TypeNode[] arguments)
        => BirMemberReference.FromPhysical(ClrMemberReferences.MemberRefOf(_refs, _localTypes, selected, kind, owner, arguments));
}
