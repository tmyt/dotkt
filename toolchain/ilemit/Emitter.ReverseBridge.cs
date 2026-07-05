// AUTO-SPLIT companion of Program.cs — the C3b REVERSE iterator bridge (ref/runtime split).
//
// A concrete Kotlin collection class implementing the @Clr-bound `List`/`Collection`/`Iterable` (now the CLR
// IReadOnlyList/IReadOnlyCollection/IEnumerable) must satisfy IEnumerable<T> -> provide `IEnumerator<T> GetEnumerator()`.
// But it only has a Kotlin `iterator(): Iterator<T>` (hasNext/next), NOT a BCL IEnumerator<T> (MoveNext/Current). We
// generate, IN IL (Codex-reviewed decision A2 — Kotlin can't express the two distinct `Current` slots), a single generic
// adapter `<>dotkt_EnumeratorOverKotlinIterator<T>` that wraps the Kotlin iterator, plus a `GetEnumerator()` on each
// qualifying class. docs/design-clr-stdlib-ref-runtime-split.md "Reverse GetEnumerator bridge".
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

partial class Emitter
{
    TypeBuilder _enumAdapterTB;          // the open generic adapter `<>dotkt_EnumeratorOverKotlinIterator`1`
    ConstructorBuilder _enumAdapterCtor; // its ctor(Iterator<T>) on the open def

    // Emit the generic adapter type ONCE (after the Kotlin Iterator interface's methods are declared). No-op if the
    // assembly has no kotlin.collections.Iterator (nothing to bridge).
    void EmitEnumeratorAdapter()
    {
        if (_enumAdapterTB != null) return;
        if (!_types.TryGetValue("kotlin.collections.Iterator", out var iterTi)) return;
        if (!iterTi.Methods.TryGetValue("hasNext", out var openHasNext)) return;
        if (!iterTi.Methods.TryGetValue("next", out var openNext)) return;

        var tb = _mod.DefineType("<>dotkt_EnumeratorOverKotlinIterator`1",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);
        var T = tb.DefineGenericParameters("T")[0];

        var ienumGenDef = typeof(System.Collections.Generic.IEnumerator<>);
        var ienumT = ienumGenDef.MakeGenericType(T);                 // IEnumerator<T>
        var ienum = typeof(System.Collections.IEnumerator);          // non-generic
        var idisp = typeof(IDisposable);
        tb.AddInterfaceImplementation(ienumT);
        tb.AddInterfaceImplementation(ienum);
        tb.AddInterfaceImplementation(idisp);

        var iterClosed = iterTi.TB.MakeGenericType(T);               // kotlin.collections.Iterator<T>
        var fIt = tb.DefineField("_it", iterClosed, FieldAttributes.Private | FieldAttributes.InitOnly);
        var fCur = tb.DefineField("_cur", T, FieldAttributes.Private);

        // ctor(Iterator<T>) { base(); _it = arg; }
        var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[] { iterClosed });
        var cil = ctor.GetILGenerator();
        cil.Emit(OpCodes.Ldarg_0);
        cil.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
        cil.Emit(OpCodes.Ldarg_0); cil.Emit(OpCodes.Ldarg_1); cil.Emit(OpCodes.Stfld, fIt);
        cil.Emit(OpCodes.Ret);

        var hasNextC = TypeBuilder.GetMethod(iterClosed, openHasNext);
        var nextC = TypeBuilder.GetMethod(iterClosed, openNext);

        const MethodAttributes ifaceImpl = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig;

        // bool MoveNext() { if (_it.hasNext()) { _cur = _it.next(); return true; } return false; }
        var mMove = tb.DefineMethod("MoveNext", ifaceImpl, typeof(bool), Type.EmptyTypes);
        var mil = mMove.GetILGenerator();
        var lblFalse = mil.DefineLabel();
        mil.Emit(OpCodes.Ldarg_0); mil.Emit(OpCodes.Ldfld, fIt); mil.Emit(OpCodes.Callvirt, hasNextC);
        mil.Emit(OpCodes.Brfalse, lblFalse);
        mil.Emit(OpCodes.Ldarg_0);                                                       // this (for stfld _cur)
        mil.Emit(OpCodes.Ldarg_0); mil.Emit(OpCodes.Ldfld, fIt); mil.Emit(OpCodes.Callvirt, nextC);
        mil.Emit(OpCodes.Stfld, fCur);
        mil.Emit(OpCodes.Ldc_I4_1); mil.Emit(OpCodes.Ret);
        mil.MarkLabel(lblFalse);
        mil.Emit(OpCodes.Ldc_I4_0); mil.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(mMove, ienum.GetMethod("MoveNext"));

        // T get_Current()  -- the generic IEnumerator<T>.Current slot
        var mCurG = tb.DefineMethod("get_Current", ifaceImpl | MethodAttributes.SpecialName, T, Type.EmptyTypes);
        var cgi = mCurG.GetILGenerator();
        cgi.Emit(OpCodes.Ldarg_0); cgi.Emit(OpCodes.Ldfld, fCur); cgi.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(mCurG, TypeBuilder.GetMethod(ienumT, ienumGenDef.GetMethod("get_Current")));

