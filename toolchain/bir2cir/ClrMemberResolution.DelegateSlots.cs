using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE DELEGATE A LITERAL LAMBDA PHYSICALLY CONSTRUCTS.
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
//                                           and its `delegateCtorRef`/`invokeRef` are re-resolved against it.
//                                           ECMA-335 II.14.6 delegate compatibility covers the residual
//                                           reference covariance/contravariance between the two.
//   * DIFFERENT because the lambda is VOID-returning and the slot's `Invoke` returns a value -> ADAPT: no
//     method pointer is delegate-compatible with that slot (a `void` return is assignable to nothing), so the
//     value has to be produced. bir2cir authors an adapter CLASS holding the natural delegate, whose `invoke`
//     calls it and returns the `Unit` singleton, and the construction becomes an ordinary `newClosure` over it.
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
// to admit that instantiation — see `AdapterClass`.
//
// The decision is MARKED during resolution — where the selected member and its parameter vector are known — and
// MATERIALIZED once every resolution pass has run, so a construction is rewritten after the passes that read its
// Kotlin-shaped `funcType` are done with it.
static partial class ClrMemberResolution
{
    // Transient: the delegate this construction's SLOT declares, as the resolved physical `fqn`. Consumed by
    // MaterializeDelegateSlots and never written to CIR.
    const string DelegateSlotKey = "dotktDelegateSlot";

