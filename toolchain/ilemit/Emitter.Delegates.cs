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

    // A wide delegate type this assembly DEFINES is marked [KotlinFunction(0)] (a plain function type — no
    // infix/operator/suspend) so dll2klib restores it as a Kotlin function type. This is ilemit stamping
    // its OWN emitted member (analogous to StampCompilerGenerated), NOT round-trip generation over user code: the
    // attribute CLASS is the ordinary CIR-defined `KotlinFunctionAttribute` in `_types` (bir2cir emits it, #71 S2),
    // whose (int) ctor is resolved generically. Absent (a --no-stdlib or runtime build that emits no attr class) -> skip.
    void StampKotlinFunctionZero(TypeBuilder tb)
    {
        if (!_types.TryGetValue(CompilerServicesNs + "KotlinFunctionAttribute", out var ti)) return;
        EnsureCtorsDefined(ti);
        if (ti.Ctors.Count == 0) return;
        SetAttribute(tb.SetCustomAttribute, ti.Ctors[0], new[] { Bcl("System.Int32") }, 0);
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
            Bcl("System.Object"));
        SetAttribute(_unitAdapterTB.SetCustomAttribute,
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
        EmitField(il, OpCodes.Ldsfld, instF);
        il.Emit(OpCodes.Ret);
        return mb;
    }

    // The wide delegate types THIS assembly defines, by metadata name. A stdlib build seeds it with the whole
    // canonical 17..22 family (DefineCanonicalDelegates); any build may additionally add a deferred 23+ shape this
    // compilation used. A CANONICAL name is therefore present here only in a stdlib build, and is resolved from the
    // referenced stdlib in every other one — never both.
    readonly Dictionary<string, TypeBuilder> _syntheticDelegates = new();

    readonly Dictionary<TypeBuilder, ConstructorBuilder> _syntheticDelegateCtors = new();

    readonly Dictionary<TypeBuilder, MethodBuilder> _syntheticDelegateInvokes = new();

    ConstructorInfo DelegateCtor(Type ft)
    {
        var sig = new[] { Bcl("System.Object"), Bcl("System.IntPtr") };
        // #150: key the synthetic-delegate ctor fast-path on IsGenericInst (GetGenericArguments-based), SYMMETRIC with
        // InvokeOf/ContainsTypeBuilder. `IsGenericType` is UNRELIABLE for a TypeBuilderInstantiation across Reflection.Emit
        // versions (see IsGenericInst) — was `ft.IsGenericType`, which only fires because this SDK happens to report True;
        // a version reporting False would skip this branch and hit `ft.GetConstructor(sig)` on the un-baked builder
        // instantiation -> NotSupportedException. GetGenericArguments().Length is always populated, so this is robust.
        if (IsGenericInst(ft) && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateCtors.TryGetValue(dtb, out var dctor))
            return AnchorConstructor(ft, dctor);
        return (IsGenericInst(ft) && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            ? AnchorConstructor(ft, ft.GetGenericTypeDefinition().GetConstructor(sig))
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
        if (IsGenericInst(ft) && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateInvokes.TryGetValue(dtb, out var invoke))
            return AnchorMethod(ft, invoke);
        if (IsGenericInst(ft) && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            return AnchorMethod(ft, ft.GetGenericTypeDefinition().GetMethod("Invoke"));
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
        if (!IsGenericInst(ft)) return ft.GetMethod("Invoke")?.ReturnType ?? Bcl("System.Void");
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

    // #220 — the CANONICAL high-arity delegate family. Kotlin function arities 17..22 (System.Func/Action stop at 16;
    // 23 is the frontend's BuiltInFunctionArity.BIG_ARITY) have ONE definition for the whole platform, in the stdlib
    // — so a `(…17 args…) -> R` in a public signature is the SAME Reflection type on both sides of a module boundary.
    // The stdlib self-build emits the family unconditionally (DefineCanonicalDelegates); every other assembly
    // RESOLVES it out of the referenced stdlib and defines nothing. Arity 23 and above is still minted per assembly
    // pending the variadic big-arity ABI, so it is nominally module-local and not a valid cross-assembly signature.
    internal const int CanonicalDelegateMinArity = 17;
    internal const int CanonicalDelegateMaxArity = 22;

    // Emit the whole canonical family into the stdlib twins (both `--build-stdlib` modes), unconditionally: the
    // definition exists because the ABI says it does, not because this compilation happened to need it.
    void DefineCanonicalDelegates()
    {
        for (var arity = CanonicalDelegateMinArity; arity <= CanonicalDelegateMaxArity; arity++)
        {
            SyntheticDelegateType("KAction", arity, returnsValue: false);
            SyntheticDelegateType("KFunc", arity + 1, returnsValue: true);
        }
    }

    // The delegate DEFINITION for a wide function type. In the canonical range this is a lookup, never a choice:
    // the stdlib build finds the family it just defined, and every other build resolves the stdlib's copy exactly
    // as it resolves any other referenced type. Only the deferred 23+ range still mints a definition here.
    Type WideDelegateDef(string baseName, int genericArity, int kotlinArity, bool returnsValue)
    {
        if (kotlinArity > CanonicalDelegateMaxArity) return SyntheticDelegateType(baseName, genericArity, returnsValue);
        var name = CompilerServicesNs + baseName + "`" + genericArity;
        if (_syntheticDelegates.TryGetValue(name, out var own)) return own;
        try { return ResolveType(name); }
        catch (NotSupportedException e)
        {
            // The compile-reference set has no stdlib (an ilemit invocation built without one). Minting a definition
            // here instead is exactly the per-assembly ABI #220 removed, so this is a refusal — and it names the
            // reason, because `target type '…KFunc`18' is absent` alone does not explain why a 17-parameter lambda
            // needs the stdlib when a 16-parameter one does not.
            throw new NotSupportedException(
                $"a Kotlin function type of {kotlinArity} parameters is `{name}`, which is defined by the DotKt " +
                "stdlib — the compile reference set must contain it (System.Func/Action only reach 16 parameters)", e);
        }
    }

    Type SyntheticFuncType(Type[] args, Type ret)
    {
        var all = args.Append(ret).ToArray();
        return ConstructedType(WideDelegateDef("KFunc", all.Length, args.Length, returnsValue: true), all);
    }

    Type SyntheticActionType(Type[] args) =>
        ConstructedType(WideDelegateDef("KAction", args.Length, args.Length, returnsValue: false), args);

    TypeBuilder SyntheticDelegateType(string baseName, int arity, bool returnsValue)
    {
        var metadataName = CompilerServicesNs + baseName + "`" + arity;
        if (_syntheticDelegates.TryGetValue(metadataName, out var cached))
            return cached;

        var tb = _mod.DefineType(metadataName,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            Bcl("System.MulticastDelegate"));
        SetAttribute(tb.SetCustomAttribute,
            Bcl("System.Runtime.CompilerServices.CompilerGeneratedAttribute").GetConstructor(Type.EmptyTypes), Array.Empty<Type>());
        StampKotlinFunctionZero(tb);

        var names = Enumerable.Range(1, arity).Select(i => i == arity && returnsValue ? "TResult" : "T" + i).ToArray();
        var gps = tb.DefineGenericParameters(names);
        // VARIANCE, exactly as `System.Func<in T…, out TResult>` / `System.Action<in T…>` declare it. A Kotlin function
        // type is contravariant in its parameters and covariant in its result, so `(Any, …) -> String` IS a
        // `(String, …) -> Any` and the frontend accepts the assignment. Without these flags the two instantiations are
        // unrelated invariant constructed types and the store is StackUnexpected — a break at exactly arity 17, where
        // the family stops being the BCL's. Every parameter occurs only in the Invoke position its variance permits.
        for (var i = 0; i < gps.Length; i++)
            gps[i].SetGenericParameterAttributes(returnsValue && i == arity - 1
                ? GenericParameterAttributes.Covariant
                : GenericParameterAttributes.Contravariant);
        var invokeParams = returnsValue ? gps.Take(arity - 1).Cast<Type>().ToArray() : gps.Cast<Type>().ToArray();
        var invokeRet = returnsValue ? (Type)gps[^1] : Bcl("System.Void");

        var ctor = tb.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
            CallingConventions.Standard,
            new[] { Bcl("System.Object"), Bcl("System.IntPtr") });
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
