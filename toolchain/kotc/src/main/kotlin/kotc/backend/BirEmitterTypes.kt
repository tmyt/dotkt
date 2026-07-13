package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.backend.common.collectTailRecursionCalls
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrConstructor
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrVariable
import org.jetbrains.kotlin.ir.expressions.IrBlock
import org.jetbrains.kotlin.ir.expressions.IrBlockBody
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrConst
import org.jetbrains.kotlin.ir.expressions.IrConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrDelegatingConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrClassReference
import org.jetbrains.kotlin.ir.expressions.IrEnumConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrExpression
import org.jetbrains.kotlin.ir.expressions.IrExpressionBody
import org.jetbrains.kotlin.ir.declarations.IrEnumEntry
import org.jetbrains.kotlin.ir.expressions.IrGetEnumValue
import org.jetbrains.kotlin.ir.expressions.IrGetField
import org.jetbrains.kotlin.ir.expressions.IrGetObjectValue
import org.jetbrains.kotlin.ir.expressions.IrGetValue
import org.jetbrains.kotlin.ir.expressions.IrInstanceInitializerCall
import org.jetbrains.kotlin.ir.expressions.IrReturn
import org.jetbrains.kotlin.ir.expressions.IrSetField
import org.jetbrains.kotlin.ir.expressions.IrSetValue
import org.jetbrains.kotlin.ir.expressions.IrStringConcatenation
import org.jetbrains.kotlin.ir.expressions.IrThrow
import org.jetbrains.kotlin.ir.expressions.IrTry
import org.jetbrains.kotlin.ir.expressions.IrTypeOperatorCall
import org.jetbrains.kotlin.ir.expressions.IrTypeOperator
import org.jetbrains.kotlin.ir.expressions.IrWhen
import org.jetbrains.kotlin.ir.expressions.IrComposite
import org.jetbrains.kotlin.ir.expressions.IrDoWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrVararg
import org.jetbrains.kotlin.ir.expressions.IrSpreadElement
import org.jetbrains.kotlin.ir.expressions.IrFunctionExpression
import org.jetbrains.kotlin.ir.expressions.IrPropertyReference
import org.jetbrains.kotlin.ir.expressions.IrFunctionReference
import org.jetbrains.kotlin.ir.expressions.IrGetClass
import org.jetbrains.kotlin.ir.declarations.IrLocalDelegatedProperty
import org.jetbrains.kotlin.ir.declarations.IrValueDeclaration
import org.jetbrains.kotlin.ir.declarations.IrValueParameter
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.visitors.IrVisitorVoid
import org.jetbrains.kotlin.ir.visitors.acceptVoid
import org.jetbrains.kotlin.ir.visitors.acceptChildrenVoid
import org.jetbrains.kotlin.ir.types.IrSimpleType
import org.jetbrains.kotlin.ir.types.IrTypeProjection
import org.jetbrains.kotlin.ir.expressions.IrWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrBreak
import org.jetbrains.kotlin.ir.expressions.IrContinue
import org.jetbrains.kotlin.ir.expressions.IrStatementOrigin
import org.jetbrains.kotlin.ir.util.classId
import org.jetbrains.kotlin.name.CallableId
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.resolveFakeOverride
import org.jetbrains.kotlin.ir.declarations.IrTypeParameter
import org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.types.isBoxedArray
import org.jetbrains.kotlin.ir.types.isPrimitiveType
import org.jetbrains.kotlin.ir.types.isUnsignedType
import org.jetbrains.kotlin.ir.util.isPrimitiveArray
import org.jetbrains.kotlin.ir.util.isUnsignedArray
import org.jetbrains.kotlin.ir.util.defaultType
import org.jetbrains.kotlin.ir.types.makeNotNull
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.IrFileEntry
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageLocation
import java.io.File

