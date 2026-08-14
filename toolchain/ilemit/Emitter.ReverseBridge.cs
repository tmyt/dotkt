// AUTO-SPLIT companion of Program.cs — the C3b REVERSE iterator bridge (ref/runtime split).
//
// A concrete Kotlin collection class implementing the @Clr-bound `List`/`Collection`/`Iterable` (now the CLR
// IReadOnlyList/IReadOnlyCollection/IEnumerable) must satisfy IEnumerable<T> -> provide `IEnumerator<T> GetEnumerator()`.
// But it only has a Kotlin `iterator(): Iterator<T>` (hasNext/next), NOT a BCL IEnumerator<T> (MoveNext/Current). We
// generate, IN IL (Codex-reviewed decision A2 — Kotlin can't express the two distinct `Current` slots), a single generic
// adapter `dotkt$EnumeratorOverKotlinIterator<T>` that wraps the Kotlin iterator, plus a `GetEnumerator()` on each
// qualifying class. See docs/design-clr-collection-binding.md.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

partial class Emitter
{
    TypeBuilder _enumAdapterTB;          // the open generic adapter `dotkt$EnumeratorOverKotlinIterator`1`
    ConstructorBuilder _enumAdapterCtor; // its ctor(Iterator<T>) on the open def
    const string EnumeratorAdapterName = "dotkt$EnumeratorOverKotlinIterator`1"; // referenced from the stdlib dll in app builds
    // #139: the Kotlin iterator interface this assembly emits (set when a method carries the bir2cir `clrBridgeRole`
    // "hasNext"/"next" marker). ilemit holds NO Kotlin FQN/member names — the reverse bridge keys off this marker. Null
    // in an app build (the interface is referenced from the stdlib dll, not emitted here) -> the adapter is external.
    TypeInfo _iterBridgeIface;

    // Emit the generic adapter type ONCE (after the Kotlin Iterator interface's methods are declared). No-op if the
    // assembly emits no bridge-source Kotlin iterator interface (nothing to bridge — e.g. an app build, where it is a
    // referenced type). The interface + its hasNext/next are found via the bir2cir `clrBridgeRole` markers (#139) — the
    // Kotlin knowledge "kotlin.collections.Iterator.hasNext()/next() is the wrapped shape" lives in bir2cir, not here.
    void EmitEnumeratorAdapter()
    {
        if (_enumAdapterTB != null) return;
        var iterTi = _iterBridgeIface;
        if (iterTi == null) return;
        if (!iterTi.BridgeRoles.TryGetValue("hasNext", out var openHasNext)) return;
        if (!iterTi.BridgeRoles.TryGetValue("next", out var openNext)) return;

        var tb = _mod.DefineType("dotkt$EnumeratorOverKotlinIterator`1",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);
        StampCompilerGenerated(tb);   // #68: an ilemit-authored synthetic — flag it so dll2klib skips by attribute, not by name
        var T = tb.DefineGenericParameters("T")[0];

        var ienumGenDef = Bcl("System.Collections.Generic.IEnumerator`1");
        var ienumT = ConstructedType(ienumGenDef, T);                 // IEnumerator<T>
        var ienum = Bcl("System.Collections.IEnumerator");          // non-generic
        var idisp = Bcl("System.IDisposable");
        tb.AddInterfaceImplementation(ienumT);
        tb.AddInterfaceImplementation(ienum);
        tb.AddInterfaceImplementation(idisp);

        var iterClosed = ConstructedType(iterTi.TB, T);               // kotlin.collections.Iterator<T>
        var fIt = tb.DefineField("_it", iterClosed, FieldAttributes.Private | FieldAttributes.InitOnly);
        var fCur = tb.DefineField("_cur", T, FieldAttributes.Private);

        // ctor(Iterator<T>) { base(); _it = arg; }
        var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[] { iterClosed });
        var cil = ctor.GetILGenerator();
        cil.Emit(OpCodes.Ldarg_0);
        EmitConstructor(cil, OpCodes.Call, WellKnown<ConstructorInfo>("Object.ctor"));
        cil.Emit(OpCodes.Ldarg_0); cil.Emit(OpCodes.Ldarg_1); EmitField(cil, OpCodes.Stfld, fIt);
        cil.Emit(OpCodes.Ret);

        var hasNextC = AnchorMethod(iterClosed, openHasNext);
        var nextC = AnchorMethod(iterClosed, openNext);

        const MethodAttributes ifaceImpl = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig;

