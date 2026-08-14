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

    // --- Unit-return delegate adapters (task #75 S4a) ---
    // A lifted lambda for a `() -> Unit` / `(T) -> Unit` body is emitted returning `void` (a Unit result maps to
    // void), yet a scope-function's target delegate is `Func<...,Unit>` whose `Invoke` returns the REAL `kotlin.Unit`
    // type. Binding the void method-pointer into that ctor is not delegate-compatible (ilverify [DelegateCtor]:
    // "Unrecognized arguments"). Reconcile by building the lambda's NATURAL void delegate (`Action<...>`, via the
    // ordinary newDelegate/newClosure self-build) and wrapping it in a synthetic static adapter that invokes it and
    // returns `Unit.INSTANCE`. Wrapping the void DELEGATE (not the raw target) is what keeps the adapter well-scoped:
    // a capturing lambda in a generic function is a GENERIC closure `Closure<T>` whose `invoke` a direct adapter could
    // only name by dragging the enclosing method's `T` into this non-generic holder — impossible; the void delegate
    // absorbs that genericity, so the adapter names only the (concrete/BCL) delegate type. Fires ONLY on the
    // void-lifted-vs-non-void-delegate mismatch; a genuine `Action` (void Invoke) / matching-return `Func` binds directly.
    TypeBuilder _unitAdapterTB;
    TypeBuilder _delegateInvokeAdapterTB;

    int _unitAdapterCounter;
    readonly Dictionary<string, MethodBuilder> _delegateInvokeAdapters = new();
    readonly Dictionary<string, MethodBuilder> _delegateCtorAdapters = new();

    TypeBuilder UnitAdapterHolder()
    {
        if (_unitAdapterTB != null) return _unitAdapterTB;
        _unitAdapterTB = _mod.DefineType(CompilerServicesNs + "UnitDelegateAdapters",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.Class,
            Bcl("System.Object"));
        SetAttribute(_unitAdapterTB.SetCustomAttribute,
            // #370-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the assembly, not a call any program makes
            Bcl("System.Runtime.CompilerServices.CompilerGeneratedAttribute").GetConstructor(Type.EmptyTypes), Array.Empty<Type>());
        return _unitAdapterTB;
    }

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

    MethodInfo DelegateInvokeAdapter(Type ft)
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
            var gps = mb.DefineGenericParameters(Enumerable.Range(1, actual.Length)
                .Select(i => returnsValue && i == actual.Length ? "TResult" : "T" + i).ToArray());
            var delegateType = ConstructedType(def, gps);
            var invokeParams = returnsValue ? gps.Take(gps.Length - 1).Cast<Type>().ToArray() : gps.Cast<Type>().ToArray();
            mb.SetReturnType(returnsValue ? gps[^1] : Bcl("System.Void"));
            mb.SetParameters(new[] { delegateType }.Concat(invokeParams).ToArray());
            var il = mb.GetILGenerator();
            for (int i = 0; i <= invokeParams.Length; i++) il.Emit(OpCodes.Ldarg, i);
            // #370-residual: a delegate has exactly one Invoke (ECMA-335 II.14.6) — no candidate set to choose from
            EmitMethod(il, OpCodes.Callvirt, AnchorMethod(delegateType, def.GetMethod("Invoke")));
            il.Emit(OpCodes.Ret);
            _delegateInvokeAdapters[key] = mb;
        }
        return mb.MakeGenericMethod(actual);
    }

    void EmitDelegateInvoke(ILGenerator il, Type ft)
    {
        if (NeedsDelegateInvokeAdapter(ft)) EmitMethod(il, OpCodes.Call, DelegateInvokeAdapter(ft));
        else EmitMethod(il, OpCodes.Callvirt, InvokeOf(ft));
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
    MethodInfo DelegateCtorAdapter(Type ft)
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
            var gps = mb.DefineGenericParameters(Enumerable.Range(1, actual.Length)
                .Select(i => returnsValue && i == actual.Length ? "TResult" : "T" + i).ToArray());
            var delegateType = ConstructedType(def, gps);
            mb.SetReturnType(delegateType);
            mb.SetParameters(Bcl("System.Object"), Bcl("System.IntPtr"));
            var il = mb.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            EmitConstructor(il, OpCodes.Newobj, AnchorConstructor(delegateType,
                // #370-residual: a delegate has exactly one .ctor(object, native int) (ECMA-335 II.14.6) — no candidate set to choose from
                def.GetConstructor(new[] { Bcl("System.Object"), Bcl("System.IntPtr") })));
            il.Emit(OpCodes.Ret);
            _delegateCtorAdapters[key] = mb;
        }
        return mb.MakeGenericMethod(actual);
    }

    void EmitDelegateCtor(ILGenerator il, Type ft)
    {
        if (NeedsDelegateInvokeAdapter(ft)) EmitMethod(il, OpCodes.Call, DelegateCtorAdapter(ft));
        else EmitConstructor(il, OpCodes.Newobj, DelegateCtor(ft));
    }

    // `static Unit A(<voidDelegate> d, <params>) { d.Invoke(<params>); return Unit.INSTANCE; }` — the void delegate `ft`
    // is bound as the delegate ctor's target (a closed static delegate), the remaining signature matching the Unit
    // delegate's Invoke. `invokeRet` is that Invoke return type (must carry a static `INSTANCE` singleton — `kotlin.Unit`);
    // `paramTypes` are the delegate's parameter types (identical for the void and the Unit delegate — only the return
    // differs), forwarded straight through.
    MethodInfo UnitWrapAdapter(Type ft, Type invokeRet, Type[] paramTypes, FieldInfo singleton)
    {
        // The singleton the conversion's node names. It does not depend on the adapter's arity — it is one static
        // field of one type — so it rides the node rather than being fetched by string off the return type here.
        var instF = singleton
            ?? throw new InvalidOperationException(
                $"ilemit: reconciling a void lambda into a delegate returning {invokeRet} needs the "
                + "`unitInstanceRef` its node carries. Every external member arrives named (#370)");
        var pTypes = new[] { ft }.Concat(paramTypes).ToArray();
        var mb = UnitAdapterHolder().DefineMethod("Unit$" + (_unitAdapterCounter++),
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            invokeRet, pTypes);
        var il = mb.GetILGenerator();
        for (int i = 0; i < pTypes.Length; i++) il.Emit(OpCodes.Ldarg, i);
        EmitDelegateInvoke(il, ft);
        EmitField(il, OpCodes.Ldsfld, instF);
        il.Emit(OpCodes.Ret);
        return mb;
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

    // The delegate's Invoke RETURN type, resolved WITHOUT a by-name member lookup on the instantiation — which throws
    // NotSupportedException on a `TypeBuilderInstantiation` (`Func<Res,int>`, Res a user TypeBuilder) and returns the
    // UNSUBSTITUTED `TResult` off a `MethodOnTypeBuilderInstantiation` (both documented Reflection.Emit unreliabilities).
    // Read the definition's Invoke return, then positionally substitute an open return type-param with the closed arg.
    // Both callers (the delegate-arg rewrap — guard-guaranteed closed — and the concrete-runtime event delegates) pass a
    // closed `ft`, so the substituted result is a concrete type; a def whose Invoke returns a type constructed over a
    // param would come back open, but that only feeds the Unit-adapter trigger, where it is rightly not `kotlin.Unit`.
    Type InvokeRetOf(Type ft)
    {
        // #370-residual: a delegate has exactly one Invoke (ECMA-335 II.14.6); this reads its RETURN TYPE, emitting no member token
        if (!IsGenericInst(ft)) return ft.GetMethod("Invoke")?.ReturnType ?? Bcl("System.Void");
        var def = ft.GetGenericTypeDefinition();
        var r = def is TypeBuilder dtb && _declaredDelegateInvokes.TryGetValue(dtb, out var declared)
            ? declared.ReturnType
            // #370-residual: a delegate has exactly one Invoke (ECMA-335 II.14.6) — no candidate set to choose from
            : def.GetMethod("Invoke").ReturnType;
        return r.IsGenericParameter && r.GenericParameterPosition < ft.GetGenericArguments().Length
            ? ft.GetGenericArguments()[r.GenericParameterPosition] : r;
    }

    // The delegate's RETURN / PARAMETER .NET types from a structured `funcType` (a Fn node) — carried by the BIR, so we
    // never reflect the Invoke of a TypeBuilder-baked delegate (unreliable on an un-baked generic instantiation). The
    // funcType slot is ALWAYS a structured Fn node (#37/#49; the legacy `func:` string form is retired — #48).
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