internal fun BirEmitter.birType(t: IrType): TypeNode {
	// UNIFORM nullability: any `T?` -> `{t:nullable,of:<non-null core>}`, for VALUE, REFERENCE, and type-variable
	// types alike (spec §1). kotc stays CLR-free — it does NOT distinguish struct from ref; nullability rides the
	// type node only, and the decl-level `nullable`/`retNullable` flags are RETIRED. bir2cir DERIVES the CLR form
	// (value `Nullable<T>` vs reference NRT byte) from this node. Wrapping the non-null core makes every early-return
	// special case below (ClrRef/Span/array/fn/Comparator/charSeq/coroutine-fn/type-parameter) apply to the core.
	if (t.isMarkedNullable()) return TypeNode.Nullable(birType(t.makeNotNull()))
	// A type parameter `T` -> a positional `tv` (resolved in IL context). On the CLR generics are reified, so
	// even `reified T` rides on this (no inlining) — see [[clr-not-jvm-discard-jvmisms]].
	(t.classifierOrNull as? org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol)?.let { tp ->
		// While splicing an `inline fun`'s body, its OWN type params are substituted with the call's type arguments.
		typeArgSubst[tp.owner]?.let { return it }
		return tvOf(tp.owner)
	}
	// The intrinsic `kotlin.clr.ClrRef<T>` -> `byRef T` (a managed reference).
	if (t.classFqName?.asString() == "kotlin.clr.ClrRef")
		return TypeNode.ByRef(argType(t, 0) ?: OBJ)
	// The intrinsic `kotlin.clr.Span<T>` -> the FAITHFUL `kotlin.clr.Span<T>` identity. Substituting it to the
	// real `System.Span<T>` is a CLR-representation decision (the last naked `System.*` name in kotc), so bir2cir
	// owns it (LowerType), exactly like every other @ClrTypeAlias / primitive substitution.
	if (t.classFqName?.asString() == "kotlin.clr.Span")
		return TypeNode.Fqn("kotlin.clr.Span", listOf(argType(t, 0) ?: OBJ))
	// A reference array `kotlin.Array<E>` -> `TypeNode.Array(<E>)` (the element rides its own faithful identity).
	if (t.isBoxedArray) return TypeNode.Array(arrayElemType(t))
	// A SIGNED primitive array (`kotlin.IntArray`/…) OR an unsigned specialized array (`kotlin.UByteArray`/…, #76)
	// -> the FAITHFUL FQN identity (the type's OWN FQN, read from the IR — not a kotlin.* table); deciding
	// "IntArray/UByteArray IS an array of Int/UByte" is a REPRESENTATION decision that belongs in bir2cir (it
	// decomposes this token to `Array(elem)`). The array intrinsics (arrayGet/arraySet/forArray) + the sized ctor
	// are likewise bir2cir-derived off it. Unsigned mirrors signed exactly, in ALL builds (no build-mode gate).
	if (t.isPrimitiveArray() || t.isUnsignedArray()) return TypeNode.Fqn(t.classFqName!!.asString())
	val fqp = t.classFqName?.asString()
	// kotlin.text.Regex stays its bare `kotlin.*` FQN here (falls through to the user-class `@kotlin.text.Regex`
	// path below); bir2cir substitutes it to System.Text.RegularExpressions.Regex off the stdlib's @ClrTypeAlias
	// on the Regex class (metadata-driven — layer purity, no CLR name in kotc).
	// NOTE: kotlin.text.MatchResult is a REAL emitted Kotlin interface (runtime/stdlib/.../MatchResult.kt) with a real
	// CLR realization (ClrMatchResult over a System...Match); it must NOT be aliased to System...Match here — doing so
	// made `ClrMatchResult : MatchResult` try to implement a CLASS as an interface (TypeLoadException). A MatchResult
	// reference resolves as a referenced stdlib type (ilemit's MapType referenced-type fallback).
	// Kotlin throwables stay their bare `kotlin.*` FQN here (emitted as `@kotlin.IllegalArgumentException`, etc. via
	// the user-class fall-through below); bir2cir lowers them to the BCL base off the stdlib's @ClrTypeAlias on each
	// exception class (metadata-driven). A custom `class E : Exception(msg)`
	// supertype rides the same path; `.message`/`.cause` are plain property reads that bir2cir substitutes to
	// clrPropGet System.Exception.Message/.InnerException off the @ClrProperty binding (no kotc BCL-name knowledge).
	// kotlin.AutoCloseable (and its jar typealias kotlin.io.Closeable) stays its bare `kotlin.*` FQN here (falls
	// through to the user-class `@kotlin.AutoCloseable` path below); bir2cir substitutes it to System.IDisposable off
	// the stdlib's @ClrTypeAlias binding (layer purity — no CLR type name in kotc). The `close()->Dispose` member
	// rename + the `use{}` finally call are likewise metadata-driven (@ClrIntrinsic("Dispose")).
	// kotlin.CharSequence stays its plain `kotlin.CharSequence` FQN identity (the general interface branch below emits
	// the same bare Fqn — it is non-generic with no clrName); bir2cir SUBSTITUTES it to the synthesized
	// `dotkt_CharSequence` interface (no faithful .NET equivalent), exactly as it substitutes `kotlin.String`.
	// A function type as a value (e.g. a `(P)->R` parameter): `kotlin.FunctionN` -> `fn` (Func/Action shape). A
	// `kotlin.coroutines.SuspendFunctionN` (a `suspend (P)->R` value) sets `fn.suspend=true` — the SAME delegate
	// shape carrying the suspend FACT (which the newSuspendLambda SM builder needs). bir2cir ERASES a suspend `fn`
	// to `object` wherever it lands in a TYPE slot (only the `funcType` node key keeps it), so kotc bakes no
	// coroutine ABI here — behavior-preserving.
	if (fqp != null && (fqp.startsWith("kotlin.coroutines.SuspendFunction") || fqp.startsWith("kotlin.Function"))) {
		val args = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type }
		if (args.isNotEmpty()) {
			val ret = args.last(); val ps = args.dropLast(1)
			val suspend = fqp.startsWith("kotlin.coroutines.SuspendFunction")
			// A RECEIVER function type `P.() -> R` is `FunctionN<P,…,R>` carrying the `kotlin.ExtensionFunctionType`
			// annotation (a frontend fact). Kotlin flattens the receiver to the first delegate arg on the CLR, but that
			// erases the "this was a receiver" bit — so a re-consuming DotKt assembly loses the implicit `this: P` in a
			// `apply1 { … }` lambda (#145). Carry it in `fn.recv` (the FIRST type arg, dropped from params): the CLR
			// delegate is unchanged (DelegateParams re-prepends recv), and bir2cir stamps [KotlinExtensionFunctionType]
			// off recv so facadegen/ClrTypeInjection restore `P.() -> R`. Non-ext function type keeps the flat shape.
			// Guard to NON-suspend (#145 phase 1): a `suspend P.() -> R` stays flattened as today. bir2cir erases a
			// suspend fn to `object` and rides the pre-erasure shape on [KotlinSuspendFunctionType], whose facadegen
			// gate requires `recv == null` — recv-izing the suspend arm would degrade suspend restore, and a recv-bearing
			// suspend fn would perturb the SequenceScope hot path in SuspendColdLowering. Non-suspend only.
			val isExt = !suspend && (t as? IrSimpleType)?.annotations?.any { it.type.classFqName?.asString() == "kotlin.ExtensionFunctionType" } == true
			return if (isExt && ps.isNotEmpty())
				TypeNode.Fn(false, funcRetTypeOf(ret), ps.drop(1).map { birTypeDeleg(it) }, birTypeDeleg(ps.first()))
			else
				TypeNode.Fn(suspend, funcRetTypeOf(ret), ps.map { birTypeDeleg(it) })
		}
	}
	// `by lazy` delegate: kotlin.Lazy<T> is a REAL emitted stdlib interface (its impl `UnsafeLazyImpl` is pure
	// Kotlin, produced by the stdlib `lazy()` function) — kotc emits the plain Kotlin type identity and falls
	// through to the user-class/interface branch below (`@kotlin.Lazy[…]`). It is NOT aliased to System.Lazy:
	// that CLR type is SEALED, so a Kotlin class could not implement it, and the alias was pure CLR knowledge
	// that must not live in kotc (layer purity — cf. coerce/isBlank pure-body migration).
	// kotlin.reflect.KProperty0/KMutableProperty0/KProperty1/KMutableProperty1 (a `::prop` callable reference's
	// type, and the compiler-synthesized KProperty argument of a delegate's getValue/setValue) are REAL emitted
	// stdlib interfaces (KPropertyClr.kt) — falls through to the user-class/interface branch below
	// (`kotlin.reflect.KProperty0[…]` etc.), the SAME real-generic-stdlib-interface path as `kotlin.Lazy<T>` above.
	// kotc's `propertyRef`/`kPropertyStub` materialize REAL implementations of these interfaces (#70) — no more
	// synthetic `dotkt$KProperty` name-bag identity here.
	// kotlin.properties.Read(Write)Property<T,V> is NOT monomorphized: it falls through to the
	// user-class/interface branch below (`@kotlin.properties.ReadWriteProperty[…]`), the REAL generic
	// stdlib interface — same as `by lazy`'s `kotlin.Lazy<T>`. A delegate field/local typed as this
	// interface, the value from `Delegates.observable(…)`, and the getValue/setValue dispatch owner then
	// all agree on one type identity (ilverify-clean). The real generic interface is used (as generic
	// `kotlin.Lazy<T>` works with a value V).
	// Kotlin function type `(A,B)->R` (kotlin.FunctionN<A,B,R>) and a callable-reference type `KFunctionN<…>`
	// (the inferred type of `obj::method`/`::foo`) -> an `fn` (Func/Action delegate).
	val fqn = t.classFqName?.asString()
	if (fqn != null && (fqn.startsWith("kotlin.Function") || fqn.startsWith("kotlin.reflect.KFunction"))) {
		val tys = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type }
		if (tys.isNotEmpty()) {
			val retT = tys.last()
			val ret = if (retT.isUnit()) TypeNode.Fqn("kotlin.Unit") else argElemNullable(retT)
			return TypeNode.Fn(false, ret, tys.dropLast(1).map { birType(it) })
		}
	}
	// kotc emits the Kotlin FQN identity as-is for a SOURCE TYPE — it knows nothing about the CLR. bir2cir lowers
	// these (kotlin.Int -> System.Int32, kotlin.Unit -> System.Void, …). NO `int`/`void`/`System.Int32` here.
	when (val kfq = t.classFqName?.asString()) {
		"kotlin.Unit", "kotlin.Nothing", "kotlin.Any",
		"kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
		"kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char", "kotlin.String",
		"kotlin.UInt", "kotlin.ULong", "kotlin.UByte", "kotlin.UShort" -> return TypeNode.Fqn(kfq)
	}
	// A (Mutable)Iterator<E>/Iterable<E> — for ANY element E, type-parameter (`gp:E`) or concrete (`int`) alike —
	// maps to the REAL generic identity via the FQN path below: `kotlin.collections.Iterator[E]` (a real emitted
	// stdlib interface) / `Iterable[E]` (bir2cir @ClrTypeAlias'd to `System.Collections.Generic.IEnumerable<E>`).
	// kotc produces no per-element monomorphized synthetic: the reverse GetEnumerator bridge in ilemit + the real
	// generic Iterator interface handle the IEnumerable<->Iterator read-as (roadmap step 3).
	val klass = t.classifierOrNull?.owner as? IrClass
	// A @Clr / FIR-injected .NET type ("clr:System.Text.StringBuilder"); a constructed generic .NET type
	// (`Collection<Int>`) carries its concrete args as `clrg:<openName>[int]`.
	val clrTypeParams = klass?.typeParameters
	klass?.let { clrName(it) }?.let { netName ->
		// A .NET-injected / stdlib type identity: emit its Kotlin FQN (`netName`) as an `fqn`. bir2cir/ilemit
		// resolve whether it is a referenced .NET type / generic (the old `clr:`/`clrg:` decision). A nested
		// nullable type-parameter arg keeps its `nullable(tv)` marker (bir2cir erases it).
		val args = (t as? IrSimpleType)?.arguments?.mapNotNull { arg ->
			(arg as? IrTypeProjection)?.type?.let { argElemNullable(it) }
		}
		return when {
			!args.isNullOrEmpty() -> TypeNode.Fqn(netName, args)
			// A GENERIC type referenced raw / star-projected (no args) still needs its arity — fill `object` per
			// type param (the open generic def is unresolvable downstream).
			!clrTypeParams.isNullOrEmpty() -> TypeNode.Fqn(netName, clrTypeParams.map { OBJ })
			else -> TypeNode.Fqn(netName)
		}
	}
	// Enums -> the real .NET enum type reference (package-qualified, like other user types).
	if (klass != null && klass.kind == ClassKind.ENUM_CLASS) return TypeNode.Fqn(typeName(klass))
	// A user-declared class/interface becomes a reference to that BIR type; a constructed user generic carries
	// concrete args. Anon objects resolve through `typeName`.
	if (klass != null && (klass.kind == ClassKind.CLASS || klass.kind == ClassKind.INTERFACE)) {
		// An `inner class` re-declares its enclosing class(es)' type params; reference it WITH those (as `tv`).
		val enclArgs = innerEnclosingTypeParams(klass).map { tvOf(it) }
		if (klass.typeParameters.isNotEmpty()) {
			val sargs = (t as? IrSimpleType)?.arguments
			if (!sargs.isNullOrEmpty()) {
				val ownArgs = sargs.map { a ->
					val at = (a as? IrTypeProjection)?.type
					when {
						// A STAR projection (`Comparable<*>`) -> Any (dropping it leaves a raw generic — malformed).
						at == null -> OBJ
						// A `Unit` TYPE-ARG stays the real Unit identity (a generic arg of System.Void is invalid).
						at.isUnit() -> TypeNode.Fqn("kotlin.Unit")
						// A NULLABLE type-parameter arg keeps its `nullable(tv)` marker (bir2cir erases it).
						else -> argElemNullable(at)
					}
				}
				return TypeNode.Fqn(typeName(klass), enclArgs + ownArgs)
			}
		}
		if (enclArgs.isNotEmpty()) return TypeNode.Fqn(typeName(klass), enclArgs)
		return TypeNode.Fqn(typeName(klass))
	}
	return OBJ
}

