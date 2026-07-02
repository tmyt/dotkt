package kotc.frontend;

import org.jetbrains.kotlin.fir.FirGeneratedDeclarationsUtilsKt;
import org.jetbrains.kotlin.fir.declarations.FirClassLikeDeclaration;
import org.jetbrains.kotlin.fir.extensions.FirDeclarationGenerationExtension;

/**
 * Access to the FIR-internal {@code ownerGenerator} attribute ({@code org.jetbrains.kotlin.fir.FirGeneratedDeclarationsUtils}
 * — Kotlin-{@code internal} with a {@code private} data key, but the compiled accessor is {@code public static} bytecode,
 * so plain Java reaches it compile-time-checked against the pinned 2.2.0 embeddable jar; no reflection).
 *
 * WHY THIS MUST EXIST (do not "clean it up"): for a class with GENERATED origin, ALL member generation is routed
 * through {@code fir.ownerGenerator} ({@code FirGeneratedScopes.kt:290}, {@code listOf(classSymbol.fir.ownerGenerator!!)}).
 * The framework sets that attribute only on symbols RETURNED from the generation hooks — and for a companion object,
 * only via {@code ClassifierStorage.generateNestedClassifier}'s fallback ({@code FirGeneratedScopes.kt:255}), which is
 * PREEMPTED by the early return at {@code FirGeneratedScopes.kt:245-248} once the owner's {@code companionObjectSymbol}
 * is linked. Implicit companion access ({@code App.Start(...)} without {@code .Companion}) requires that link to exist
 * BEFORE resolution ({@code ResolveUtils.kt:457}), so an eagerly-linked companion can never receive {@code ownerGenerator}
 * from the framework — it must be set here at creation time, or every member lookup on the companion NPEs.
 */
public final class FirInternals {
	private FirInternals() {}

	public static void setOwnerGenerator(FirClassLikeDeclaration declaration, FirDeclarationGenerationExtension extension) {
		FirGeneratedDeclarationsUtilsKt.setOwnerGenerator(declaration, extension);
	}
}
