// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// The canonical wide delegate family + Kotlin function-type (FunctionN/Action) resolution.
sealed partial class Emitter
{
    // The physical namespace of CIR-selected canonical delegate references.
    const string CompilerServicesNs = "DotKt.Runtime.CompilerServices.";

    TypeBuilder _delegateInvokeAdapterTB;

    readonly Dictionary<string, MethodBuilder> _delegateInvokeAdapters = new();
    readonly Dictionary<string, MethodBuilder> _delegateCtorAdapters = new();

    TypeBuilder DelegateInvokeAdapterHolder()
    {
        if (_delegateInvokeAdapterTB != null) return _delegateInvokeAdapterTB;
        _delegateInvokeAdapterTB = _mod.DefineType(CompilerServicesNs + "DelegateInvokeAdapters",
            TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.Class,
            Bcl("System.Object"));
        SetAttribute(_delegateInvokeAdapterTB.SetCustomAttribute,
            // #370-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the assembly, not a call any program makes
            Bcl("System.Runtime.CompilerServices.CompilerGeneratedAttribute").GetConstructor(Type.EmptyTypes), Array.Empty<Type>());
        return _delegateInvokeAdapterTB;
    }

    // PersistedAssemblyBuilder cannot encode a MemberRef for a BCL delegate instantiated with a COMPOSITE open
    // type (`Func<E[]>`, `Func<List<E>,R>`): TypeBuilder.GetMethod fails to map Invoke onto that TypeSpec. Keep the
    // public ABI as System.Func/Action and move the composite type into a MethodSpec instead:
    //
    //   static TResult InvokeFunc<T1,...,TResult>(Func<T1,...,TResult> d, T1 p1, ...) => d(p1, ...);
    //
    // The helper body names only direct generic parameters, which Reflection.Emit encodes reliably; the call site
    // instantiates those parameters with E[]/List<E>/etc. This is an emit mechanism, never a delegate-family choice.
    bool NeedsDelegateInvokeAdapter(Type ft)
    {
        if (!IsGenericInst(ft) || ft.GetGenericTypeDefinition() is TypeBuilder) return false;
        var n = ft.GetGenericTypeDefinition().FullName;
        if (n == null || !(n.StartsWith("System.Func`", StringComparison.Ordinal)
                           || n.StartsWith("System.Action`", StringComparison.Ordinal))) return false;
        return ft.GetGenericArguments().Any(a => !a.IsGenericParameter && ContainsTypeBuilder(a));
    }