/** birType of a type-argument at index [i], or null if absent/non-projection. */
private fun BirEmitter.argType(t: IrType, i: Int): TypeNode? =
	(t as? IrSimpleType)?.arguments?.getOrNull(i)?.let { (it as? IrTypeProjection)?.type?.let(::birType) }

/**
 * A type parameter -> a POSITIONAL, scope-tagged `tv` (spec §1). scope="method" (CLR `!!i`) when declared on
 * a function/constructor (i = its index in the method's own type params); scope="type" (CLR `!i`) when declared
 * on a class (i = the FLATTENED index over the enclosing-type nesting chain — enclosing params prepended, matching
 * the `enclArgs + ownArgs` construction order everywhere else). bir2cir/ilemit derive the CLR generic parameter.
 */
internal fun BirEmitter.tvOf(param: IrTypeParameter): TypeNode.Tv {
	val decl = param.parent
	return if (decl is IrClass) TypeNode.Tv("type", innerEnclosingTypeParams(decl).size + param.index)
	else TypeNode.Tv("method", param.index)
}

/** True if a structured type contains a type variable (`tv`) anywhere — replaces the `.contains("gp:")` scan.
 *  A non-generic synthetic (`dotkt_KProperty`) can't bake a `tv`, so this gates the fall-through. */