    /// <summary>
    /// Mark every literal delegate construction among <paramref name="call"/>'s arguments with the delegate its
    /// parameter declares.
    /// </summary>
    static void StampDelegateArgumentTargets(JsonObject call, MethodInfo method, TypeNode[] ownerArgs,
        string argumentKey = "args")
    {
        var methodArgs = (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray()
            ?? Array.Empty<TypeNode>();
        if (methodArgs.Any(t => t == null)) return;
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
    /// Record the delegate a literal construction's slot declares, when that slot is a delegate at all.
    /// </summary>
    /// <remarks>
    /// Only the slot is recorded, never the outcome: the construction's own `funcType` can still be rewritten by
    /// a later pass, so what to do about the pair is decided when both halves are final.
    /// </remarks>
    internal static bool MarkDelegateSlot(JsonObject construction, TypeNode slotType)
    {
        if ((construction["k"] as JsonValue)?.GetValue<string>() is not ("newDelegate" or "newClosure")) return false;
        if (construction.ContainsKey(DelegateSlotKey)) return true;
        if (DelegateFqnOfSlot(slotType) is not TypeNode.Fqn slotDelegate) return false;
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
    static TypeNode.Fqn DelegateFqnOfSlot(TypeNode slotType)
    {
        var shape = slotType;
        while (shape is TypeNode.Nullable nullable) shape = nullable.Of;
        while (shape is TypeNode.Oblivious oblivious) shape = oblivious.Of;
        var physical = BirTypeLowering.LowerPhysicalType(shape, _refs.Aliases, _refs.IsValueTypeFqn,
            _refs.PhysicalTypeNames, typeArg: false, _localTypes);
        if (physical is TypeNode.Fn fn) physical = BirTypeLowering.DelegateFqnOf(fn);
        if (physical is not TypeNode.Fqn named) return null;
        var open = ResolveOwnerType(named);
        return open != null && IsDelegate(open) ? named : null;
    }

    // ---- materialization -----------------------------------------------------------------------

    static JsonObject _adapterHost;
    static string _adapterScope = "File";
    static int _nextAdapter;
    static readonly Dictionary<string, string> _adapters = new(StringComparer.Ordinal);

    /// <summary>
    /// Rewrite every marked delegate construction so it states the delegate it physically builds, authoring the
    /// void-to-value adapter classes that requires.
    /// </summary>
    /// <remarks>
    /// Runs once per file, after every resolution pass: the mark is placed where the callee is known, and the
    /// rewrite has to see the construction's final `funcType`.
    /// </remarks>
    public static void MaterializeDelegateSlots(JsonNode root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypes)
    {
        if (root is not JsonObject file) return;
        _refs = refs;
        _localTypes = localTypes ?? new HashSet<string>();
        _adapterHost = file;
        _adapterScope = string.Concat(((file["fileClass"] as JsonValue)?.GetValue<string>() ?? "File")
            .Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        _adapters.Clear();
        var pending = new List<JsonObject>();
        Collect(file, pending);
        foreach (var construction in pending) Materialize(construction);
        _adapterHost = null;
    }

    // CHILDREN FIRST. Adapting a construction moves it under a new node, and a JSON value has one parent, so the
    // move copies it; a nested construction still waiting to be rewritten would be rewritten in the detached copy
    // and lost. Rewriting bottom-up means the copy is already final.
    static void Collect(JsonNode node, List<JsonObject> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList()) if (kv.Value != null) Collect(kv.Value, into);
                if (obj.ContainsKey(DelegateSlotKey)) into.Add(obj);
                break;
            case JsonArray array:
                foreach (var item in array.ToList()) if (item != null) Collect(item, into);
                break;
        }
    }

    static void Materialize(JsonObject construction)
    {
        var slot = TypeJson.Read(construction[DelegateSlotKey]) as TypeNode.Fqn;
        construction.Remove(DelegateSlotKey);
        if (slot == null) return;
        var natural = TypeJson.Read(construction["funcType"]);
        if (natural is not TypeNode.Fn naturalFn) return;   // already retargeted to a named delegate
        var naturalDelegate = BirTypeLowering.DelegateFqnOf(naturalFn);
        if (naturalDelegate == null || naturalDelegate.Equals(slot)) return;
        if (naturalFn.Ret is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" }
            && SlotInvokeReturn(slot) is TypeNode slotReturn
            && slotReturn is not TypeNode.Fqn { Args: null, Name: "void" or "System.Void" })
            AdaptVoidConstruction(construction, naturalFn, slot, slotReturn);
        else
            Retarget(construction, slot);
    }

    // Point a construction at the delegate its slot declares. The two carriers ilemit consumes are the
    // constructor it runs and the `Invoke` its value is called through; both follow the type, so both are
    // re-resolved rather than patched.
    static void Retarget(JsonObject construction, TypeNode.Fqn slot)
    {
        construction["funcType"] = TypeJson.Write(slot);
        construction.Remove("delegateCtorRef");
        construction.Remove("invokeRef");
        ResolveDelegateCtor(construction, slot);
        ResolveDelegateInvoke(construction, slot);
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
                $"bir2cir: the delegate '{slot.Name}' has {invoke.Count} Invoke declarations, not one (#400)");
        return SubstOwnerParams(invoke[0].ReturnType, slot.Args ?? Array.Empty<TypeNode>());
    }

    const string UnitFqn = "kotlin.Unit";
    const string UnitInstance = "INSTANCE";

    // Replace the void-returning construction with a `newClosure` over an adapter that produces the Unit value the
    // slot's Invoke has to return. The original construction becomes the adapter's single capture, so its own
    // resolved constructor and Invoke keep describing the natural delegate.
    static void AdaptVoidConstruction(JsonObject construction, TypeNode.Fn naturalFn,
        TypeNode.Fqn slot, TypeNode slotReturn)
    {
        // The value the adapter returns is the `Unit` singleton, so the slot's Invoke must be able to receive it.
        // Kotlin resolution only fills such a slot from a `Unit` lambda, so anything else is a producer defect
        // rather than a program this rule has to accept.
        if (slotReturn is not TypeNode.Fqn { Args: null } returnName
            || (returnName.Name != UnitFqn && returnName.Name != "object" && returnName.Name != "System.Object"))
            throw new InvalidOperationException(
                $"bir2cir: a Unit lambda fills '{slot.Name}', whose Invoke returns "
                + $"{TypeNode.ToJson(slotReturn)} — no Unit value can be produced for that slot (#400)");

        var parameters = naturalFn.DelegateParams;
        var frame = new TypeNode.Fn(false, new TypeNode.Fqn("void"),
            Enumerable.Range(0, parameters.Length).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray(),
            null, naturalFn.Clr);
        var adapter = AdapterClass(frame, parameters.Length, slotReturn);

        var captured = new JsonObject();
        foreach (var kv in construction.ToList()) { captured[kv.Key] = kv.Value?.DeepClone(); construction.Remove(kv.Key); }

        // The adaptation happens AT the construction, so the diagnostic position stays on both halves.
        if (captured["pos"] is JsonNode position) construction["pos"] = position.DeepClone();
        construction["k"] = "newClosure";
        construction["closureType"] = TypeJson.Write(new TypeNode.Fqn(adapter));
        construction["method"] = "invoke";
        construction["captures"] = new JsonArray { captured };
        construction["funcType"] = TypeJson.Write(slot);
        if (parameters.Length > 0)
            construction["typeArgs"] = new JsonArray(parameters.Select(TypeJson.Write).ToArray());
        ResolveDelegateCtor(construction, slot);
        ResolveDelegateInvoke(construction, slot);
    }

    // One adapter class per (natural delegate family, arity, produced return) in a file — the shape depends on
    // nothing else, because its parameters ARE the delegate's.
    static string AdapterClass(TypeNode.Fn frame, int arity, TypeNode slotReturn)
    {
        var key = $"{frame.Clr}|{arity}|{TypeNode.ToJson(slotReturn)}";
        if (_adapters.TryGetValue(key, out var existing)) return existing;

        var name = $"dotkt${_adapterScope}$UnitDelegateAdapter{_nextAdapter++}";
        var frameJson = TypeJson.Write(frame);
        var self = new JsonObject { ["t"] = "fqn", ["name"] = name };

        var invokeBody = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "exprStmt",
                ["expr"] = DelegateInvokeCall(self, frameJson, arity),
            },
            new JsonObject
            {
                ["k"] = "return",
                ["value"] = UnitSingletonRead(),
            },
        };
        var invoke = new JsonObject
        {
            ["name"] = "invoke",
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = false,
            ["params"] = new JsonArray(Enumerable.Range(0, arity).Select(i => (JsonNode)new JsonObject
            {
                ["name"] = "p" + i,
                ["type"] = TypeJson.Write(new TypeNode.Tv("type", i)),
            }).ToArray()),
            ["ret"] = TypeJson.Write(slotReturn),
            ["body"] = invokeBody,
            // The body's last statement is the return; no fall-through terminator is needed or wanted.
            ["bodyTerminates"] = true,
        };

