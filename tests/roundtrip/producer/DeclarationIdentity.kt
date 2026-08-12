@file:Suppress("NOTHING_TO_INLINE")

package roundtrip.identity

public val Map<Int, Int>.roundtripIdentityProperty: Int get() = 301
@get:kotlin.clr.ClrName("roundtripIdentityPropertyMutableMap")
public val MutableMap<Int, Int>.roundtripIdentityProperty: Int get() = 302
public val String.roundtripNullableProperty: Int get() = 303
@get:kotlin.clr.ClrName("roundtripNullablePropertyNullable")
public val String?.roundtripNullableProperty: Int get() = 304

private var roundtripMapState: Int = 0
private var roundtripMutableMapState: Int = 0
public var Map<Int, Int>.roundtripMutableIdentityProperty: Int
    get() = roundtripMapState
    set(value) { roundtripMapState = value + 600 }
@get:kotlin.clr.ClrName("roundtripMutableIdentityPropertyMutableMap")
@set:kotlin.clr.ClrName("roundtripMutableIdentityPropertyMutableMap")
public var MutableMap<Int, Int>.roundtripMutableIdentityProperty: Int
    get() = roundtripMutableMapState
    set(value) { roundtripMutableMapState = value + 700 }

public fun Map<Int, Int>.roundtripIdentityFunction(): Int = 305
@kotlin.clr.ClrName("roundtripIdentityFunctionMutableMap")
public fun MutableMap<Int, Int>.roundtripIdentityFunction(): Int = 306
public fun String.roundtripNullableFunction(): Int = 307
@kotlin.jvm.JvmName("roundtripNullableFunctionNullable")
public fun String?.roundtripNullableFunction(): Int = 308

public inline fun Map<Int, Int>.roundtripInlineIdentity(): Int = 309
@kotlin.clr.ClrName("roundtripInlineIdentityMutableMap")
public inline fun MutableMap<Int, Int>.roundtripInlineIdentity(): Int = 310

public fun Map<Int, Int>.roundtripDefaultIdentity(value: Int = 311): Int = value
@kotlin.clr.ClrName("roundtripDefaultIdentityMutableMap")
public fun MutableMap<Int, Int>.roundtripDefaultIdentity(value: Int = 312): Int = value
@get:kotlin.clr.ClrName("roundtripDefaultNamedPropertyPhysical")
public val roundtripDefaultNamedProperty: Int = 333

// The MutableMap overload lives in DeclarationIdentityCrossFile.kt. Their CLR MethodDefs have different file-facade
// owners, but dll2klib merges both declarations back into this package and must restore both semantic signatures.
public fun Map<Int, Int>.roundtripCrossFileIdentity(): Int = 325

public class RoundtripMemberIdentity {
    public fun Map<Int, Int>.erasedMember(): Int = 313
    @kotlin.clr.ClrName("erasedMemberMutableMap")
    public fun MutableMap<Int, Int>.erasedMember(): Int = 314

    public fun read(value: Map<Int, Int>): Int = value.erasedMember()
    public fun readMutable(value: MutableMap<Int, Int>): Int = value.erasedMember()
    public fun erasedCallable(value: Map<Int, Int>): Int = value.size + 315
    @kotlin.clr.ClrName("erasedCallableMutableMap")
    public fun erasedCallable(value: MutableMap<Int, Int>): Int = value.size + 316
}

public class RoundtripStaticMemberIdentity {
    companion {
        public fun erasedCallable(value: Map<Int, Int>): Int = value.size + 317
        @kotlin.clr.ClrName("erasedCallableMutableMap")
        public fun erasedCallable(value: MutableMap<Int, Int>): Int = value.size + 318
        public suspend fun erasedSuspendCallable(value: Map<Int, Int>): Int = value.size + 319
        @kotlin.clr.ClrName("erasedSuspendCallableMutableMap")
        public suspend fun erasedSuspendCallable(value: MutableMap<Int, Int>): Int = value.size + 320
    }
}

public class RoundtripRegularCompanionIdentity {
    companion object {
        public fun erasedCallable(value: Map<Int, Int>): Int = value.size + 326
        @kotlin.clr.ClrName("erasedCallableMutableMap")
        public fun erasedCallable(value: MutableMap<Int, Int>): Int = value.size + 327
    }
}

public class RoundtripNullableGenericIdentity<T> {
    @get:kotlin.clr.ClrName("selectedPropertyPhysical")
    public val selectedProperty: T? get() = null
    public fun selected(value: T?): Int = 329
    @kotlin.clr.ClrName("selectedAnyNullable")
    public fun selected(value: Any?): Int = 330
    public fun selectedMap(value: Map<Int, Int>): Int = 331
    @kotlin.clr.ClrName("selectedMapMutableMap")
    public fun selectedMap(value: MutableMap<Int, Int>): Int = 332
}

public class RoundtripCompanionExtensionIdentity

public companion fun RoundtripCompanionExtensionIdentity.erasedCompanionCallable(
    value: Map<Int, Int>,
): Int = value.size + 321

@kotlin.clr.ClrName("erasedCompanionCallableMutableMap")
public companion fun RoundtripCompanionExtensionIdentity.erasedCompanionCallable(
    value: MutableMap<Int, Int>,
): Int = value.size + 322

public companion suspend fun RoundtripCompanionExtensionIdentity.erasedSuspendCompanionCallable(
    value: Map<Int, Int>,
): Int = value.size + 323

@kotlin.clr.ClrName("erasedSuspendCompanionCallableMutableMap")
public companion suspend fun RoundtripCompanionExtensionIdentity.erasedSuspendCompanionCallable(
    value: MutableMap<Int, Int>,
): Int = value.size + 324