internal fun BirEmitter.hasTv(t: TypeNode): Boolean = when (t) {
	is TypeNode.Tv -> true
	is TypeNode.Fqn -> t.args?.any { hasTv(it) } == true
	is TypeNode.Fn -> hasTv(t.ret) || t.params.any { hasTv(it) } || (t.recv?.let { hasTv(it) } == true)
	is TypeNode.Nullable -> hasTv(t.of)
	is TypeNode.Oblivious -> hasTv(t.of)   // frontend-only, but keep the match exhaustive
	is TypeNode.Array -> hasTv(t.elem)
	is TypeNode.ByRef -> hasTv(t.of)
}

/** True if [t] is or contains a `tv` (an unresolved type variable). Used by the lifted-anon capture scan to
 *  decide whether an inline-substituted param resolves to an ENCLOSING generic param (must be captured) vs a
 *  concrete type (resolves fine). */
internal fun BirEmitter.containsTv(t: TypeNode): Boolean = when (t) {
	is TypeNode.Tv -> true
	is TypeNode.Fqn -> t.args?.any { containsTv(it) } == true
	is TypeNode.Fn -> containsTv(t.ret) || t.params.any { containsTv(it) } || (t.recv?.let { containsTv(it) } == true)
	is TypeNode.Nullable -> containsTv(t.of)
	is TypeNode.Oblivious -> containsTv(t.of)   // frontend-only, but keep the match exhaustive
	is TypeNode.Array -> containsTv(t.elem)
	is TypeNode.ByRef -> containsTv(t.of)
}