        // bool MoveNext() { if (_it.hasNext()) { _cur = _it.next(); return true; } return false; }
        var mMove = tb.DefineMethod("MoveNext", ifaceImpl, Bcl("System.Boolean"), Type.EmptyTypes);
        var mil = mMove.GetILGenerator();
        var lblFalse = mil.DefineLabel();
        mil.Emit(OpCodes.Ldarg_0); EmitField(mil, OpCodes.Ldfld, fIt); EmitMethod(mil, OpCodes.Callvirt, hasNextC);
        mil.Emit(OpCodes.Brfalse, lblFalse);
        mil.Emit(OpCodes.Ldarg_0);                                                       // this (for stfld _cur)
        mil.Emit(OpCodes.Ldarg_0); EmitField(mil, OpCodes.Ldfld, fIt); EmitMethod(mil, OpCodes.Callvirt, nextC);
        EmitField(mil, OpCodes.Stfld, fCur);
        mil.Emit(OpCodes.Ldc_I4_1); mil.Emit(OpCodes.Ret);
        mil.MarkLabel(lblFalse);
        mil.Emit(OpCodes.Ldc_I4_0); mil.Emit(OpCodes.Ret);
        // #370-residual: the local axis: wiring a slot on a type this compilation is emitting (#395)
        WireMethodOverride(tb, mMove, WellKnown<MethodInfo>("Enumerator.MoveNext"));

        // T get_Current()  -- the generic IEnumerator<T>.Current slot
        var mCurG = tb.DefineMethod("get_Current", ifaceImpl | MethodAttributes.SpecialName, T, Type.EmptyTypes);
        var cgi = mCurG.GetILGenerator();
        cgi.Emit(OpCodes.Ldarg_0); EmitField(cgi, OpCodes.Ldfld, fCur); cgi.Emit(OpCodes.Ret);
        // #370-residual: the local axis: wiring a slot on a type this compilation is emitting (#395)
        WireMethodOverride(tb, mCurG, AnchorMethod(ienumT, ienumGenDef.GetMethod("get_Current")));

        // object System.Collections.IEnumerator.get_Current()  -- the non-generic slot (boxes a value T)
        var mCurO = tb.DefineMethod("dotkt$NonGenericCurrent",
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            Bcl("System.Object"), Type.EmptyTypes);
        StampCompilerGenerated(mCurO);   // #68: ilemit-authored generated member
        var coi = mCurO.GetILGenerator();
        coi.Emit(OpCodes.Ldarg_0); EmitField(coi, OpCodes.Ldfld, fCur); coi.Emit(OpCodes.Box, T); coi.Emit(OpCodes.Ret);
        // #370-residual: the local axis: wiring a slot on a type this compilation is emitting (#395)
        WireMethodOverride(tb, mCurO, WellKnown<MethodInfo>("Enumerator.Current"));

        // void Reset() => throw new NotSupportedException();  (Kotlin iterators are not resettable)
        var mReset = tb.DefineMethod("Reset", ifaceImpl, Bcl("System.Void"), Type.EmptyTypes);
        var ri = mReset.GetILGenerator();
        EmitConstructor(ri, OpCodes.Newobj, WellKnown<ConstructorInfo>("NotSupportedException.ctor0"));
        ri.Emit(OpCodes.Throw);
        // #370-residual: the local axis: wiring a slot on a type this compilation is emitting (#395)
        WireMethodOverride(tb, mReset, WellKnown<MethodInfo>("Enumerator.Reset"));

        // void Dispose() {}
        var mDisp = tb.DefineMethod("Dispose", ifaceImpl, Bcl("System.Void"), Type.EmptyTypes);
        mDisp.GetILGenerator().Emit(OpCodes.Ret);
        // #370-residual: the local axis: wiring a slot on a type this compilation is emitting (#395)
        WireMethodOverride(tb, mDisp, WellKnown<MethodInfo>("Disposable.Dispose"));

