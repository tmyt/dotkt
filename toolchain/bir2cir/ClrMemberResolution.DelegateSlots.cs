using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE DELEGATE A LAMBDA OR CALLABLE REFERENCE PHYSICALLY CONSTRUCTS.
//
// A Kotlin lambda has a NATURAL delegate — the family `BirTypeLowering` picked for its function type
// (`System.Action`/`System.Func`, or the wide `KAction`/`KFunc`). The slot it fills often declares a DIFFERENT
// delegate: a custom .NET delegate (`ThreadStart`, `Cbk.GenericResult<T>`), or another construction of the same
// family whose return was erased (`Func<string,object>` for a `Func<string,string>` value). Which delegate a
// construction physically builds is a CLR representation decision, so it is made here and stated in CIR; the
// emitter used to make it from the reflected parameter type at the call site, and to author the reconciling
// adapter itself.
//
// Two outcomes, one rule — compare the natural delegate with the slot's:
//
//   * SAME delegate                      -> nothing to state; the construction already builds it.
//   * DIFFERENT, and both are bindable    -> RETARGET: the construction's `funcType` becomes the slot's delegate
//                                           and its `delegateCtorRef` is resolved against that delegate.
//                                           ECMA-335 II.14.6 delegate compatibility covers the residual
//                                           reference covariance/contravariance between the two.
//   * DIFFERENT because the lambda is VOID-returning and the slot's `Invoke` returns a value -> ADAPT: no
//     method pointer is delegate-compatible with that slot (a `void` return is assignable to nothing), so the
//     value has to be produced. bir2cir authors an adapter CLASS holding the natural delegate, whose `invoke`
//     calls it and returns the `Unit` singleton, and the construction becomes an ordinary `newClosure` over it.
//   * DIFFERENT because the target method returns a VALUE and the slot returns VOID -> ADAPT: call the natural
//     value-returning delegate once and discard its result. Binding its method directly to Action is invalid IL.
//
// The adapter class is generic in the delegate's PARAMETER TYPES, not in the enclosing frame's type variables:
// `Adapter<T0..Tn-1>` holds an `Action<T0..Tn-1>` and declares `invoke(T0..Tn-1)`. The site instantiates it with
// the actual parameter types, whatever they mention. That is what frees the adapter from POSITIVE constraints: a
// delegate family declares none on its own parameters, so whatever is legal as `Action<X>`'s argument is legal as
// `Adapter<X>`'s, and any bound `X` itself owes was already satisfied where `X` was written. Generalizing over the
// frame's type variables instead would have to re-declare their constraints, because a constrained construction
// (`Constrained<T> where T : IMarker`) appearing INSIDE a parameter would then be spelled over an unconstrained
// parameter of the adapter. The one attribute the position DOES owe is the byref-like ANTI-constraint: a delegate
// parameter may be a `ref struct` (`Action<Span<int>>` is a legal delegate), and a parameter standing for it has
// to admit that instantiation — see `TypedPhysicalDelegateAdapter.AdapterClass`.
//
// A SLOT is any declared position a construction fills: an argument's parameter, a delegate-typed property
// setter's parameter, a public delegate field, an event's handler. All four take the one rule, because "which
// delegate does this lambda build" is the same question in each.
//
// The decision is MARKED during resolution — where the selected member and its declared type are known — and
// MATERIALIZED once every resolution pass has run, so a construction is rewritten after the passes that read its
// Kotlin-shaped `funcType` are done with it.
static partial class ClrMemberResolution
{
    // Transient: the delegate this construction's SLOT declares, as the resolved physical `fqn`. Consumed by
    // MaterializeDelegateSlots and never written to CIR.
    const string DelegateSlotKey = "dotktDelegateSlot";

    internal static bool HasDeclaredDelegateSlot(JsonObject construction) =>
        construction.ContainsKey(DelegateSlotKey);