/** A collision-free identifier fragment derived from a structured type's canonical JSON (interim; the §2.4
 *  registry replaces this). Non-alnum chars collapse to `_`, so distinct `Type`s stay distinct (via toJson). */
internal fun BirEmitter.mangle(t: TypeNode): String = t.toJson().replace(Regex("[^A-Za-z0-9]"), "_")

// The erased / star-projection / Any? fallback type identity. kotc emits the pure Kotlin FQN `kotlin.Any`;
// bir2cir/ilemit resolve it to System.Object. (Replaces the old bare-string `object` shorthand.)
internal val BirEmitter.OBJ: TypeNode get() = TypeNode.Fqn("kotlin.Any")

/** Structured-Type JSON for a bare Kotlin/synthetic FQN identity — the ONLY way a type reaches the wire.
 *  Used to spell a KNOWN type-literal (a `kotlin.*` primitive, a `dotkt$*` synthetic) in a hand-built node
 *  template: `"type":${fqnJson("kotlin.Int")}` (never a bare string). */
internal fun BirEmitter.fqnJson(name: String): String = TypeNode.Fqn(name).toJson()

/** A type-argument's identity. `birType` now UNIFORMLY wraps any nullable core (incl. a nullable type-PARAMETER
 *  `Iterable<T?>` inner `T?`) as `{t:nullable,of:...}`, so this is just `birType`; kept as a named seam for the
 *  call sites that document the "preserve nullable type-arg" intent. bir2cir erases the marked arg. */
