using System.Collections.Generic;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// ENUM inherited-member binding (C2 boxed-primitive dual-representation).
//
// A BASIC Kotlin `enum class` (constants only — no ctor params / user methods / per-entry bodies) is emitted as a real
// CLR value-type `enum` (`kind:"enum"`), which does NOT declare its own `ToString`/`GetHashCode`/`Equals` — it INHERITS
// them from `System.Enum`/`System.ValueType`/`System.Object`. Two receiver shapes reach a `System.Enum` member here and
// both would otherwise dead-end at a method the concrete enum type does not declare:
//
//  (1) CONCRETE receiver (`E.A.toString()`, `#90`): kotc emits `callInstance ownerType=E method=toString anySlot:true`
//      (the static receiver type is the concrete enum). ObjectSlotRename renames `toString`->`ToString` but keeps
//      owner `E` — so ilemit's `FindMethod("E","ToString")` fails ("method E.ToString not found"). Rebind to an
//      `objMethod` (box the value-type receiver, then the `System.Object` virtual slot) — `System.Enum` overrides
//      `ToString`/`Equals`/`GetHashCode`, so virtual dispatch yields the correct result. Concrete `.name`/`.ordinal`
//      reads arrive as semantic enum operations; bir2cir resolves those against declaration-owned storage.
//
//  (2) GENERIC receiver (`e: T`, `T : Enum<T>`): kotc names the semantic base `kotlin.Enum<T>`, which does not
//      name a concrete CLR receiver or a MethodDef. A semantic Any.toString call uses the CLR Object virtual
//      slot, regardless of whether its receiver is a native enum or a rich enum with an authored override.
//
// Non-ref builds only (the ref/rt stdlib keeps `kotlin.Enum`'s own pure-Kotlin member bodies untouched). Scoped to the
// `kotlin.Enum` owner and to locally-declared value-type enum owners, so no other call is affected.
static partial class EnumMemberBinding
{
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.EnumRuntimeKt";
    // Collect the local BASIC (value-type) enum type names — a `kind:"enum"` decl — across ALL input files. The set
    // MUST span the whole compilation, not one .bir.json: kotc emits one file per source, but a call site
    // (`E.A.toString()`) may live in a DIFFERENT file from the `enum class E` declaration, while ilemit's emitted-type
    // table is assembly-wide (a same-assembly cross-file `E.ToString` still dead-ends). A RICH enum is `kind:"class"` +
    // `enumRich:true` (a singleton-field reference class that DOES synthesize its own toString) — deliberately excluded.
    public static HashSet<string> CollectBasicEnums(IEnumerable<BirFileDeclaration> roots)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty().OfType<BirEnumDefinition>())
                if (type.Kind.OrDefault() == "enum" && type.Name.OrDefault() is string n)
                    set.Add(n);
        return set;
    }

}
