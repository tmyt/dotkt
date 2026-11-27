package kotc.pipeline

import org.jetbrains.kotlin.diagnostics.DiagnosticReporter
import org.jetbrains.kotlin.diagnostics.reportOn
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.analysis.checkers.MppCheckerKind
import org.jetbrains.kotlin.fir.analysis.checkers.context.CheckerContext
import org.jetbrains.kotlin.fir.analysis.checkers.expression.ExpressionCheckers
import org.jetbrains.kotlin.fir.analysis.checkers.expression.FirExpressionChecker
import org.jetbrains.kotlin.fir.analysis.diagnostics.FirErrors
import org.jetbrains.kotlin.fir.analysis.extensions.FirAdditionalCheckersExtension
import org.jetbrains.kotlin.fir.declarations.hasAnnotation
import org.jetbrains.kotlin.fir.expressions.FirAnnotationCall
import org.jetbrains.kotlin.fir.expressions.FirNamedArgumentExpression
import org.jetbrains.kotlin.fir.expressions.impl.FirResolvedArgumentList
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrar
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName

internal class ClrAttributeArguments(session: FirSession) : FirAdditionalCheckersExtension(session) {
    object Registrar : FirExtensionRegistrar() {
        override fun ExtensionRegistrarContext.configurePlugin() {
            +FirAdditionalCheckersExtension.Factory(::ClrAttributeArguments)
        }
    }

    override val expressionCheckers = object : ExpressionCheckers() {
        override val annotationCallCheckers = setOf(NamedMemberChecker)
    }

    private object NamedMemberChecker : FirExpressionChecker<FirAnnotationCall>(MppCheckerKind.Common) {
        private val marker = ClassId.topLevel(FqName("kotlin.clr.ClrAttributeNamedArgument"))

        context(context: CheckerContext, reporter: DiagnosticReporter)
        override fun check(expression: FirAnnotationCall) {
            val args = expression.argumentList as? FirResolvedArgumentList ?: return
            // Resolution can replace source expressions (for example an array literal).
            // The member role belongs to the resolved parameter, the explicit name to the source.
            val suppliedNames = args.originalArgumentList?.arguments.orEmpty()
                .filterIsInstance<FirNamedArgumentExpression>().map { it.name }.toSet()
            for ((argument, parameter) in args.mapping) {
                if (parameter.name !in suppliedNames && parameter.hasAnnotation(marker, context.session)) {
                    reporter.reportOn(argument.source, FirErrors.UNSUPPORTED,
                        "CLR attribute member '${parameter.name}' requires a named argument ('${parameter.name} = ...')")
                }
            }
        }
    }
}