internal fun BirEmitter.argElemNullable(at: IrType): TypeNode = birType(at)

internal fun BirEmitter.constJson(c: IrConst): String = when (val v = c.value) {
	is String -> str(v)
	is Boolean -> v.toString()
	is Char -> str(v.toString())
	null -> "null"
	// NaN / ±Infinity are not valid JSON number tokens (`{"value":NaN}` breaks the parser) — emit them as a string
	// the ilemit const handler decodes to the special double/float (`Double.NaN` etc. appear in stdlib `average()`).
	is Double -> if (v.isNaN() || v.isInfinite()) str(v.toString()) else v.toString()
	is Float -> if (v.isNaN() || v.isInfinite()) str(v.toString()) else v.toString()
	else -> v.toString()
}

/** True if `t` is an array kotc emits array intrinsics (arrayGet/arraySet/arrayLen/forArray) for: a reference
 *  `Array<T>`, a signed primitive array, or an unsigned specialized array (`UByteArray`/…, #76 — native like
 *  signed, in ALL builds). Array-ness is read from the IR type system
 *  (`isBoxedArray`/`isPrimitiveArray`/`isUnsignedArray`), NOT a kotlin.* FQN table. */
internal fun BirEmitter.isArrayType(t: IrType): Boolean =
	t.isBoxedArray || t.isPrimitiveArray() || t.isUnsignedArray()

/** A value-type primitive OR an unsigned inline-class primitive (`kotlin.UInt`/…) — the set whose operators
 *  lower to raw CIL / whose receiver+args need value coercion. Read from the IR, not a FQN table. */
internal fun IrType.isPrimitiveOrUnsigned(): Boolean = makeNotNull().let { it.isPrimitiveType() || it.isUnsignedType() }

/** The element type of a REFERENCE `Array<T>`. NOT called for a signed OR unsigned specialized primitive array —
 *  bir2cir DERIVES that element off the faithful `kotlin.IntArray`/`kotlin.UByteArray`/… identity. */
internal fun BirEmitter.arrayElemType(t: IrType): TypeNode {
	val fq = t.classFqName?.asString()
	if (fq == "kotlin.Array")
		return (t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type?.let(::birType) } ?: OBJ
	return OBJ
}

/** Kotlin nullable VALUE type (`Int?`/`Double?`… AND the unsigned inline-classes `UInt?`/`UByte?`/…) -> the value
 *  element identity (`kotlin.Int`, `kotlin.UInt`…), else null. Unsigned is a value type on the CLR (`Nullable<uint>`),
 *  so a `UInt?` needs the SAME HasValue/Value unwrap as a signed `Int?` — a bare pass-through leaves a `Nullable<uint>`
 *  STRUCT where the use site wants the bare value (#118). bir2cir lowers the `kotlin.UInt` elem to `System.UInt32`
 *  exactly as it does `kotlin.Int` -> `System.Int32` (#76 native-unsigned). */