        var field = new JsonObject { ["name"] = "d", ["type"] = frameJson.DeepClone() };
        var ctor = new JsonObject
        {
            ["params"] = new JsonArray { field.DeepClone() },
            ["baseArgs"] = null,
            ["body"] = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "setField",
                    ["ownerType"] = self.DeepClone(),
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["name"] = "d",
                    ["value"] = new JsonObject { ["k"] = "local", ["name"] = "d" },
                },
            },
        };

        var declaration = new JsonObject
        {
            ["name"] = name,
            ["kind"] = "class",
            ["generated"] = true,
        };
        if (arity > 0)
            declaration["typeParams"] = new JsonArray(
                Enumerable.Range(0, arity).Select(i => (JsonNode)new JsonObject
                {
                    ["name"] = "T" + i,
                    // The adapter's parameter STANDS FOR the delegate's own parameter, so it must admit every
                    // type that one admits — and a delegate family's parameters admit a byref-like type
                    // (`Action<Span<int>>` is a legal .NET delegate). Without the anti-constraint the adapter
                    // rejects the instantiation the natural delegate already made. This is the only generic
                    // attribute the position needs: positive constraints belong to whatever type is substituted
                    // in, which the frame that supplies it has already satisfied.
                    ["specialConstraints"] = new JsonArray { "allowsRefStruct" },
                }).ToArray());
        declaration["base"] = null;
        declaration["interfaces"] = new JsonArray();
        declaration["fields"] = new JsonArray { field };
        declaration["ctors"] = new JsonArray { ctor };
        declaration["methods"] = new JsonArray { invoke };

        var types = _adapterHost["types"] as JsonArray;
        if (types == null) { types = new JsonArray(); _adapterHost["types"] = types; }
        types.Add(declaration);
        _adapters[key] = name;
        return name;
    }

    static JsonObject DelegateInvokeCall(JsonObject self, JsonNode frameJson, int arity)
    {
        var call = new JsonObject
        {
            ["k"] = "delegateInvoke",
            ["funcType"] = frameJson.DeepClone(),
            ["recv"] = new JsonObject
            {
                ["k"] = "field",
                ["ownerType"] = self.DeepClone(),
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["name"] = "d",
            },
            ["args"] = new JsonArray(Enumerable.Range(0, arity).Select(i => (JsonNode)new JsonObject
            {
                ["k"] = "local",
                ["name"] = "p" + i,
            }).ToArray()),
        };
        ResolveDelegateInvoke(call, "funcType");
        return call;
    }

    // The `Unit` singleton, read exactly as any Kotlin `object` instance is. A build that is EMITTING
    // `kotlin.Unit` has no reference to name and correctly carries none — the local axis.
    static JsonObject UnitSingletonRead()
    {
        var read = new JsonObject
        {
            ["k"] = "staticField",
            ["ownerType"] = new JsonObject { ["t"] = "fqn", ["name"] = UnitFqn },
            ["name"] = UnitInstance,
        };
        ResolveStaticField(read);
        return read;
    }
}
