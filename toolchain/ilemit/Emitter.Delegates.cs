// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Synthetic delegate types + Kotlin function-type (FunctionN/Action) resolution.
sealed partial class Emitter
{
    // The embedded round-trip attribute namespace (#71 S2: the attribute CLASSES are now ordinary CIR type decls
    // emitted by bir2cir; this const only names the synthetic-delegate metadata namespace below).
    const string CompilerServicesNs = "DotKt.Runtime.CompilerServices.";

    // ilemit AUTHORS its own synthetic high-arity delegate types; mark each [KotlinFunction(0)] (a plain function
    // type — no infix/operator/suspend) so dll2klib restores it as a Kotlin function type. This is ilemit stamping
    // its OWN emitted member (analogous to StampCompilerGenerated), NOT round-trip generation over user code: the
    // attribute CLASS is the ordinary CIR-defined `KotlinFunctionAttribute` in `_types` (bir2cir emits it, #71 S2),
    // whose (int) ctor is resolved generically. Absent (a --no-stdlib or runtime build that emits no attr class) -> skip.
    void StampKotlinFunctionZero(TypeBuilder tb)
    {
        if (!_types.TryGetValue(CompilerServicesNs + "KotlinFunctionAttribute", out var ti)) return;
        EnsureCtorsDefined(ti);
        if (ti.Ctors.Count == 0) return;
        tb.SetCustomAttribute(new CustomAttributeBuilder(ti.Ctors[0], new object[] { 0 }));
    }

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
            typeof(object));
        _unitAdapterTB.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
        return _unitAdapterTB;
    }

    TypeBuilder DelegateInvokeAdapterHolder()
    {
        if (_delegateInvokeAdapterTB != null) return _delegateInvokeAdapterTB;
        _delegateInvokeAdapterTB = _mod.DefineType(CompilerServicesNs + "DelegateInvokeAdapters",
            TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.Class,
            typeof(object));
        _delegateInvokeAdapterTB.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
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
            var delegateType = def.MakeGenericType(gps);
            var invokeParams = returnsValue ? gps.Take(gps.Length - 1).Cast<Type>().ToArray() : gps.Cast<Type>().ToArray();
            mb.SetReturnType(returnsValue ? gps[^1] : typeof(void));
            mb.SetParameters(new[] { delegateType }.Concat(invokeParams).ToArray());
            var il = mb.GetILGenerator();
            for (int i = 0; i <= invokeParams.Length; i++) il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Callvirt, TypeBuilder.GetMethod(delegateType, def.GetMethod("Invoke")));
            il.Emit(OpCodes.Ret);
            _delegateInvokeAdapters[key] = mb;
        }
        return mb.MakeGenericMethod(actual);
    }

    void EmitDelegateInvoke(ILGenerator il, Type ft)
    {
        if (NeedsDelegateInvokeAdapter(ft)) il.Emit(OpCodes.Call, DelegateInvokeAdapter(ft));
        else il.Emit(OpCodes.Callvirt, InvokeOf(ft));
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
            var delegateType = def.MakeGenericType(gps);
            mb.SetReturnType(delegateType);
            mb.SetParameters(typeof(object), typeof(IntPtr));
            var il = mb.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Newobj, TypeBuilder.GetConstructor(delegateType,
                def.GetConstructor(new[] { typeof(object), typeof(IntPtr) })));
            il.Emit(OpCodes.Ret);
            _delegateCtorAdapters[key] = mb;
        }
        return mb.MakeGenericMethod(actual);
    }

    void EmitDelegateCtor(ILGenerator il, Type ft)
    {
        if (NeedsDelegateInvokeAdapter(ft)) il.Emit(OpCodes.Call, DelegateCtorAdapter(ft));
        else il.Emit(OpCodes.Newobj, DelegateCtor(ft));
    }

    // `static Unit A(<voidDelegate> d, <params>) { d.Invoke(<params>); return Unit.INSTANCE; }` — the void delegate `ft`
    // is bound as the delegate ctor's target (a closed static delegate), the remaining signature matching the Unit
    // delegate's Invoke. `invokeRet` is that Invoke return type (must carry a static `INSTANCE` singleton — `kotlin.Unit`);
    // `paramTypes` are the delegate's parameter types (identical for the void and the Unit delegate — only the return
    // differs), forwarded straight through.
    MethodInfo UnitWrapAdapter(Type ft, Type invokeRet, Type[] paramTypes)
    {
        // `invokeRet` is a referenced (baked) `kotlin.Unit` in an app/rt build -> its static INSTANCE reflects directly.
        // (Were the rt-stdlib itself to ever bind such a delegate while `kotlin.Unit` is still a TypeBuilder, this GetField
        // would throw a loud NotSupportedException rather than mis-emit — no gate hits it; add a `_types` lookup if it does.)
        var instF = invokeRet.GetField("INSTANCE", BindingFlags.Public | BindingFlags.Static)
            ?? throw new NotSupportedException($"cannot reconcile a void lambda into a delegate returning {invokeRet} (no static INSTANCE singleton)");
        var pTypes = new[] { ft }.Concat(paramTypes).ToArray();
        var mb = UnitAdapterHolder().DefineMethod("Unit$" + (_unitAdapterCounter++),
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            invokeRet, pTypes);
        var il = mb.GetILGenerator();
        for (int i = 0; i < pTypes.Length; i++) il.Emit(OpCodes.Ldarg, i);
        EmitDelegateInvoke(il, ft);
        il.Emit(OpCodes.Ldsfld, instF);
        il.Emit(OpCodes.Ret);
        return mb;
    }

    readonly Dictionary<string, TypeBuilder> _syntheticDelegates = new();

    readonly Dictionary<TypeBuilder, ConstructorBuilder> _syntheticDelegateCtors = new();

    readonly Dictionary<TypeBuilder, MethodBuilder> _syntheticDelegateInvokes = new();

    ConstructorInfo DelegateCtor(Type ft)
    {
        var sig = new[] { typeof(object), typeof(IntPtr) };
        // #150: key the synthetic-delegate ctor fast-path on IsGenericInst (GetGenericArguments-based), SYMMETRIC with
        // InvokeOf/ContainsTypeBuilder. `IsGenericType` is UNRELIABLE for a TypeBuilderInstantiation across Reflection.Emit
        // versions (see IsGenericInst) — was `ft.IsGenericType`, which only fires because this SDK happens to report True;
        // a version reporting False would skip this branch and hit `ft.GetConstructor(sig)` on the un-baked builder
        // instantiation -> NotSupportedException. GetGenericArguments().Length is always populated, so this is robust.
        if (IsGenericInst(ft) && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateCtors.TryGetValue(dtb, out var dctor))
            return TypeBuilder.GetConstructor(ft, dctor);
        return (IsGenericInst(ft) && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            ? TypeBuilder.GetConstructor(ft, ft.GetGenericTypeDefinition().GetConstructor(sig))
            : ft.GetConstructor(sig);
    }

    // A generic INSTANTIATION test that does NOT depend on `IsGenericType`, whose value for a TypeBuilderInstantiation is
    // version-dependent and unreliable (SDK 10.0.101 empirically reports IsGenericType=TRUE for a synthetic KFunc`2<int,
    // string>; older Reflection.Emit reported FALSE) — the generic-arg list, by contrast, is ALWAYS populated. All
    // TypeBuilder-instantiation branching here keys on this, never on IsGenericType.
    static bool IsGenericInst(Type t) => !t.IsGenericParameter && t.GetGenericArguments().Length > 0;

    // The delegate's `Invoke` method, bridged via TypeBuilder.GetMethod for a TypeBuilder-involving instantiation.
    MethodInfo InvokeOf(Type ft)
    {
        if (IsGenericInst(ft) && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateInvokes.TryGetValue(dtb, out var invoke))
            return TypeBuilder.GetMethod(ft, invoke);
        if (IsGenericInst(ft) && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            return TypeBuilder.GetMethod(ft, ft.GetGenericTypeDefinition().GetMethod("Invoke"));
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
        if (!IsGenericInst(ft)) return ft.GetMethod("Invoke")?.ReturnType ?? typeof(void);
        var r = ft.GetGenericTypeDefinition().GetMethod("Invoke").ReturnType;
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

    Type SyntheticFuncType(Type[] args, Type ret) =>
        SyntheticDelegateType("KFunc", args.Append(ret).ToArray(), returnsValue: true).MakeGenericType(args.Append(ret).ToArray());

    Type SyntheticActionType(Type[] args) =>
        SyntheticDelegateType("KAction", args, returnsValue: false).MakeGenericType(args);

    TypeBuilder SyntheticDelegateType(string baseName, Type[] genericArgs, bool returnsValue)
    {
        var arity = genericArgs.Length;
        var metadataName = CompilerServicesNs + baseName + "`" + arity;
        if (_syntheticDelegates.TryGetValue(metadataName, out var cached))
            return cached;

        var tb = _mod.DefineType(metadataName,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(MulticastDelegate));
        tb.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
        StampKotlinFunctionZero(tb);

        var names = Enumerable.Range(1, arity).Select(i => i == arity && returnsValue ? "TResult" : "T" + i).ToArray();
        var gps = tb.DefineGenericParameters(names);
        var invokeParams = returnsValue ? gps.Take(arity - 1).Cast<Type>().ToArray() : gps.Cast<Type>().ToArray();
        var invokeRet = returnsValue ? (Type)gps[^1] : typeof(void);

        var ctor = tb.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
            CallingConventions.Standard,
            new[] { typeof(object), typeof(IntPtr) });
        ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        var invoke = tb.DefineMethod(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            invokeRet,
            invokeParams);
        invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        _syntheticDelegates[metadataName] = tb;
        _syntheticDelegateCtors[tb] = ctor;
        _syntheticDelegateInvokes[tb] = invoke;
        return tb;
    }

}