        // object System.Collections.IEnumerator.get_Current()  -- the non-generic slot (boxes a value T)
        var mCurO = tb.DefineMethod("dotkt_NonGenericCurrent",
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeof(object), Type.EmptyTypes);
        var coi = mCurO.GetILGenerator();
        coi.Emit(OpCodes.Ldarg_0); coi.Emit(OpCodes.Ldfld, fCur); coi.Emit(OpCodes.Box, T); coi.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(mCurO, ienum.GetMethod("get_Current"));

        // void Reset() => throw new NotSupportedException();  (Kotlin iterators are not resettable)
        var mReset = tb.DefineMethod("Reset", ifaceImpl, typeof(void), Type.EmptyTypes);
        var ri = mReset.GetILGenerator();
        ri.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor(Type.EmptyTypes));
        ri.Emit(OpCodes.Throw);
        tb.DefineMethodOverride(mReset, ienum.GetMethod("Reset"));

        // void Dispose() {}
        var mDisp = tb.DefineMethod("Dispose", ifaceImpl, typeof(void), Type.EmptyTypes);
        mDisp.GetILGenerator().Emit(OpCodes.Ret);
        tb.DefineMethodOverride(mDisp, idisp.GetMethod("Dispose"));

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
    static readonly HashSet<Type> EnumerableDerived = new()
    {
        typeof(System.Collections.Generic.IEnumerable<>),
        typeof(System.Collections.Generic.IReadOnlyList<>),
        typeof(System.Collections.Generic.IReadOnlyCollection<>),
        typeof(System.Collections.Generic.IList<>),
        typeof(System.Collections.Generic.ICollection<>),
    };

    // If `itype` (a @Clr collection interface the class implements) derives from IEnumerable<E>, and the class lacks a
    // GetEnumerator but HAS a Kotlin iterator(), generate `IEnumerator<E> GetEnumerator()` (+ the non-generic explicit
    // one) that wraps this.iterator() in the adapter. Returns true if GetEnumerator is now present (so the caller skips
    // wiring "GetEnumerator" in the generic by-name loop, which would collide on the two overloads).
    bool GenerateGetEnumeratorIfNeeded(TypeInfo ti, Type itype)
    {
        if (ti.Methods.ContainsKey("GetEnumerator")) return true;       // already generated (idempotent across interfaces)
        if (_enumAdapterTB == null) return false;                       // no Kotlin Iterator in this assembly
        if (!itype.IsGenericType || !EnumerableDerived.Contains(itype.GetGenericTypeDefinition())) return false;
        if (!ti.Methods.TryGetValue("iterator", out var iterMethod)) return false;   // nothing to wrap

        var elemType = itype.GetGenericArguments()[0];

        // this.iterator() — for a generic class, anchor the call on the self-instantiation C<!0> (else "not fully
        // instantiated" at runtime), same as the generic-self-call fix elsewhere.
        var selfType = ti.IsGeneric ? ti.TB.MakeGenericType(ti.TB.GetGenericArguments()) : (Type)ti.TB;
        var iterCall = ti.IsGeneric ? TypeBuilder.GetMethod(selfType, iterMethod) : (MethodInfo)iterMethod;

        var adapterClosed = _enumAdapterTB.MakeGenericType(elemType);                 // adapter<E>
        var adapterCtor = TypeBuilder.GetConstructor(adapterClosed, _enumAdapterCtor);
        var ienumElem = typeof(System.Collections.Generic.IEnumerator<>).MakeGenericType(elemType);
        var ienumerableGenDef = typeof(System.Collections.Generic.IEnumerable<>);
        var ienumerableElem = ienumerableGenDef.MakeGenericType(elemType);

        const MethodAttributes ifaceImpl = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig;

        // IEnumerator<E> GetEnumerator() { return new adapter<E>(this.iterator()); }
        var gGen = ti.TB.DefineMethod("GetEnumerator", ifaceImpl, ienumElem, Type.EmptyTypes);
        var gi = gGen.GetILGenerator();
        gi.Emit(OpCodes.Ldarg_0);
        gi.Emit(OpCodes.Callvirt, iterCall);
        gi.Emit(OpCodes.Newobj, adapterCtor);
        gi.Emit(OpCodes.Ret);
        // Re-anchor only when the element type involves a TypeBuilder (a class type param); for a CONCRETE element type
        // IEnumerable<int> is a pure runtime type, so TypeBuilder.GetMethod would throw — use normal reflection.
        var getEnumIfaceM = ContainsTypeBuilder(elemType)
            ? TypeBuilder.GetMethod(ienumerableElem, ienumerableGenDef.GetMethod("GetEnumerator"))
            : ienumerableElem.GetMethod("GetEnumerator");
        ti.TB.DefineMethodOverride(gGen, getEnumIfaceM);
        ti.Methods["GetEnumerator"] = gGen;

        // IEnumerator System.Collections.IEnumerable.GetEnumerator() { return this.GetEnumerator(); } (explicit, non-generic)
        var gGenSelf = ti.IsGeneric ? TypeBuilder.GetMethod(selfType, gGen) : (MethodInfo)gGen;
        var gNon = ti.TB.DefineMethod("dotkt_NonGenericGetEnumerator",
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            typeof(System.Collections.IEnumerator), Type.EmptyTypes);
        var ni = gNon.GetILGenerator();
        ni.Emit(OpCodes.Ldarg_0);
        ni.Emit(OpCodes.Callvirt, gGenSelf);
        ni.Emit(OpCodes.Ret);
        ti.TB.DefineMethodOverride(gNon, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator"));
        return true;
    }
}
