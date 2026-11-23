using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    // This record carries a selected reflection declaration, not a second
    // lookup catalog. The shared scalar selectors remain the only matchers.
    sealed record NativeCallSelection(MethodInfo Declaration, BirMemberReference Reference,
        string Dispatch, TypeNode Return);

    static void ResolveNativeCallsSource(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, BirGraphValue node)
    {
        switch (node)
        {
            case BirNew construction:
                if (construction.Type.OrDefault() is TypeNode.Fqn referenced
                    && !bindingLocalTypes.Contains(referenced.Name) && ResolveOwnerType(referenced, refs: bindingReferences) != null)
                    ResolveNativeConstructorSource(bindingLocalTypes, bindingReferences, construction, referenced, construction.ArgTypes.OrDefault(),
                        construction.Args.OrDefault(), construction.MemberSignature.OrDefault(),
                        construction.ConstructorDeclarationId.OrDefault(), construction.ConstructorNativeKey.OrDefault());
                break;
            case BirNewClr construction:
                if (construction.Type.OrDefault() is TypeNode.Fqn owner)
                    ResolveNativeConstructorSource(bindingLocalTypes, bindingReferences, construction, owner, construction.ArgTypes.OrDefault(),
                        construction.Args.OrDefault(), construction.MemberSignature.OrDefault(), null, construction.ConstructorNativeKey.OrDefault());
                break;
            case BirNativeStatic call:
                ResolvePlainNativeCallSource(bindingLocalTypes, bindingReferences, call, call.Type.OrDefault(), call.Method.OrDefault(),
                    call.ArgTypes.OrDefault(), call.ResolvedMemberParams.OrDefault(), false, false,
                    call.TypeArgs.OrEmpty().Length != 0, call.Args.OrDefault());
                break;
            case BirNativeInstance call when call.MemberRef.HasValue && call.ResolvedMemberReturn.HasValue && call.Dispatch.HasValue:
                // An earlier owning binder has already authored this exact
                // declaration and dispatch, not an overload-search descriptor.
                call.ArgTypes = default; call.ResolvedMemberParams = default;
                break;
            case BirNativeInstance call:
                ResolvePlainNativeCallSource(bindingLocalTypes, bindingReferences, call, call.Type.OrDefault(), call.Method.OrDefault(),
                    call.ArgTypes.OrDefault(), call.ResolvedMemberParams.OrDefault(), true, call.Super.OrDefault(),
                    call.TypeArgs.OrEmpty().Length != 0, call.Args.OrDefault());
                break;
            case BirGenericStatic call:
                ResolveGenericNativeCallSource(bindingLocalTypes, bindingReferences, call, call.Type.OrDefault(), call.Method.OrDefault(),
                    call.ResolvedMemberParams.OrDefault(), call.Args.OrDefault(), false, false,
                    call.MemberRef.HasValue, call.ResolvedMemberReturn.IsPresent);
                break;
            case BirGenericInstance call:
                ResolveGenericNativeCallSource(bindingLocalTypes, bindingReferences, call, call.Type.OrDefault(), call.Method.OrDefault(),
                    call.ResolvedMemberParams.OrDefault(), call.Args.OrDefault(), true, call.Super.OrDefault(),
                    call.MemberRef.HasValue, call.ResolvedMemberReturn.IsPresent);
                break;
            case BirBoundClrDelegate construction:
                // This actual kind has no argTypes input. Do not substitute its
                // function/caller signature for the old reader's empty vector.
                ResolveNativeDelegateSource(bindingLocalTypes, bindingReferences, construction, construction.ClrType.OrDefault(),
                    construction.Method.OrDefault(), null, true);
                break;
            case BirClrStaticDelegate construction:
                ResolveNativeDelegateSource(bindingLocalTypes, bindingReferences, construction, construction.ClrType.OrDefault(),
                    construction.Method.OrDefault(), construction.ArgTypes.OrDefault(), false);
                break;
            case BirConstrainedCall call:
                ResolveConstrainedNativeCallSource(bindingLocalTypes, bindingReferences, call);
                break;
        }
    }

    static List<TypeNode> NativeArgumentTypes(TypeNode[] arguments)
        => arguments?.Where(type => type != null).ToList() ?? new List<TypeNode>();

    static void ResolveNativeConstructorSource(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, BirGraphValue node, TypeNode.Fqn owner,
        TypeNode[] argumentTypes, IBirExpression[] arguments, TypeNode[] selectedSignature, string identity, string nativeKey)
    {
        var open = ResolveOwnerType(owner, refs: bindingReferences)
            ?? throw new InvalidOperationException($"bir2cir: newClr owner '{owner.Name}' does not resolve to a .NET type");
        var signature = NativeArgumentTypes(argumentTypes).Select(BirTypeLowering.CanonicalPhysicalSlotType).ToList();
        var ownerArguments = owner.Args?.Select(BirTypeLowering.CanonicalPhysicalSlotType).ToArray();
        List<ConstructorInfo> Candidates() => open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(constructor => constructor.GetParameters().Length == signature.Count).ToList();
        ConstructorInfo selected;
        // Only the real BirNew producer carries the selected constructor ID.
        if (identity != null) selected = bindingReferences.SelectConstructorDeclaration(Candidates(), identity);
        else if (nativeKey != null) selected = bindingReferences.SelectedNativeConstructor(open, nativeKey);
        else if (selectedSignature != null)
        {
            var declaration = selectedSignature.Select(BirTypeLowering.CanonicalPhysicalSlotType).ToList();
            selected = ClrDeclarationMatching.PickDeclaredConstructor(bindingReferences, Candidates(), declaration,
                $"selected constructor declaration on '{owner.Name}' ({DescArgs(declaration)})");
        }
        else selected = ClrDeclarationMatching.PickUnique(bindingReferences, Candidates(), constructor => constructor.GetParameters(), signature, ownerArguments,
            $"newClr owner={TypeNode.ToJson(owner)} ({DescArgs(signature)})");

        CoerceNativeConstructorViews(bindingReferences, arguments, selected.GetParameters(), signature, ownerArguments);
        var reference = BirMemberReference.FromPhysical(ClrMemberReferences.MemberRefOf(bindingReferences, bindingLocalTypes, selected, MemberRefNode.Kinds.Ctor, open, ownerArguments));
        StampNativeDelegateArgumentTargets(bindingLocalTypes, bindingReferences, arguments, Array.Empty<TypeNode>(), selected,
            ownerArguments ?? Array.Empty<TypeNode>());
        switch (node)
        {
            case BirNew construction:
                construction.MemberRef = reference;
                construction.ResolvedMemberReturn = new TypeNode.Fqn("void");
                construction.ArgTypes = default; construction.MemberSignature = default;
                construction.ConstructorDeclarationId = default; construction.ConstructorNativeKey = default;
                break;
            case BirNewClr construction:
                construction.MemberRef = reference;
                construction.ResolvedMemberReturn = new TypeNode.Fqn("void");
                construction.ConstructorNativeKey = default;
                construction.ArgTypes = default; construction.MemberSignature = default;
                break;
        }
    }

    // A constructor head slot may require the already-sanctioned readonly→
    // mutable view. The cast owns the SAME operand, including nested anchors.
    static void CoerceNativeConstructorViews(ReferenceMetadataIndex bindingReferences, IBirExpression[] arguments, ParameterInfo[] parameters,
        IReadOnlyList<TypeNode> signature, TypeNode[] ownerArguments)
    {
        if (arguments == null || arguments.Length != parameters.Length) return;
        for (var index = 0; index < parameters.Length; index++)
        {
            var declared = ClrDeclarationMatching.MemberSigOf(bindingReferences, parameters[index].ParameterType);
            var closed = ownerArguments is { Length: > 0 } ? SupertypeGraph.SubstOwnerTvs(declared, ownerArguments) : declared;
            if (CollectionViewFaces.IsViewSeam(signature[index], closed) && arguments[index] is { } actual)
                arguments[index] = new BirCast { Type = closed, E = OptionalField<IBirExpression>.Of(actual) };
        }
    }

    static NativeCallSelection NativeMethodSelection(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, MethodInfo declaration, Type open,
        TypeNode[] ownerArguments, string dispatch)
    {
        // The selected override owns crossing/generic facts. A constrained
        // instruction instead names its virtual root slot, never the body.
        var target = dispatch == "constrained" ? ConstrainedSlot(declaration, open) : declaration;
        return new(declaration, BirMemberReference.FromPhysical(
            ClrMemberReferences.MemberRefOf(bindingReferences, bindingLocalTypes, target, MemberRefNode.Kinds.Method, open, ownerArguments)), dispatch,
            NativeDeclaredReturn(bindingReferences, declaration.ReturnType));
    }

    static void ResolvePlainNativeCallSource(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, BirGraphValue node, TypeNode ownerType, string name,
        TypeNode[] argumentTypes, TypeNode[] selectedSignature, bool instance, bool superCall,
        bool hasTypeArguments, IBirExpression[] arguments)
    {
        TypeNode.Fqn owner; Type open;
        if (instance && ownerType is TypeNode.Array)
        {
            open = ClrDeclarationMatching.SystemArrayMlc(bindingReferences) ?? throw new InvalidOperationException(
                "bir2cir: clrInstance array-owner method could not resolve System.Array");
            owner = new TypeNode.Fqn("System.Array");
            ((BirNativeInstance)node).Type = owner;
        }
        else if (ownerType is TypeNode.Fqn named)
        {
            owner = named;
            open = ResolveOwnerType(owner, refs: bindingReferences) ?? throw new InvalidOperationException(
                $"bir2cir: {(instance ? "clrInstance" : "clrStatic")} owner '{owner.Name}' does not resolve to a .NET type");
        }
        else return;
        var signature = NativeArgumentTypes(argumentTypes);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (instance ? BindingFlags.Instance : BindingFlags.Static);
        // Source access has already been checked; protected reference slots
        // are legal, private/assembly-only candidates are not.
        var candidates = Candidates(bindingReferences, open, name, signature, owner.Args, flags).Where(IsPublicOrProtected).ToList();
        // Explicit type arguments own generic selection; an absent vector must
        // not let a same-shaped generic definition enter nongeneric matching.
        if (!hasTypeArguments) candidates = candidates.Where(method => !method.IsGenericMethodDefinition).ToList();
        var context = $"{(instance ? "clrInstance" : "clrStatic")} owner={TypeNode.ToJson(owner)} .{name}({DescArgs(signature)})";
        var selected = selectedSignature != null
            ? ClrDeclarationMatching.TryPickDeclared(bindingReferences, bindingLocalTypes, candidates, selectedSignature.ToList(), open)
                ?? throw new InvalidOperationException($"bir2cir: selected declaration {context} has no unique open signature")
            : ClrDeclarationMatching.PickUnique(bindingReferences, candidates, method => method.GetParameters(), signature, owner.Args, context);
        var plan = NativeMethodSelection(bindingLocalTypes, bindingReferences, selected, open, owner.Args, instance ? Dispatch(selected, open, superCall) : null);
        WriteNativeMethodSelection(node, plan);
        StampNativeDelegateArgumentTargets(bindingLocalTypes, bindingReferences, arguments, node.ApplicationTypeArguments.OrEmpty(), selected, owner.Args ?? Array.Empty<TypeNode>());
        switch (node)
        {
            case BirNativeStatic call: call.ArgTypes = default; call.ResolvedMemberParams = default; break;
            case BirNativeInstance call:
                call.ArgTypes = default; call.ResolvedMemberParams = default; call.Dispatch = plan.Dispatch; break;
        }
    }

    static void ResolveGenericNativeCallSource(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, BirGraphValue node, TypeNode ownerType, string name,
        TypeNode[] signature, IBirExpression[] arguments, bool instance, bool superCall,
        bool hasReference, bool returnPresent)
    {
        if (hasReference && returnPresent)
        {
            ClearNativeGenericDescriptor(node);
            return;
        }
        if (returnPresent) throw new InvalidOperationException(
            "bir2cir: generic external call carries a declared-return stamp without its resolved memberRef");
        var owner = ownerType as TypeNode.Fqn;
        var open = owner != null ? ResolveOwnerType(owner, refs: bindingReferences) : null;
        if (open == null || name == null) throw new InvalidOperationException(
            $"bir2cir: generic external call '{owner?.Name ?? "<unknown>"}.{name ?? "<unknown>"}' does not resolve to a declaration in the reference set");
        var arity = node.ApplicationTypeArguments.OrEmpty().Length;
        if (signature == null || signature.Length != (arguments?.Length ?? 0) || signature.Any(type => type == null))
            throw new InvalidOperationException($"bir2cir: generic external call '{owner.Name}.{name}<{arity}>' carries no usable frontend-resolved parameter vector");
        // Match the selected OPEN declaration, never close its type TVs before
        // choosing between overloads. A unique name/arity alone is not evidence.
        var selected = ClrDeclarationMatching.SelectedGenericDeclaration(bindingReferences, bindingLocalTypes, open, name, arity, instance, signature.ToList())
            ?? throw new InvalidOperationException($"bir2cir: generic external call '{owner.Name}.{name}<{arity}>' has no unique declaration matching its frontend-resolved parameter vector in the reference set");
        var plan = NativeMethodSelection(bindingLocalTypes, bindingReferences, selected, open, owner.Args, instance ? Dispatch(selected, open, superCall) : null);
        WriteNativeMethodSelection(node, plan);
        if (node is BirGenericInstance call)
        {
            call.Dispatch = plan.Dispatch; call.ReceiverMode = open.IsValueType ? "address" : "value";
        }
        StampNativeDelegateArgumentTargets(bindingLocalTypes, bindingReferences, arguments, node.ApplicationTypeArguments.OrEmpty(), selected, owner.Args ?? Array.Empty<TypeNode>());
        ClearNativeGenericDescriptor(node);
    }

    static void ClearNativeGenericDescriptor(BirGraphValue node)
    {
        if (node is BirGenericStatic stat) stat.ResolvedMemberParams = default;
        else if (node is BirGenericInstance instance) instance.ResolvedMemberParams = default;
    }

    static void ResolveNativeDelegateSource(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, BirGraphValue node, TypeNode ownerType, string name,
        TypeNode[] argumentTypes, bool instance)
    {
        if (ownerType is not TypeNode.Fqn owner) return;
        var kind = instance ? "newBoundClrDelegate" : "newClrStaticDelegate";
        var open = ResolveOwnerType(owner, refs: bindingReferences)
            ?? throw new InvalidOperationException($"bir2cir: {kind} owner '{owner.Name}' does not resolve to a .NET type");
        var signature = NativeArgumentTypes(argumentTypes);
        var arity = node.ApplicationTypeArguments.OrEmpty().Length;
        var candidates = Candidates(bindingReferences, open, name, signature, owner.Args,
                BindingFlags.Public | BindingFlags.NonPublic | (instance ? BindingFlags.Instance : BindingFlags.Static))
            .Where(IsPublicOrProtected)
            .Where(method => method.IsGenericMethodDefinition ? method.GetGenericArguments().Length == arity : arity == 0).ToList();
        var selected = ClrDeclarationMatching.PickUnique(bindingReferences, candidates, method => method.GetParameters(), signature, owner.Args,
            $"{kind} owner={TypeNode.ToJson(owner)} .{name}({DescArgs(signature)})");
        // ldftn names the selected declaration, not a constrained call target.
        WriteNativeMethodSelection(node, NativeMethodSelection(bindingLocalTypes, bindingReferences, selected, open, owner.Args, null));
        if (node is BirClrStaticDelegate stat) stat.ArgTypes = default;
    }

    static void ResolveConstrainedNativeCallSource(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, BirConstrainedCall call)
    {
        call.Super = default;
        // ContainsKey in the original input includes explicit null.
        if (call.MemberRef.IsPresent) return;
        if (call.Iface.OrDefault() is not TypeNode.Fqn owner || !call.Method.HasValue) return;
        var name = call.Method.Value;
        if (!call.Args.HasValue) throw new InvalidOperationException(
            $"bir2cir: constrained call '{owner.Name}.{name}' requires an 'args' array");
        var open = ResolveOwnerType(owner, refs: bindingReferences);
        if (open == null) return;
        var arity = call.TypeArgs.OrEmpty().Length;
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == call.Args.Value.Length
                && (method.IsGenericMethodDefinition ? method.GetGenericArguments().Length == arity : arity == 0)).ToList();
        var selected = call.ResolvedMemberParams.HasValue
            ? ClrDeclarationMatching.TryPickDeclared(bindingReferences, bindingLocalTypes, candidates, call.ResolvedMemberParams.Value.ToList(), open)
            : !call.Sig.HasValue ? (candidates.Count == 1 ? candidates[0] : null)
                : ClrDeclarationMatching.TryPickUnique(bindingReferences, candidates, call.Sig.Value.ToList(), owner.Args ?? Array.Empty<TypeNode>());
        if (selected == null) throw new InvalidOperationException(
            $"bir2cir: constrained call '{owner.Name}.{name}' does not resolve to one declared signature");
        WriteNativeMethodSelection(call, NativeMethodSelection(bindingLocalTypes, bindingReferences, selected, open, owner.Args ?? Array.Empty<TypeNode>(), null));
        StampNativeDelegateArgumentTargets(bindingLocalTypes, bindingReferences, call.Args.Value, call.TypeArgs.OrEmpty(), selected, owner.Args ?? Array.Empty<TypeNode>());
        call.ResolvedMemberParams = default;
    }

    static void WriteNativeMethodSelection(BirGraphValue node, NativeCallSelection selected)
    {
        var parameters = NativeResolvedMethodTypeParameters(selected.Declaration, node.ApplicationTypeArguments.OrEmpty().Length);
        switch (node)
        {
            case BirNativeStatic call:
                call.MemberRef = selected.Reference; call._resolvedMethodTypeParams = parameters; call.ResolvedMemberReturn = selected.Return; break;
            case BirNativeInstance call:
                call.MemberRef = selected.Reference; call._resolvedMethodTypeParams = parameters; call.ResolvedMemberReturn = selected.Return; break;
            case BirGenericStatic call:
                call.MemberRef = selected.Reference; call._resolvedMethodTypeParams = parameters; call.ResolvedMemberReturn = selected.Return; break;
            case BirGenericInstance call:
                call.MemberRef = selected.Reference; call._resolvedMethodTypeParams = parameters; call.ResolvedMemberReturn = selected.Return; break;
            case BirBoundClrDelegate call:
                call.MemberRef = selected.Reference; call._resolvedMethodTypeParams = parameters; call.ResolvedMemberReturn = selected.Return; break;
            case BirClrStaticDelegate call:
                call.MemberRef = selected.Reference; call._resolvedMethodTypeParams = parameters; call.ResolvedMemberReturn = selected.Return; break;
            case BirConstrainedCall call:
                call.MemberRef = selected.Reference; call._resolvedMethodTypeParams = parameters; call.ResolvedMemberReturn = selected.Return; break;
        }
    }
}
