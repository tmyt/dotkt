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
    // type — no infix/operator/suspend) so facadegen restores it as a Kotlin function type. This is ilemit stamping
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

    int _unitAdapterCounter;

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
        il.Emit(OpCodes.Callvirt, InvokeOf(ft));
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
        if (ft.IsGenericType && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateCtors.TryGetValue(dtb, out var dctor))
            return TypeBuilder.GetConstructor(ft, dctor);
        return (IsGenericInst(ft) && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            ? TypeBuilder.GetConstructor(ft, ft.GetGenericTypeDefinition().GetConstructor(sig))
            : ft.GetConstructor(sig);
    }

    // A generic INSTANTIATION test that survives the new Reflection.Emit (where a TypeBuilderInstantiation reports
    // IsGenericType=false): its generic-arg list is still populated.
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