internal fun BirEmitter.nullableElem(t: IrType): TypeNode? =
	if (t.isMarkedNullable() && t.isPrimitiveOrUnsigned()) t.classFqName?.asString()?.let { TypeNode.Fqn(it) } else null

/** A value-type-nullable source (`Int?` = `Nullable<T>` on the CLR) narrowed/cast to its NON-null value
 *  (`Int`) must read `Nullable<T>.get_Value` — a bare load / `unbox.any` over a `Nullable<T>` STRUCT reads
 *  garbage or emits invalid IL (the C1 smart-cast miscompile). Given the SOURCE and required non-null USE/target
 *  type, returns the element to wrap in a `nullableValue` unwrap, else null. */
internal fun BirEmitter.nullableValueUnwrapElem(srcType: IrType, useType: IrType): TypeNode? {
	val elem = nullableElem(srcType) ?: return null          // source is Int?/Long?/Double?/UInt?…
	if (useType.isMarkedNullable()) return null              // target is still nullable -> no unwrap
	val tgt = useType.classFqName?.asString()?.takeIf { useType.isPrimitiveOrUnsigned() } ?: return null
	return if (elem is TypeNode.Fqn && tgt == elem.name) elem else null
}

/** Emit `node` coerced into a slot of the EXPECTED type: unwrap a value-type-nullable (`Int?`) to its
 *  non-null value (`Int`) when the slot demands the bare value — the CLR twin of the JVM backend's implicit
 *  `Integer.intValue()` coercion at an assignment / argument / return, which has NO IR cast node. */
internal fun BirEmitter.coerceValue(node: IrExpression, expected: IrType): String =
	if (isPreUnwrappedRead(node)) expr(node)
	else nullableValueUnwrapElem(node.type, expected)?.let { elem -> """{"k":"nullableValue","elem":${str(elem)},"e":${expr(node)}}""" } ?: expr(node)

/** True if reading `o` already yields the bare non-null VALUE of a value-type-nullable — an IrGetValue whose
 *  `valSubst` substitution was pre-unwrapped to `Nullable<T>.Value` (a `SAFE_CALL` receiver). The unwrap helpers
 *  must then NOT wrap again, or the `.Value` is read twice (`n?.plus(1)` -> 1 instead of 8). */
internal fun BirEmitter.isPreUnwrappedRead(o: IrExpression): Boolean =
	o is IrGetValue && o.symbol.owner.name.asString() in valSubstUnwrapped

/** Kotlin visibility -> BIR access keyword (public/private/internal/protected). A `@kotlin.PublishedApi internal`
 *  declaration emits as PUBLIC: it is part of the inline-published surface, so a cross-assembly spliced inline body
 *  (e.g. use{}'s `closeFinally`, `Uuid.toLongs`'s `get_mostSignificantBits`) must be able to bind it — CLR-internal
 *  would be a MethodAccessException at run. @PublishedApi's targets are CLASS/CONSTRUCTOR/FUNCTION/PROPERTY (never
 *  the accessor), so for a property accessor read the annotation off its corresponding property. */
internal fun BirEmitter.visOf(d: IrDeclarationWithVisibility): String = when (d.visibility.delegate) {
	Visibilities.Private, Visibilities.PrivateToThis -> "private"
	Visibilities.Internal -> {
		val annHost = (d as? IrSimpleFunction)?.correspondingPropertySymbol?.owner ?: d
		if (annHost.annotations.any { it.type.classFqName?.asString() == "kotlin.PublishedApi" }) "public" else "internal"
	}
	Visibilities.Protected -> "protected"
	else -> "public"
}

/**
 * Owner-type spec for a member access / `new`: `Box[int]` when the receiver is a CONCRETE construction of a
 * user generic, else the bare `Box`. Inside the generic type's own methods the receiver is `Box<T>` (args are
 * the type's own parameters) -> bare name, so members resolve against the open FieldBuilder/MethodBuilder
 * directly (the correct `!0`-typed reference), not a self-instantiation.
 */