        _enumAdapterTB = tb;
        _enumAdapterCtor = ctor;
    }

    // NOTE (2026-07-05): a former `KotlinEnumerableIfaces` hardcode (`kotlin.collections.{Set,MutableSet,
    // MutableCollection,MutableList,MutableIterable}`) — the last Kotlin-language-knowledge leak in ilemit — was
    // REMOVED. bir2cir lowers every such Kotlin collection interface to its `clrg:System.Collections...` alias in
    // every runnable (rt/app) build, so the reverse GetEnumerator bridge fires purely on the `clr:`/`clrg:` spec via
    // GenerateGetEnumeratorIfNeeded below (EnumerableDerived). The bare Kotlin-name spec only survives in the ref
    // build (compile-time-only metadata where @Clr substitution is OFF), and there the class does NOT actually
    // implement IEnumerable, so no bridge is wanted. ilemit therefore holds no Kotlin-collection identity at all.

    // The BCL collection interfaces that derive from IEnumerable<T> — a class implementing any of these needs a
    // GetEnumerator. (Mutable IList/ICollection added when MutableList binds; dictionaries handled separately.)
    readonly HashSet<Type> EnumerableDerived;

    // If `itype` (a @Clr collection interface the class implements) derives from IEnumerable<E>, and the class lacks a
    // GetEnumerator but HAS a Kotlin iterator(), generate `IEnumerator<E> GetEnumerator()` (+ the non-generic explicit
    // one) that wraps this.iterator() in the adapter. Returns true if GetEnumerator is now present (so the caller skips
    // wiring "GetEnumerator" in the generic by-name loop, which would collide on the two overloads).
    bool GenerateGetEnumeratorIfNeeded(TypeInfo ti, Type itype)
    {
        if (ti.Methods.ContainsKey("GetEnumerator")) return true;       // already generated (idempotent across interfaces)
        // The adapter is emitted LOCALLY only in the assembly that emits kotlin.collections.Iterator (the stdlib rt
        // build). In an APP assembly it is a REFERENCED public generic type in DotKt.Stdlib.dll — resolve it so an app
        // class implementing Iterable<E> (bir2cir-lowered to IEnumerable<E>) still gets a synthesized GetEnumerator (#58).
        Type externalAdapterOpen = null;
        if (_enumAdapterTB == null)
        {
            externalAdapterOpen = ResolvesExternally(EnumeratorAdapterName) ? ResolveType(EnumeratorAdapterName) : null;
            if (externalAdapterOpen == null) return false;              // no Kotlin Iterator adapter available anywhere
        }
        if (!itype.IsGenericType || !EnumerableDerived.Contains(itype.GetGenericTypeDefinition())) return false;
        // The class's own `iterator()` (bir2cir `clrBridgeRole` marker, #139 — not the Kotlin member name).
        if (!ti.BridgeRoles.TryGetValue("iterator", out var iterMethod)) return false;   // nothing to wrap

        var elemType = itype.GetGenericArguments()[0];

        // this.iterator() — for a generic class, anchor the call on the self-instantiation C<!0> (else "not fully
        // instantiated" at runtime), same as the generic-self-call fix elsewhere.
        var selfType = ti.IsGeneric ? ConstructedType(ti.TB, ti.TB.GetGenericArguments()) : (Type)ti.TB;
        var iterCall = ti.IsGeneric ? AnchorMethod(selfType, iterMethod) : (MethodInfo)iterMethod;

        // adapter<E> + its ctor(Iterator<E>). Local build: the adapter is our TypeBuilder (TypeBuilder.GetConstructor
        // re-anchors the ConstructorBuilder). App build: it is a referenced runtime type — MakeGenericType, then reflect
        // the (single) ctor for a concrete element, or TypeBuilder.GetConstructor when the element is a class type param.
        Type adapterClosed; ConstructorInfo adapterCtor;
        if (_enumAdapterTB != null)
        {
            adapterClosed = ConstructedType(_enumAdapterTB, elemType);
            adapterCtor = AnchorConstructor(adapterClosed, _enumAdapterCtor);
        }
        else
        {
            adapterClosed = ConstructedType(externalAdapterOpen, elemType);
            adapterCtor = ContainsTypeBuilder(elemType)
                ? AnchorConstructor(adapterClosed, externalAdapterOpen.GetConstructors()[0])
                : adapterClosed.GetConstructors()[0];
        }
        var ienumElem = ConstructedType(Bcl("System.Collections.Generic.IEnumerator`1"), elemType);
        var ienumerableGenDef = Bcl("System.Collections.Generic.IEnumerable`1");
        var ienumerableElem = ConstructedType(ienumerableGenDef, elemType);

        const MethodAttributes ifaceImpl = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig;

        // IEnumerator<E> GetEnumerator() { return new adapter<E>(this.iterator()); }
        var gGen = ti.TB.DefineMethod("GetEnumerator", ifaceImpl, ienumElem, Type.EmptyTypes);
        var gi = gGen.GetILGenerator();
        gi.Emit(OpCodes.Ldarg_0);
        EmitMethod(gi, OpCodes.Callvirt, iterCall);
        EmitConstructor(gi, OpCodes.Newobj, adapterCtor);
        gi.Emit(OpCodes.Ret);
        // Re-anchor only when the element type involves a TypeBuilder (a class type param); for a CONCRETE element type
        // IEnumerable<int> is a pure runtime type, so TypeBuilder.GetMethod would throw — use normal reflection.
        var getEnumIfaceM = ContainsTypeBuilder(elemType)
            // #370-residual: the local axis: wiring a slot on a type this compilation is emitting (#395)
            ? AnchorMethod(ienumerableElem, ienumerableGenDef.GetMethod("GetEnumerator"))
            // #370-residual: the local axis: wiring a slot on a type this compilation is emitting (#395)
            : WellKnown<MethodInfo>("Enumerable.GetEnumerator");
        WireMethodOverride(ti.TB, gGen, getEnumIfaceM);
        ti.Methods["GetEnumerator"] = gGen;

        // IEnumerator System.Collections.IEnumerable.GetEnumerator() { return this.GetEnumerator(); } (explicit, non-generic)
        var gGenSelf = ti.IsGeneric ? AnchorMethod(selfType, gGen) : (MethodInfo)gGen;
        var gNon = ti.TB.DefineMethod("dotkt$NonGenericGetEnumerator",
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            Bcl("System.Collections.IEnumerator"), Type.EmptyTypes);
        StampCompilerGenerated(gNon);   // #68: ilemit-authored generated member
        var ni = gNon.GetILGenerator();
        ni.Emit(OpCodes.Ldarg_0);
        EmitMethod(ni, OpCodes.Callvirt, gGenSelf);
        ni.Emit(OpCodes.Ret);
        WireMethodOverride(ti.TB, gNon, WellKnown<MethodInfo>("Enumerable.GetEnumerator"));
        return true;
    }
}
