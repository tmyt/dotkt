using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    static void ResolveNativeTemplatesSource(ReferenceMetadataIndex bindingReferences, BirGraphValue node)
    {
        switch (node)
        {
            case BirNewList value: ResolveNativeList(bindingReferences, value); break;
            case BirSpreadConcat value: ResolveNativeSpread(bindingReferences, value); break;
            case BirForEach value: ResolveNativeEnumeration(bindingReferences, value); break;
            case BirEventAccessorBody value: ResolveNativeEventBody(bindingReferences, value); break;
            case BirNullableNull value:
                if (!value.CtorRef.IsPresent) value.CtorRef = NativeNullableReference(bindingReferences, value.Elem.OrDefault(), "nullableNull");
                break;
            case BirNullableWrap value:
                if (!value.CtorRef.IsPresent) value.CtorRef = NativeNullableReference(bindingReferences, value.Elem.OrDefault(), "nullableWrap");
                break;
            case BirNullableHasValue value:
                if (!value.HasValueRef.IsPresent) value.HasValueRef = NativeNullableReference(bindingReferences, value.Elem.OrDefault(), "nullableHasValue");
                break;
            case BirNullableValue value:
                if (!value.ValueRef.IsPresent) value.ValueRef = NativeNullableReference(bindingReferences, value.Elem.OrDefault(), "nullableValue");
                break;
            case BirSafeValueCast value:
                if (!value.CtorRef.IsPresent) value.CtorRef = NativeNullableReference(bindingReferences, value.Elem.OrDefault(), "nullableWrap");
                break;
            case BirDelegateInvoke value:
                if (!value.InvokeRef.IsPresent)
                    value.InvokeRef = NativeInvokeReference(bindingReferences, value.FuncType.OrDefault(), "funcType");
                break;
            case BirNewArrayInit value:
                if (!value.InvokeRef.IsPresent)
                    value.InvokeRef = NativeInvokeReference(bindingReferences, value.FuncType.OrDefault()
                        ?? NativeInitializerFunction(value.Init.OrDefault() as BirGraphValue), "init");
                break;
            case BirNewDelegate value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(bindingReferences, value.FuncType.OrDefault(), "funcType");
                break;
            case BirNewClosure value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(bindingReferences, value.FuncType.OrDefault(), "funcType");
                break;
            case BirNewBoundDelegate value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(bindingReferences, value.FuncType.OrDefault(), "funcType");
                if (!value.InvokeRef.IsPresent)
                    value.InvokeRef = NativeInvokeReference(bindingReferences, value.FuncType.OrDefault(), "funcType");
                break;
            case BirBoundClrDelegate value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(bindingReferences, value.FuncType.OrDefault()
                        ?? value.ClrType.OrDefault() ?? value.Type.OrDefault(), "type");
                break;
            case BirClrStaticDelegate value:
                if (!value.DelegateCtorRef.IsPresent)
                    value.DelegateCtorRef = NativeDelegateConstructor(bindingReferences, value.FuncType.OrDefault()
                        ?? value.ClrType.OrDefault() ?? value.Type.OrDefault(), "type");
                break;
        }
    }

    static void ResolveNativeList(ReferenceMetadataIndex bindingReferences, BirNewList node)
    {
        if (node.CtorRef.IsPresent) return;
        var template = CollectionTemplates.First(template => template.Kind == "newList");
        var element = node.Elem.OrDefault() ?? throw new InvalidOperationException(
            "bir2cir: newList states no readable elem and so names no constructed type");
        var arguments = new[] { element };
        var open = ResolveOwnerType(new TypeNode.Fqn(template.Owner, arguments), refs: bindingReferences)
            ?? throw new InvalidOperationException(
                $"bir2cir: newList constructs '{template.Owner}', which does not resolve to a .NET type");
        var constructors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(candidate => candidate.GetParameters().Length == 0).ToList();
        var constructor = ClrDeclarationMatching.TryPickUniqueCtor(bindingReferences, constructors, new List<TypeNode>(), arguments)
            ?? throw new InvalidOperationException(
                $"bir2cir: '{template.Owner}' has no unique parameterless constructor for newList");
        node.CtorRef = NativeReference(bindingReferences, constructor, MemberRefNode.Kinds.Ctor, open, arguments);
        // Add(!0) is the declaration's own vector, not an inferred value type.
        var signature = Enumerable.Range(0, template.Arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToList();
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == template.Accumulator && method.GetParameters().Length == signature.Count).ToList();
        var accumulator = ClrDeclarationMatching.TryPickUnique(bindingReferences, candidates, signature, arguments)
            ?? throw new InvalidOperationException(
                $"bir2cir: '{template.Owner}.{template.Accumulator}' does not resolve to one declaration for newList");
        node.AddRef = NativeReference(bindingReferences, accumulator, MemberRefNode.Kinds.Method, open, arguments);
    }

    static void ResolveNativeSpread(ReferenceMetadataIndex bindingReferences, BirSpreadConcat node)
    {
        if (node.CtorRef.IsPresent) return;
        var element = node.Elem.OrDefault() ?? throw new InvalidOperationException(
            "bir2cir: spreadConcat states no readable elem and so names no accumulator type");
        var arguments = new[] { element };
        var open = ResolveOwnerType(new TypeNode.Fqn(SpreadOwner, arguments), refs: bindingReferences)
            ?? throw new InvalidOperationException(
                $"bir2cir: spreadConcat accumulates into '{SpreadOwner}', which does not resolve to a .NET type");
        var constructors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(candidate => candidate.GetParameters().Length == 0).ToList();
        node.CtorRef = NativeReference(bindingReferences, ClrDeclarationMatching.TryPickUniqueCtor(bindingReferences, constructors, new List<TypeNode>(), arguments)
            ?? throw Missing(SpreadOwner, ".ctor"), MemberRefNode.Kinds.Ctor, open, arguments);
        var slot = new TypeNode.Tv("type", 0);
        node.AddRef = NativeSpreadMember(bindingReferences, open, arguments, "Add", new List<TypeNode> { slot });
        node.AddRangeRef = NativeSpreadMember(bindingReferences, open, arguments, "AddRange", new List<TypeNode> {
            new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { slot }),
        });
        node.ToArrayRef = NativeSpreadMember(bindingReferences, open, arguments, "ToArray", new List<TypeNode>());
    }

    static BirMemberReference NativeSpreadMember(ReferenceMetadataIndex bindingReferences, Type open, TypeNode[] arguments, string name, List<TypeNode> signature)
    {
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == signature.Count).ToList();
        return NativeReference(bindingReferences, ClrDeclarationMatching.TryPickUnique(bindingReferences, candidates, signature, arguments) ?? throw Missing(SpreadOwner, name),
            MemberRefNode.Kinds.Method, open, arguments);
    }

    static void ResolveNativeEnumeration(ReferenceMetadataIndex bindingReferences, BirForEach node)
    {
        if (node.MoveNextRef.IsPresent) return;
        var element = node.Elem.OrDefault() ?? throw new InvalidOperationException(
            "bir2cir: forEachInline states no readable elem and so names no enumerator protocol");
        // Both physical arms are required: the emitter chooses between exact
        // named operands, never between competing reflection declarations.
        node.EnumerableGetRef = NativeProtocolReference(bindingReferences, "System.Collections.Generic.IEnumerable", new[] { element }, "GetEnumerator");
        node.CurrentRef = NativeProtocolReference(bindingReferences, "System.Collections.Generic.IEnumerator", new[] { element }, "get_Current");
        node.EnumerableGetErasedRef = NativeProtocolReference(bindingReferences, "System.Collections.IEnumerable", Array.Empty<TypeNode>(), "GetEnumerator");
        node.CurrentErasedRef = NativeProtocolReference(bindingReferences, "System.Collections.IEnumerator", Array.Empty<TypeNode>(), "get_Current");
        node.MoveNextRef = NativeProtocolReference(bindingReferences, "System.Collections.IEnumerator", Array.Empty<TypeNode>(), "MoveNext");
    }

    static BirMemberReference NativeProtocolReference(ReferenceMetadataIndex bindingReferences, string name, TypeNode[] arguments, string member)
    {
        var owner = new TypeNode.Fqn(name, arguments.Length == 0 ? null : arguments);
        var open = ResolveOwnerType(owner, refs: bindingReferences) ?? throw new InvalidOperationException(
            $"bir2cir: forEachInline walks '{name}', which does not resolve to a .NET type");
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == member && method.GetParameters().Length == 0).ToList();
        var selected = ClrDeclarationMatching.TryPickUnique(bindingReferences, candidates, new List<TypeNode>(), arguments) ?? throw new InvalidOperationException(
            $"bir2cir: '{name}.{member}' does not resolve to one declaration for forEachInline");
        return NativeReference(bindingReferences, selected, MemberRefNode.Kinds.Method, open, arguments);
    }

    static void ResolveNativeEventBody(ReferenceMetadataIndex bindingReferences, BirEventAccessorBody node)
    {
        if (node.Kind.OrDefault() == "raise")
        {
            if (!node.InvokeRef.IsPresent) node.InvokeRef = NativeInvokeReference(bindingReferences, node.DelegateType.OrDefault(), "delegateType");
            return;
        }
        if (node.CombineRef.IsPresent || node.Kind.OrDefault() is not ("add" or "remove") || !node.DelegateType.HasValue) return;
        var delegateType = new TypeNode.Fqn("System.Delegate");
        var open = ResolveOwnerType(delegateType, refs: bindingReferences) ?? throw new InvalidOperationException(
            "bir2cir: 'System.Delegate' does not resolve to a .NET type");
        var signature = new List<TypeNode> { delegateType, delegateType };
        BirMemberReference Select(string name)
        {
            var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == name && method.GetParameters().Length == 2).ToList();
            var selected = ClrDeclarationMatching.TryPickUnique(bindingReferences, candidates, signature, Array.Empty<TypeNode>()) ?? throw new InvalidOperationException(
                $"bir2cir: 'System.Delegate.{name}(Delegate, Delegate)' does not resolve to one declaration");
            return NativeReference(bindingReferences, selected, MemberRefNode.Kinds.Method, open, Array.Empty<TypeNode>());
        }
        node.CombineRef = Select("Combine");
        node.RemoveRef = Select("Remove");
        var interlocked = ResolveOwnerType(new TypeNode.Fqn("System.Threading.Interlocked"), refs: bindingReferences)
            ?? throw new InvalidOperationException("bir2cir: 'System.Threading.Interlocked' does not resolve");
        var exchange = interlocked.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "CompareExchange" && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 3).ToList();
        if (exchange.Count != 1) throw new InvalidOperationException(
            $"bir2cir: 'Interlocked.CompareExchange<T>' resolves to {exchange.Count} declarations, not one");
        node.CompareExchangeRef = NativeReference(bindingReferences, exchange[0], MemberRefNode.Kinds.Method, interlocked, Array.Empty<TypeNode>());
    }

    static OptionalField<BirMemberReference> NativeNullableReference(ReferenceMetadataIndex bindingReferences, TypeNode element, string kind)
    {
        if (element == null) return default;
        var arguments = new[] { element };
        var open = ResolveOwnerType(new TypeNode.Fqn(NullableFqn, arguments), refs: bindingReferences) ?? throw new InvalidOperationException(
            $"bir2cir: {kind} wraps '{NullableFqn}', which does not resolve to a .NET type");
        if (kind is "nullableNull" or "nullableWrap")
        {
            var constructors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => candidate.GetParameters().Length == 1).ToList();
            var selected = ClrDeclarationMatching.TryPickUniqueCtor(bindingReferences, constructors, new List<TypeNode> { new TypeNode.Tv("type", 0) }, arguments)
                ?? throw new InvalidOperationException($"bir2cir: '{NullableFqn}' has no unique one-argument constructor for {kind}");
            return NativeReference(bindingReferences, selected, MemberRefNode.Kinds.Ctor, open, arguments);
        }
        var name = kind == "nullableHasValue" ? "get_HasValue" : "get_Value";
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == 0).ToList();
        var accessor = ClrDeclarationMatching.TryPickUnique(bindingReferences, candidates, new List<TypeNode>(), arguments) ?? throw new InvalidOperationException(
            $"bir2cir: '{NullableFqn}.{name}' does not resolve to one declaration for {kind}");
        return NativeReference(bindingReferences, accessor, MemberRefNode.Kinds.PropertyAccessor, open, arguments);
    }

    static BirMemberReference NativeInvokeReference(ReferenceMetadataIndex bindingReferences, TypeNode type, string key)
        => type == null ? throw new InvalidOperationException(
            $"bir2cir: a function-type call states no type under funcType/clrType/{key}")
            : BirMemberReference.FromPhysical(DelegateInvokeReference(bindingReferences, type));

    static BirMemberReference NativeDelegateConstructor(ReferenceMetadataIndex bindingReferences, TypeNode type, string key)
        => type == null ? throw new InvalidOperationException(
            $"bir2cir: a delegate construction states no function type under funcType/clrType/{key}")
            : BirMemberReference.FromPhysical(DelegateConstructorReference(bindingReferences, type));

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

    static BirMemberReference NativeReference(ReferenceMetadataIndex bindingReferences, MethodBase selected, string kind, Type owner, TypeNode[] arguments)
        => BirMemberReference.FromPhysical(ClrMemberReferences.MemberRefOf(bindingReferences, _localTypes, selected, kind, owner, arguments));
}