internal fun BirEmitter.ownerSpec(klass: IrClass?, recvType: IrType?): TypeNode {
	klass ?: return TypeNode.Fqn("?")
	val name = typeName(klass)
	// An `inner class` re-declares its enclosing type params; construct it WITH them (as `tv`). See innerEnclosingTypeParams.
	val enclArgs = innerEnclosingTypeParams(klass).map { tvOf(it) }
	if (klass.typeParameters.isEmpty())
		return if (enclArgs.isNotEmpty()) TypeNode.Fqn(name, enclArgs) else TypeNode.Fqn(name)
	// A type-parameter argument keeps its `tv` form (resolvable in the enclosing generic context), NOT the open type.
	// A `Unit` TYPE-ARG stays the real Unit identity; a STAR projection -> Any (mirroring birType).
	val args = (recvType as? IrSimpleType)?.arguments?.map { a ->
		val at = (a as? IrTypeProjection)?.type
		when {
			at == null -> OBJ
			at.isUnit() -> TypeNode.Fqn("kotlin.Unit")
			else -> birType(at)
		}
	}
	val all = enclArgs + (args ?: emptyList())
	return if (all.isEmpty()) TypeNode.Fqn(name) else TypeNode.Fqn(name, all)
}

/**
 * The .NET TYPE name for an S5 FIR-injected .NET type (synthesized into FIR without annotations): read off the injected
 * symbol's RESOLVED IR identity — the type's `ClassId` (`kotc.frontend.clrInjectedDotNetName`), a structural projection
 * of facadegen's metadata (A2 interop-no-registry, stage 1 — no injector-populated name-keyed side-table). The backend
 * must resolve this so injected types are real .NET types (otherwise they leak in as user classes). MEMBER slot names
 * are NOT resolved here — that is bir2cir's DeclarationRename off the `overrides` marker + the refs (A2 step 5).
 */
internal fun BirEmitter.clrName(decl: org.jetbrains.kotlin.ir.declarations.IrAnnotationContainer): String? {
	// TYPE IDENTITY ONLY. kotc no longer resolves any MEMBER slot name here: a facadegen-injected .NET member's slot
	// AND a stdlib @ClrTypeAlias/@ClrIntrinsic member's slot are BOTH resolved by bir2cir's DeclarationRename off the
	// `overrides` marker + the refs (A2 / #61 step 5). This accessor now yields only the .NET TYPE name for an
	// injected .NET type (read off its IR `ClassId`) — used for type-origin decisions (routing a ctor to a plain
	// `new` / a field to a plain `field` on a .NET owner, the `byrefBackingField`/delegate/for-in origin tests),
	// never a member slot. bir2cir reshapes those plain nodes to their CLR forms off the refs.
	// A2 stage 1: the injected .NET type's .NET name is read straight off its IR `ClassId` (structural resolved
	// identity) against facadegen's metadata.
	return (decl as? IrClass)?.classId?.let { kotc.frontend.clrInjectedDotNetName(it) }
}

/** Boolean ORIGIN-GATE: is `decl` a facadegen-injected .NET/CLR type (vs a pure-Kotlin/stdlib type)? The truthiness
 *  half of [clrName] — call sites that only test "is this a .NET owner?" (routing a ctor to a plain `new`, a field to a
 *  plain `field`, excluding a .NET owner from a user-class path) use THIS; only sites that EMIT the .NET FQN identity
 *  keep [clrName] for its returned string. */
internal fun BirEmitter.isExternalNetType(decl: org.jetbrains.kotlin.ir.declarations.IrAnnotationContainer): Boolean =
	clrName(decl) != null

/** JSON for a structured type in a node template — `str(typeNode)` emits the `{t:…}` object (no quotes).
 *  An overload of `str(String)` so every `"type":${str(x)}` site works whether x is a name or a Type. */
internal fun BirEmitter.str(t: TypeNode): String = t.toJson()

internal fun BirEmitter.str(s: String): String {
	val escaped = buildString(s.length + 2) {
		for (ch in s) {
			when (ch) {
				'\\' -> append("\\\\")
				'"' -> append("\\\"")
				'\n' -> append("\\n")
				'\r' -> append("\\r")
				'\t' -> append("\\t")
				'\b' -> append("\\b")
				'\u000C' -> append("\\f")
				else -> {
					if (ch.code < 0x20) {
						append("\\u")
						append(ch.code.toString(16).padStart(4, '0'))
					} else {
						append(ch)
					}
				}
			}
		}
	}
	return "\"$escaped\""
}