    /// <summary>
    /// Mark every delegate construction among <paramref name="call"/>'s arguments with the delegate its
    /// parameter declares.
    /// </summary>
    static void StampDelegateArgumentTargets(JsonObject call, MethodInfo method, TypeNode[] ownerArgs,
        string argumentKey = "args")
    {
        var methodArgs = (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray()
            ?? Array.Empty<TypeNode>();
        if (methodArgs.Any(t => t == null)) return;
        // A parameter vector that cannot be CLOSED names no slot. Every resolver above already refuses to select a
        // generic declaration whose type arguments the site does not state, but the completeness of the
        // substitution is this rule's own precondition — a leftover callee type variable here would be read as one
        // of the CALLER's, and the construction would be pointed at a delegate that is not the parameter's.
        if (method.IsGenericMethodDefinition && methodArgs.Length != method.GetGenericArguments().Length) return;
        StampDelegateArgumentTargets(call, method.GetParameters(), ownerArgs, methodArgs, argumentKey);
    }

    static void StampDelegateArgumentTargets(JsonObject call, ParameterInfo[] parameters,
        TypeNode[] ownerArgs, TypeNode[] methodArgs, string argumentKey = "args")
        => StampDelegateArgumentTargets(call,
            parameters.Select(parameter => RefTypeOf(parameter.ParameterType)).ToArray(), ownerArgs, methodArgs,
            argumentKey);

    static void StampDelegateArgumentTargets(JsonObject call, TypeNode[] parameters,
        TypeNode[] ownerArgs, TypeNode[] methodArgs, string argumentKey = "args")
    {
        if (call[argumentKey] is not JsonArray args || parameters.Length != args.Count) return;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (args[i] is not JsonObject arg) continue;
            var slot = SupertypeGraph.SubstOwnerTvs(parameters[i], ownerArgs);
            MarkDelegateSlot(arg, SubstituteMethodTypeArgs(slot, methodArgs));
        }
    }