    MethodInfo DelegateInvokeAdapter(Type ft, MethodInfo openInvoke)
    {
        var def = ft.GetGenericTypeDefinition();
        var actual = ft.GetGenericArguments();
        bool returnsValue = def.FullName.StartsWith("System.Func`", StringComparison.Ordinal);
        string key = (returnsValue ? "F" : "A") + actual.Length;
        if (!_delegateInvokeAdapters.TryGetValue(key, out var mb))
        {
            mb = DelegateInvokeAdapterHolder().DefineMethod(
                (returnsValue ? "InvokeFunc" : "InvokeAction") + actual.Length,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            // #400-residual: the ENCODING workaround's own frame — a MethodSpec that moves the composite type off the
            // TypeSpec PersistedAssemblyBuilder cannot encode. It stands for no CIR declaration and decides nothing.
            var gps = mb.DefineGenericParameters(Enumerable.Range(1, actual.Length)
                .Select(i => returnsValue && i == actual.Length ? "TResult" : "T" + i).ToArray());
            var delegateType = ConstructedType(def, gps);
            var invokeParams = returnsValue ? gps.Take(gps.Length - 1).Cast<Type>().ToArray() : gps.Cast<Type>().ToArray();
            mb.SetReturnType(returnsValue ? gps[^1] : Bcl("System.Void"));
            mb.SetParameters(new[] { delegateType }.Concat(invokeParams).ToArray());
            var il = mb.GetILGenerator();
            for (int i = 0; i <= invokeParams.Length; i++) il.Emit(OpCodes.Ldarg, i);
            // #370-residual: a delegate has exactly one Invoke (ECMA-335 II.14.6) — no candidate set to choose from
            EmitMethod(il, OpCodes.Callvirt, AnchorOn(delegateType, openInvoke));
            il.Emit(OpCodes.Ret);
            _delegateInvokeAdapters[key] = mb;
        }
        return mb.MakeGenericMethod(actual);
    }

    // `openInvoke` is the DECLARATION the node named; anchoring it onto the call site's constructed delegate is
    // mechanical, and the adapter arm needs the same declaration for the body it synthesizes.
    void EmitDelegateInvoke(ILGenerator il, Type ft, MethodInfo openInvoke)
    {
        if (NeedsDelegateInvokeAdapter(ft)) EmitMethod(il, OpCodes.Call, DelegateInvokeAdapter(ft, openInvoke));
        else EmitMethod(il, OpCodes.Callvirt, AnchorOn(ft, openInvoke));
    }

    // The same PersistedAssemblyBuilder limitation applies to the delegate `.ctor`: it cannot map
    // `Func<E[]>::.ctor` when E is an enclosing TypeBuilder parameter. As with Invoke, keep the CIR-selected
    // System.Func/Action identity and move the composite instantiation into a MethodSpec:
    //
    //   static Func<TResult> NewFunc<TResult>(object target, IntPtr method) =>
    //       new Func<TResult>(target, method);
    //
    // The helper definition mentions only its own direct generic parameters, which Reflection.Emit can encode;
    // the call site supplies E[]/List<E>/etc. This is strictly an encoding adapter, not delegate selection.
    // The adapter is shared per arity, and so is the OPEN constructor it needs: `Func`N..ctor(object, native int)`
    // does not vary with the instantiation. The call site has the node, so the reference reaches here rather than
    // being fetched by signature inside a body that belongs to no node.
    MethodInfo DelegateCtorAdapter(Type ft, ConstructorInfo openCtor)
    {
        var def = ft.GetGenericTypeDefinition();
        var actual = ft.GetGenericArguments();
        bool returnsValue = def.FullName.StartsWith("System.Func`", StringComparison.Ordinal);
        string key = (returnsValue ? "F" : "A") + actual.Length;
        if (!_delegateCtorAdapters.TryGetValue(key, out var mb))
        {
            mb = DelegateInvokeAdapterHolder().DefineMethod(
                (returnsValue ? "NewFunc" : "NewAction") + actual.Length,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            // #400-residual: the ENCODING workaround's own frame — the constructor twin of the Invoke helper above,
            // for the same PersistedAssemblyBuilder limitation. It stands for no CIR declaration and decides nothing.
            var gps = mb.DefineGenericParameters(Enumerable.Range(1, actual.Length)
                .Select(i => returnsValue && i == actual.Length ? "TResult" : "T" + i).ToArray());
            var delegateType = ConstructedType(def, gps);
            mb.SetReturnType(delegateType);
            mb.SetParameters(Bcl("System.Object"), Bcl("System.IntPtr"));
            var il = mb.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            // The declaration came from the node's resolved delegateCtorRef. Re-anchoring that declaration onto
            // the helper's generic delegate view is mechanical; looking the constructor up again would discard
            // the very identity the carrier supplied.
            EmitConstructor(il, OpCodes.Newobj, Sanction(AnchorOn(delegateType, openCtor)));
            il.Emit(OpCodes.Ret);
            _delegateCtorAdapters[key] = mb;
        }
        return mb.MakeGenericMethod(actual);
    }

    // The delegate a construction builds is the one its node names: bir2cir decided which delegate the construction
    // physically is (its natural one, or the slot's — including authoring a void-to-value adapter when no method
    // pointer could be compatible at all), and stated it as `funcType` + `delegateCtorRef`. Nothing is chosen here.
    void EmitDelegateCtor(ILGenerator il, Type ft, JsonElement node)
    {
        var ctor = RequiredRef<ConstructorInfo>(node, "delegateCtorRef", "a delegate construction");
        if (NeedsDelegateInvokeAdapter(ft)) EmitMethod(il, OpCodes.Call, DelegateCtorAdapter(ft, ctor));
        else EmitConstructor(il, OpCodes.Newobj, ctor);
    }

    // Runtime members of delegate TypeBuilders declared by CIR. Reflection.Emit cannot reflect members from an
    // unbaked generic TypeBuilder instantiation, so construction/invocation anchors through these declared handles.
    readonly Dictionary<TypeBuilder, ConstructorBuilder> _declaredDelegateCtors = new();
    readonly Dictionary<TypeBuilder, MethodBuilder> _declaredDelegateInvokes = new();

    ConstructorInfo DelegateCtor(Type ft)
    {
        var sig = new[] { Bcl("System.Object"), Bcl("System.IntPtr") };
        // #150: key the self-defined-delegate ctor fast-path on IsGenericInst (GetGenericArguments-based), SYMMETRIC with
        // InvokeOf/ContainsTypeBuilder. `IsGenericType` is UNRELIABLE for a TypeBuilderInstantiation across Reflection.Emit
        // versions (see IsGenericInst) — was `ft.IsGenericType`, which only fires because this SDK happens to report True;
        // a version reporting False would skip this branch and hit `ft.GetConstructor(sig)` on the un-baked builder
        // instantiation -> NotSupportedException. GetGenericArguments().Length is always populated, so this is robust.
        if (IsGenericInst(ft) && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _declaredDelegateCtors.TryGetValue(dtb, out var dctor))
            return AnchorConstructor(ft, dctor);
        return (IsGenericInst(ft) && ContainsTypeBuilder(ft))
            // #370-residual: a delegate has exactly one .ctor(object, native int) (ECMA-335 II.14.6) — no candidate set to choose from
            ? AnchorConstructor(ft, ft.GetGenericTypeDefinition().GetConstructor(sig))
            // #370-residual: a delegate has exactly one .ctor(object, native int) (ECMA-335 II.14.6) — no candidate set to choose from
            : ft.GetConstructor(sig);
    }

    // A generic INSTANTIATION test that does NOT depend on `IsGenericType`, whose value for a TypeBuilderInstantiation is
    // version-dependent and unreliable (SDK 10.0.101 empirically reports IsGenericType=TRUE for a TypeBuilder-defined
    // generic delegate instantiated over concrete args; older Reflection.Emit reported FALSE — the measurement was
    // taken on a wide `KFunc`) — the generic-arg list, by contrast, is ALWAYS populated. All
    // TypeBuilder-instantiation branching here keys on this, never on IsGenericType.
    static bool IsGenericInst(Type t) => !t.IsGenericParameter && t.GetGenericArguments().Length > 0;

    // The delegate's `Invoke` method, bridged via TypeBuilder.GetMethod for a TypeBuilder-involving instantiation.
    MethodInfo InvokeOf(Type ft)
    {
        if (IsGenericInst(ft) && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _declaredDelegateInvokes.TryGetValue(dtb, out var invoke))
            return AnchorMethod(ft, invoke);
        if (IsGenericInst(ft) && ContainsTypeBuilder(ft))
            // #370-residual: a delegate has exactly one Invoke (ECMA-335 II.14.6) — no candidate set to choose from
            return AnchorMethod(ft, ft.GetGenericTypeDefinition().GetMethod("Invoke"));
        // #370-residual: a delegate has exactly one Invoke (ECMA-335 II.14.6) — no candidate set to choose from
        return ft.GetMethod("Invoke");
    }

    // The delegate's RETURN / PARAMETER .NET types from a structured `funcType` (a Fn node) — carried by the BIR, so we
    // never reflect the Invoke of a TypeBuilder-baked delegate (unreliable on an un-baked generic instantiation). The
    // funcType is always a structured Fn node (#37/#49).
    Type FuncRetType(JsonElement e) =>
        DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fn fn
            ? MapType(fn.Ret) : throw new NotSupportedException("funcType is not a structured fn node");

    List<Type> FuncArgTypes(JsonElement e) =>
        DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fn fn
            ? fn.DelegateParams.Select(MapType).ToList()
            : throw new NotSupportedException("funcType is not a structured fn node");

    // Resolve the exact physical family selected in CIR. In a stdlib self-build the matching kind:"delegate"
    // declaration is in `_types`; every app resolves the same metadata identity from its stdlib reference.
    Type CanonicalDelegateDef(string baseName, int genericArity)
    {
        var name = CompilerServicesNs + baseName + "`" + genericArity;
        return _types.TryGetValue(name, out var own) ? own.AsType : ResolveType(name);
    }

    Type CanonicalFuncType(Type[] args, Type ret)
    {
        var all = args.Append(ret).ToArray();
        return ConstructedType(CanonicalDelegateDef("KFunc", all.Length), all);
    }

    Type CanonicalActionType(Type[] args) =>
        ConstructedType(CanonicalDelegateDef("KAction", args.Length), args);

    void DefineDelegateMembers(TypeInfo ti)
    {
        var invokeParams = ti.Def.GetProperty("params").EnumerateArray()
            .Select(p => MapType(p.GetProperty("type"))).ToArray();
        var invokeRet = MapType(ti.Def.GetProperty("ret"));
        var ctor = ti.TB.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
            CallingConventions.Standard,
            new[] { Bcl("System.Object"), Bcl("System.IntPtr") });
        ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        var invoke = ti.TB.DefineMethod(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            invokeRet,
            invokeParams);
        invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        _declaredDelegateCtors[ti.TB] = ctor;
        _declaredDelegateInvokes[ti.TB] = invoke;
    }

}
