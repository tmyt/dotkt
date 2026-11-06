package kotc.pipeline

import org.jetbrains.kotlin.config.AnalysisFlags
import org.jetbrains.kotlin.diagnostics.PendingDiagnosticReporter
import org.jetbrains.kotlin.diagnostics.impl.BaseDiagnosticsCollector
import org.jetbrains.kotlin.diagnostics.impl.PendingDiagnosticsReporterImpl
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.FirElement
import org.jetbrains.kotlin.fir.analysis.checkers.MppCheckerKind
import org.jetbrains.kotlin.fir.analysis.checkers.context.CheckerContext
import org.jetbrains.kotlin.fir.analysis.checkers.declaration.DeclarationCheckersDiagnosticComponent
import org.jetbrains.kotlin.fir.analysis.checkers.expression.ExpressionCheckersDiagnosticComponent
import org.jetbrains.kotlin.fir.analysis.checkers.type.TypeCheckersDiagnosticComponent
import org.jetbrains.kotlin.fir.analysis.collectors.CliDiagnosticsCollector
import org.jetbrains.kotlin.fir.analysis.collectors.DiagnosticCollectorComponents
import org.jetbrains.kotlin.fir.analysis.collectors.components.AbstractDiagnosticCollectorComponent
import org.jetbrains.kotlin.fir.analysis.collectors.components.ControlFlowAnalysisDiagnosticComponent
import org.jetbrains.kotlin.fir.analysis.collectors.components.ErrorNodeDiagnosticCollectorComponent
import org.jetbrains.kotlin.fir.analysis.collectors.components.LanguageVersionSettingsDiagnosticComponent
import org.jetbrains.kotlin.fir.analysis.collectors.components.ReportCommitterDiagnosticComponent
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.declarations.FirCallableDeclaration
import org.jetbrains.kotlin.fir.declarations.FirRegularClass
import org.jetbrains.kotlin.fir.declarations.FirResolvePhase
import org.jetbrains.kotlin.fir.exceptionHandler
import org.jetbrains.kotlin.fir.languageVersionSettings
import org.jetbrains.kotlin.fir.pipeline.SingleModuleFrontendOutput
import org.jetbrains.kotlin.fir.pipeline.runCheckers
import org.jetbrains.kotlin.fir.resolve.ScopeSession
import org.jetbrains.kotlin.fir.scopes.unsubstitutedScope
import org.jetbrains.kotlin.fir.types.FirTypeRef

/** Declaration inheritance and expression/type assignability are distinct facts for native arrays.
 * Check each element once per upstream component, with separate scope caches for those two views. */
internal fun FirSession.runClrArrayCheckers(scopes: ScopeSession, files: List<FirFile>,
    diagnostics: BaseDiagnosticsCollector, kind: MppCheckerKind) {
    val arrays = sourceArrayClasses()
    if (arrays.isEmpty()) {
        runCheckers(scopes, files, diagnostics, kind)
        return
    }
    val declarationScopes = ScopeSession()
    for (array in arrays) array.unsubstitutedScope(this, declarationScopes, false, FirResolvePhase.BODY_RESOLVE)
    val pending = PendingDiagnosticsReporterImpl(diagnostics)
    withSourceArrayBases(arrays) { original ->
        arrayDiagnosticCollector(declarationScopes, kind, arrayDeclarations = true, original).collectAll(files, pending)
        arrayDiagnosticCollector(ScopeSession(), kind, arrayDeclarations = false, original).collectAll(files, pending)
    }
}

/** Same component set as Kotlin 2.4.21's DiagnosticComponentsFactory, partitioned rather than filtered. */
private fun FirSession.arrayDiagnosticCollector(scopes: ScopeSession, kind: MppCheckerKind,
    arrayDeclarations: Boolean, original: Map<FirRegularClass, List<FirTypeRef>>): CliDiagnosticsCollector {
    val session = this
    val arrayIds = original.keys.mapTo(mutableSetOf()) { it.symbol.classId }
    return CliDiagnosticsCollector(session, scopes) { pending ->
        val components = buildList<AbstractDiagnosticCollectorComponent> {
            val declarations = DeclarationCheckersDiagnosticComponent(session, pending, kind)
            add(object : AbstractDiagnosticCollectorComponent(session, pending) {
                override fun visitElement(element: FirElement, context: CheckerContext) {
                    val owner = when (element) {
                        is FirRegularClass -> element.symbol.classId
                        is FirCallableDeclaration -> element.symbol.callableId?.takeUnless { it.isLocal }?.classId
                        else -> null
                    }
                    if ((owner in arrayIds) != arrayDeclarations) return
                    val bases = (element as? FirRegularClass)?.let(original::get)
                    if (bases != null) session.withArrayBaseOverrides(mapOf(element to bases)) {
                        element.accept(declarations, context)
                    }
                    else element.accept(declarations, context)
                }
            })
            if (!arrayDeclarations) {
                add(ExpressionCheckersDiagnosticComponent(session, pending, kind))
                add(TypeCheckersDiagnosticComponent(session, pending, kind))
                add(ControlFlowAnalysisDiagnosticComponent(session, pending, kind))
                if (kind == MppCheckerKind.Common) {
                    add(ErrorNodeDiagnosticCollectorComponent(session, pending))
                    add(LanguageVersionSettingsDiagnosticComponent(session, pending))
                }
            }
        }
        DiagnosticCollectorComponents(components.toTypedArray(), ReportCommitterDiagnosticComponent(session, pending))
    }
}

private fun CliDiagnosticsCollector.collectAll(files: List<FirFile>, pending: PendingDiagnosticReporter) {
    for (file in files) {
        try { collectDiagnostics(file, pending) }
        catch (failure: Throwable) { file.moduleData.session.exceptionHandler.handleExceptionOnFileAnalysis(file, failure) }
    }
    collectDiagnosticsInSettings(pending)
}

/** Preserve upstream's platform checker scope: the last actual session checks every fragment. */
internal fun List<SingleModuleFrontendOutput>.runClrPlatformCheckers(diagnostics: BaseDiagnosticsCollector) {
    val actual = last()
    if (actual.session.languageVersionSettings.getFlag(AnalysisFlags.headerMode)) return
    actual.session.runClrArrayCheckers(actual.scopeSession, flatMap { it.fir }, diagnostics, MppCheckerKind.Platform)
}
