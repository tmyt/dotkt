package kotc.pipeline

import org.jetbrains.kotlin.fir.declarations.builder.FirRegularClassBuilder
import org.jetbrains.kotlin.fir.types.ConeClassLikeType
import org.jetbrains.kotlin.fir.types.coneType
import org.jetbrains.kotlin.fir.types.constructClassLikeType
import org.jetbrains.kotlin.fir.types.builder.buildResolvedTypeRef
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.StandardClassIds

private val nativeArrayBase = ClassId.topLevel(FqName("System.Array"))

/** Frontend stdlib array classifiers expose one common base, including unsigned and open generic arrays.
 * Install the source assignability edge before FIR overload resolution; do not rewrite a selected
 * parameter, insert an argument conversion, or change any array storage/emission rule.
 * Physical array representation remains owned by bir2cir's BirTypeLowering. */
internal fun FirRegularClassBuilder.addClrArrayBase(classId: ClassId) {
    if (classId != StandardClassIds.Array && classId !in StandardClassIds.elementTypeByPrimitiveArrayType
        && classId !in StandardClassIds.elementTypeByUnsignedArrayType) return
    superTypeRefs.removeAll { (it.coneType as? ConeClassLikeType)?.lookupTag?.classId == StandardClassIds.Any }
    superTypeRefs += buildResolvedTypeRef {
        coneType = nativeArrayBase.constructClassLikeType(emptyArray(), false)
    }
}