    static TypeNode SubstituteMethodTypeArgs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name,
            nested.Select(a => SubstituteMethodTypeArgs(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteMethodTypeArgs(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteMethodTypeArgs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstituteMethodTypeArgs(a.Elem, args), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteMethodTypeArgs(b.Of, args)),
        TypeNode.Ptr p => new TypeNode.Ptr(SubstituteMethodTypeArgs(p.Of, args)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, SubstituteMethodTypeArgs(m.M, args),
            SubstituteMethodTypeArgs(m.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            SubstituteMethodTypeArgs(fn.Ret, args),
            fn.Params.Select(p => SubstituteMethodTypeArgs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstituteMethodTypeArgs(fn.Recv, args), fn.Clr,
            fn.Ctx?.Select(c => SubstituteMethodTypeArgs(c, args)).ToArray()),
        _ => type,
    };

    /// <summary>
    /// Record the delegate a construction's slot declares, when that slot is a delegate at all.
    /// </summary>
    /// <remarks>
    /// Only the slot is recorded, never the outcome: the construction's own `funcType` can still be rewritten by
    /// a later pass, so what to do about the pair is decided when both halves are final.
    ///
    /// LAST WRITER WINS. A later resolution can re-point the same operation at a different declaration; the mark
    /// belongs to whichever declaration the operation ends up naming, and keeping the first would materialize a
    /// slot the call no longer has.
    /// </remarks>
    internal static bool MarkDelegateSlot(JsonObject construction, TypeNode slotType)
        => MarkDelegateSlot(construction, slotType, _refs, _localTypes);

    internal static bool MarkDelegateSlot(JsonObject construction, TypeNode slotType,
        ReferenceMetadataIndex refs, IReadOnlySet<string> localTypes)
    {
        if ((construction["k"] as JsonValue)?.GetValue<string>() is not
            ("newDelegate" or "newClosure" or "newBoundDelegate" or "newBoundClrDelegate" or "newClrStaticDelegate"))
            return false;
        if (DelegateFqnOfSlot(slotType, refs, localTypes) is not TypeNode.Fqn slotDelegate)
            return construction.ContainsKey(DelegateSlotKey);
        construction[DelegateSlotKey] = TypeJson.Write(slotDelegate);
        return true;
    }

    // The physical delegate a declaration slot IS, or null when the slot is not a delegate.
    //
    // The slot is read off the resolved declaration, so it speaks the REFERENCE universe's vocabulary — a stdlib
    // reference assembly declares `kotlin.Any`/`kotlin.Boolean` verbatim, and a Kotlin collection face is not yet
    // its BCL twin. It has to reach the same physical spelling every member reference states, so it goes through
    // the SAME configured lowering `PhysicalOwnerArg` uses rather than the unconfigured one; otherwise the
    // construction's `funcType` and its own `delegateCtorRef` would name two different delegates.
    // A delegate's arguments are METHOD slots (Root-H), the same classification the reference carrier makes for
    // them, so they are lowered in that position and not as storage.
    static TypeNode.Fqn DelegateFqnOfSlot(TypeNode slotType, ReferenceMetadataIndex refs = null,
        IReadOnlySet<string> localTypes = null)
    {
        refs ??= _refs;
        localTypes ??= _localTypes;
        if (refs == null)
            throw new InvalidOperationException("bir2cir: delegate slot resolution has no reference universe");
        var shape = slotType;
        while (shape is TypeNode.Nullable nullable) shape = nullable.Of;
        while (shape is TypeNode.Oblivious oblivious) shape = oblivious.Of;
        var physical = BirTypeLowering.LowerPhysicalType(shape, refs.Aliases, refs.IsValueType,
            refs.PhysicalTypeNames, typeArg: false, localTypes ?? new HashSet<string>(), nullableFrames: refs.NullableTypeFrames);
        if (physical is TypeNode.Fn fn) physical = BirTypeLowering.DelegateFqnOf(fn);
        if (physical is not TypeNode.Fqn named) return null;
        var open = ResolveOwnerType(named, refs);
        return open != null && IsDelegate(open) ? named : null;
    }

    // ---- materialization -----------------------------------------------------------------------

    static int _nextAdapter;
    static int _nextValueAdapter;

    // Both the earlier construction pass and the final typed value-flow owner
    // synthesize this family. Keep one allocator across their explicit boundary.
    internal static string FreshDelegateAdapterName(string scope) => $"dotkt${scope}$UnitDelegateAdapter{_nextAdapter++}";
    internal static string FreshUnitDelegateTemporary() => $"dotkt$unitDelegateValue{_nextValueAdapter++}";

    // Local function slots use Fn; exact imported CLR signatures use the equivalent named Func/Action.
    // Read the latter's physical Invoke and close its owner parameters, without folding the Unit class to void.
    static TypeNode.Fn PhysicalFunctionShape(TypeNode type, bool includeNominal = false)
    {
        if (type is TypeNode.Fn fn) return fn;
        if (type is not TypeNode.Fqn named || ResolveOwnerType(named) is not Type owner
            || !IsDelegate(owner)) return null;
        var family = DelegateFamily(owner);
        if (family == null && !includeNominal) return null;
        var invoke = owner.GetMethod("Invoke");
        if (invoke == null) return null;
        var args = named.Args ?? Array.Empty<TypeNode>();
        var ret = invoke.ReturnType.FullName == "System.Void"
            ? new TypeNode.Fqn("void") : SubstOwnerParams(invoke.ReturnType, args);
        return new TypeNode.Fn(false, ret,
            invoke.GetParameters().Select(p => SubstOwnerParams(p.ParameterType, args)).ToArray(), null, family);
    }

    static bool HasRepresentationSlotSeam(TypeNode.Fn natural, TypeNode.Fn target)
    {
        static bool IsObject(TypeNode type) => type is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" };
        static bool Seam(TypeNode left, TypeNode right) => !SameDelegate(left, right)
            // Distinct physical type variables are not delegate-compatible just
            // because they represent the same source parameter. Native witnesses
            // and storage companions cross through the ordinary value adapter.
            && (IsObject(left) || IsObject(right) || left is TypeNode.Tv || right is TypeNode.Tv);
        static TypeNode Core(TypeNode type) => type switch {
            TypeNode.Nullable nullable => Core(nullable.Of),
            TypeNode.Oblivious oblivious => Core(oblivious.Of),
            _ => type,
        };
        static bool NeedsNominalDowncast(TypeNode value, TypeNode destination) =>
            Core(destination) is TypeNode.Fqn { Args: { Length: > 0 } } exact
            && Core(value) is TypeNode.Fqn carrier
            && (_localCarriers?.TryGetValue(exact.Name, out var local) == true && local == carrier.Name
                || _refs?.TryExistentialPhysicalOwner(exact.Name, out var referenced) == true
                    && referenced == carrier.Name);
        if (natural.DelegateParams.Length != target.DelegateParams.Length)
            throw new InvalidOperationException("Delegate slot parameter count differs from its construction");
        // Returns flow from the natural method to the destination delegate; parameters flow in reverse.
        // Exact->carrier reference covariance/contravariance is already delegate-compatible. Only the proven
        // carrier->exact direction needs the value adapter's checked conversion, never a byref conversion.
        return natural.DelegateParams.Zip(target.DelegateParams,
                (source, destination) => Seam(source, destination) || NeedsNominalDowncast(destination, source))
            .Any(seam => seam)
            || Seam(natural.Ret, target.Ret) || NeedsNominalDowncast(natural.Ret, target.Ret);
    }

    static bool NeedsSlotAdapter(TypeNode.Fn natural, TypeNode.Fn target) =>
        HasRepresentationSlotSeam(natural, target)
        || ((natural.Ret is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" })
            != (target.Ret is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" }));

    // Capture the natural delegate once. The adapter's Invoke owns the destination signature, and its body
    // crosses each representation seam explicitly. Generic parameters stand for complete slot types rather
    // than borrowing the enclosing method's frame or reproducing its constraints.
    internal sealed record DelegateAdapterPlan(TypeNode.Fn Frame, int Arity, TypeNode Return,
        TypeNode[] Parameters, int? GenericArity, TypeNode[] Arguments);

    // This is representation policy over immutable TypeNodes, shared by the
    // owning construction and final value-flow consumers.
    static DelegateAdapterPlan BoxedDelegateAdapterPlan(TypeNode.Fn natural, TypeNode.Fn target)
    {
        var sourceParameters = natural.DelegateParams;
        var targetParameters = target.DelegateParams;
        var arity = sourceParameters.Length;
        var producesUnit = natural.Ret is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" }
            && target.Ret is not TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };
        if (producesUnit && target.Ret is not TypeNode.Fqn { Args: null, Name: "kotlin.Unit" or "object" or "System.Object" })
            throw new InvalidOperationException("Delegate return slot cannot receive the Unit singleton");
        var arguments = new List<TypeNode>();
        TypeNode Lift(TypeNode type)
        {
            // Void is a return-slot shape, not a CLR generic argument or a value to box.
            if (type is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" }) return type;
            // A managed reference is a slot shape, never a generic argument. Equal slots share a variable,
            // preserving byref identity and avoiding box/unbox on unchanged (possibly byref-like) values.
            if (type is TypeNode.ByRef byRef) return new TypeNode.ByRef(Lift(byRef.Of));
            var index = arguments.FindIndex(argument => SameDelegate(argument, type));
            if (index < 0) { index = arguments.Count; arguments.Add(type); }
            return new TypeNode.Tv("type", index);
        }
        var sourceFrame = new TypeNode.Fn(false, Lift(natural.Ret),
            sourceParameters.Select(Lift).ToArray(), null, natural.Clr);
        var targetFrame = targetParameters.Select(Lift).ToArray();
        var targetReturn = producesUnit ? target.Ret : Lift(target.Ret);
        return new(sourceFrame, arity, targetReturn, targetFrame, arguments.Count, arguments.ToArray());
    }

    // ONE delegate, TWO vocabularies. The natural side is spelled as the document spells a type — an arity-free
    // dotted name, a `nullable` wrapper, a function type still an `fn` — while the slot side was read off metadata,
    // where a generic name carries its arity backtick, `Nullable<T>` is an ordinary construction and a function type
    // is already its delegate. Comparing the two spellings directly answers "different" for every external
    // `Func`/`Action` parameter a Kotlin lambda fills, which would send the whole population down the retarget arm
    // and put a second spelling of the same type into CIR. Compare on a key that erases the vocabulary difference
    // and nothing else, so only a REAL difference of delegate identity is acted on.
    static bool SameDelegate(TypeNode natural, TypeNode slot) =>
        DelegateIdentityKey(natural).Equals(DelegateIdentityKey(slot));

    static TypeNode DelegateIdentityKey(TypeNode type) => type switch
    {
        TypeNode.Fn { Clr: not null } fn => DelegateIdentityKey(BirTypeLowering.DelegateFqnOf(fn)),
        TypeNode.Fqn f => new TypeNode.Fqn(WithoutArity(f.Name),
            f.Args?.Select(DelegateIdentityKey).ToArray()),
        TypeNode.Nullable n => new TypeNode.Fqn("System.Nullable", new[] { DelegateIdentityKey(n.Of) }),
        TypeNode.Oblivious o => DelegateIdentityKey(o.Of),
        TypeNode.Array a => new TypeNode.Array(DelegateIdentityKey(a.Elem), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(DelegateIdentityKey(b.Of)),
        TypeNode.Ptr p => new TypeNode.Ptr(DelegateIdentityKey(p.Of)),
        _ => type,
    };

    // Drop every `\u0060N` arity group, keeping the rest of the name — including `+` nesting, which truncating at the
    // first backtick would destroy and which distinguishes two different nested declarations.
    static string WithoutArity(string name)
    {
        if (name.IndexOf('\u0060') < 0) return name;
        var text = new System.Text.StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] != '\u0060') { text.Append(name[i]); continue; }
            i++;
            while (i < name.Length && char.IsAsciiDigit(name[i])) i++;
            i--;
        }
        return text.ToString();
    }

    // The declared return of a delegate's `Invoke`, in the instantiation the slot names. ECMA-335 II.14.6 gives a
    // delegate exactly one `Invoke`, so there is no candidate set; its return is read off the open declaration and
    // substituted positionally, exactly as the constructed type would report it.
    static TypeNode SlotInvokeReturn(TypeNode.Fqn slot)
    {
        var open = ResolveOwnerType(slot);
        if (open == null) return null;
        var invoke = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Invoke").ToList();
        if (invoke.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: the delegate '{slot.Name}' has {invoke.Count} Invoke declarations, not one");
        return SubstOwnerParams(invoke[0].ReturnType, slot.Args ?? Array.Empty<TypeNode>());
    }

    const string UnitFqn = "kotlin.Unit";
    const string UnitInstance = "INSTANCE";

    // Replace the void-returning construction with a `newClosure` over an adapter that produces the Unit value the
    // slot's Invoke has to return. The original construction becomes the adapter's single capture, so its own
    // resolved constructor and Invoke keep describing the natural delegate.
    static DelegateAdapterPlan VoidDelegateAdapterPlan(TypeNode.Fn naturalFn, TypeNode.Fqn slot, TypeNode slotReturn)
    {
        if (PhysicalFunctionShape(slot, includeNominal: true) is TypeNode.Fn target
            && HasRepresentationSlotSeam(naturalFn, target))
            return BoxedDelegateAdapterPlan(naturalFn, target);
        // The value the adapter returns is the `Unit` singleton, so the slot's Invoke must be able to receive it.
        // Kotlin resolution only fills such a slot from a `Unit` lambda, so anything else is a producer defect
        // rather than a program this rule has to accept.
        if (slotReturn is not TypeNode.Fqn { Args: null } returnName
            || (returnName.Name != UnitFqn && returnName.Name != "object" && returnName.Name != "System.Object"))
            throw new InvalidOperationException(
                $"bir2cir: a Unit lambda fills '{slot.Name}', whose Invoke returns "
                + $"{TypeNode.ToJson(slotReturn)} — no Unit value can be produced for that slot");

        var parameters = naturalFn.DelegateParams;
        var frame = new TypeNode.Fn(false, new TypeNode.Fqn("void"),
            Enumerable.Range(0, parameters.Length).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray(),
            null, naturalFn.Clr);
        return new(frame, parameters.Length, slotReturn, null, null, parameters);
    }

    internal static string DelegateAdapterKey(DelegateAdapterPlan plan) =>
        DelegateAdapterKey(plan.Frame, plan.Arity, plan.Return, plan.Parameters, plan.GenericArity);

    static string DelegateAdapterKey(TypeNode.Fn frame, int arity, TypeNode slotReturn,
        TypeNode[] slotParameters, int? genericArity) =>
        $"{TypeNode.ToJson(frame)}|{arity}|{genericArity}|{TypeNode.ToJson(slotReturn)}|"
            + (slotParameters == null ? "unit" : string.Join(";", slotParameters.Select(TypeNode.ToJson)));
}
