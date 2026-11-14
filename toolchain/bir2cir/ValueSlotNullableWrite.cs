using System;
using DotKt.Bir;

// VALUE-TYPE PLATFORM SLOT ACCESS COERCION (#11/#501): a reference-KLIB-projected property/field
// may expose either a bare CLR value slot or a structural Nullable<V> slot. Kotlin platform flexibility and smart
// casts can feed the opposite representation into it. kotc is .NET-agnostic and emits the source representation; the
// slot vs source mismatch is a Kotlin<->CLR relation fact, so the coercion belongs HERE, not in kotc/ilemit:
//   - a `Nullable<V>` source (a genuine Kotlin `Int?`) into a bare value slot -> unwrap it via the existing
//     `nullableValue` node (ilemit emits `Nullable<V>.get_Value()` — which throws InvalidOperationException at runtime
//     if the source is dynamically `null`, the faithful "no value to store" outcome for a null-less value slot).
//   - a literal `null` source into a bare value slot -> a LOUD emit-time error. A CLR value type has no null
//     representation, so this is a user-code bug; a clear diagnostic beats a silent `default(V)` (0).
//   - a bare V source into a structural Nullable<V> slot -> construct Nullable<V> explicitly.
// On reads, stamp that same reflected physical slot onto `ret`/`sty`; a projected `T!` can physically be Nullable<T>
// when the CLR declaration is `T? where T : struct`, and a bare frontend stamp would otherwise corrupt the receiving
// local before any consumer can unwrap it. Reference slots are untouched (a `String!` platform reference has real
// null). Runs after native interop binding
// (so the `clrPropSet` nodes exist) and BEFORE
// BirTypeLowering (owner args + the `nullableValue` elem are still pure-Kotlin `kotlin.*`, lowered downstream exactly
// as PrimitiveOperatorLowering's `nullableValue` is). A lexical source walk ensures a
// `{local q}` source resolves its declared static type. Non-ref build only (clrPropSet is a NetInteropBinding product).
static partial class ValueSlotNullableWrite
{
    // Preserve complete owner arguments while peeling platform/nullability/reference wrappers.
    static TypeNode.Fqn UnwrapOwnerFqn(TypeNode t)
    {
        while (true)
            switch (t)
            {
                case TypeNode.Fqn f: return f;
                case TypeNode.Nullable nu: t = nu.Of; break;
                case TypeNode.Oblivious ob: t = ob.Of; break;
                case TypeNode.ByRef br: t = br.Of; break;
                default: return null;
            }
    }

    // The concrete structural value slot after owner substitution. Reflection supplies the declaration shape, while
    // BIR's owner args supply local/emitted types Reflection cannot load yet. For a concrete reflected Nullable<V>,
    // Kotlin has already type-checked the assignment; the source's exact value Fqn is therefore the authoritative
    // pre-lowering spelling of V (including nested/generic identity).
    static TypeNode ConcreteValueSlot(Type slotType, TypeNode[] ownerArgs, TypeNode src, ValueTypeOracle isValue)
    {
        if (slotType == null) return null;
        if (slotType.IsGenericParameter)
        {
            var pos = slotType.GenericParameterPosition;
            if (ownerArgs == null || pos < 0 || pos >= ownerArgs.Length) return null;
            return ownerArgs[pos] switch
            {
                TypeNode.Fqn direct when isValue(direct) => direct,
                TypeNode.Nullable { Of: TypeNode.Fqn nullableElem } nullable
                    when isValue(nullableElem) => nullable,
                _ => null,
            };
        }
        if (slotType.IsGenericType && slotType.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        {
            var reflectedElem = slotType.GetGenericArguments()[0];
            TypeNode elem = null;
            if (reflectedElem.IsGenericParameter)
            {
                var pos = reflectedElem.GenericParameterPosition;
                if (ownerArgs != null && pos >= 0 && pos < ownerArgs.Length)
                    elem = ownerArgs[pos] is TypeNode.Nullable n ? n.Of : ownerArgs[pos];
            }
            elem ??= UnwrapSurface(src);
            return elem is TypeNode.Fqn concreteElem && isValue(concreteElem)
                ? new TypeNode.Nullable(concreteElem) : null;
        }
        // For a bare reflected value slot, the source/surface supplies the exact pre-lowering V spelling. Platform
        // (`oblivious`) and nullable wrappers annotate the Kotlin view but do not change this physical bare slot.
        var bareSurface = UnwrapSurface(src);
        if (slotType.IsValueType && bareSurface is TypeNode.Fqn bareElem && isValue(bareElem)) return bareElem;
        return null;
    }

    static TypeNode UnwrapSurface(TypeNode surface)
    {
        while (true)
            switch (surface)
            {
                case TypeNode.Nullable nullable: surface = nullable.Of; break;
                case TypeNode.Oblivious oblivious: surface = oblivious.Of; break;
                default: return surface;
            }
    }

    // Whether the reflected member's instantiated CLR slot is a non-Nullable value type. Reflection exposes a
    // concrete/open struct directly through IsValueType; a type-generic slot needs owner-argument substitution.
    // Oblivious annotates a platform view only and does not add a physical Nullable<V> wrapper.
    static bool IsBareValueSlot(Type slotType, TypeNode[] ownerArgs, ValueTypeOracle isValue)
    {
        if (slotType == null) return false;
        if (slotType.IsGenericParameter)
        {
            var pos = slotType.GenericParameterPosition;
            if (ownerArgs == null || pos < 0 || pos >= ownerArgs.Length) return false;
            var arg = ownerArgs[pos];
            while (arg is TypeNode.Oblivious oblivious) arg = oblivious.Of;
            return arg is TypeNode.Fqn fqn && isValue(fqn);
        }
        if (slotType.IsGenericType && slotType.GetGenericTypeDefinition().FullName == "System.Nullable`1")
            return false;
        return slotType.IsValueType;
    }

}
