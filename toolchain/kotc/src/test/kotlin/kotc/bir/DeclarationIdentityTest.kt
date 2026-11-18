@file:OptIn(org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI::class)

package kotc.bir

import kotc.backend.BirEmitter
import kotc.backend.DefaultTypeFrame
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.ir.builders.declarations.addValueParameter
import org.jetbrains.kotlin.ir.builders.declarations.addGetter
import org.jetbrains.kotlin.ir.builders.declarations.addSetter
import org.jetbrains.kotlin.ir.builders.declarations.buildClass
import org.jetbrains.kotlin.ir.builders.declarations.buildConstructor
import org.jetbrains.kotlin.ir.builders.declarations.buildFun
import org.jetbrains.kotlin.ir.builders.declarations.buildProperty
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.IrDeclarationParent
import org.jetbrains.kotlin.ir.declarations.IrFunction
import org.jetbrains.kotlin.ir.declarations.IrTypeParameter
import org.jetbrains.kotlin.ir.declarations.impl.IrExternalPackageFragmentImpl
import org.jetbrains.kotlin.ir.declarations.impl.IrFactoryImpl
import org.jetbrains.kotlin.ir.expressions.impl.IrConstImpl
import org.jetbrains.kotlin.ir.expressions.impl.IrAnnotationImpl
import org.jetbrains.kotlin.ir.expressions.impl.fromSymbolOwner
import org.jetbrains.kotlin.ir.symbols.impl.IrExternalPackageFragmentSymbolImpl
import org.jetbrains.kotlin.ir.symbols.impl.IrTypeParameterSymbolImpl
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.defaultType
import org.jetbrains.kotlin.ir.types.typeWith
import org.jetbrains.kotlin.ir.util.createThisReceiverParameter
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.types.Variance
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertSame
import kotlin.test.assertNotEquals

class DeclarationIdentityTest {
    private val pkg = IrExternalPackageFragmentImpl(IrExternalPackageFragmentSymbolImpl(), FqName("identitytest"))

    private fun owner(name: String, parent: IrDeclarationParent = pkg, inner: Boolean = false): IrClass =
        IrFactoryImpl.buildClass {
            this.name = Name.identifier(name)
            kind = ClassKind.CLASS
            isInner = inner
        }.apply { this.parent = parent }

    private fun parameter(parent: IrDeclarationParent, name: String, index: Int): IrTypeParameter =
        IrFactoryImpl.createTypeParameter(0, 1, IrDeclarationOrigin.DEFINED,
            Name.identifier(name), IrTypeParameterSymbolImpl(), Variance.INVARIANT, index, false).apply {
            this.parent = parent
            superTypes = emptyList()
        }

    @Test
    fun carriedMethodAndPropertyAccessorIdentitiesRemainAuthoritative() {
        val container = owner("Container")
        val t = parameter(container, "T", 0)
        container.typeParameters = listOf(t)
        container.createThisReceiverParameter()
        val kotlinPkg = IrExternalPackageFragmentImpl(IrExternalPackageFragmentSymbolImpl(), FqName("kotlin"))
        val stringType = owner("String", kotlinPkg).symbol.typeWith()
        val unitType = owner("Unit", kotlinPkg).symbol.typeWith()
        fun carrier(name: String, first: String, second: String): IrAnnotationImpl {
            val annotationOwner = owner(name,
                IrExternalPackageFragmentImpl(IrExternalPackageFragmentSymbolImpl(), FqName("kotlin.clr")))
            annotationOwner.kind = ClassKind.ANNOTATION_CLASS
            val annotationType = annotationOwner.symbol.typeWith()
            val constructor = IrFactoryImpl.buildConstructor { returnType = annotationType }.apply {
                parent = annotationOwner
                addValueParameter("first", stringType)
                addValueParameter("second", stringType)
            }
            return IrAnnotationImpl.fromSymbolOwner(0, 1, annotationType, constructor.symbol).apply {
                arguments[0] = IrConstImpl.string(0, 1, stringType, first)
                arguments[1] = IrConstImpl.string(0, 1, stringType, second)
            }
        }
        fun identity(first: String, second: String) = carrier("KotlinDeclarationIdentity", first, second)
        val method = IrFactoryImpl.buildFun { name = Name.identifier("read"); returnType = t.defaultType }.apply {
            parent = container
            annotations = listOf(identity("carried-method", ""))
        }
        val property = IrFactoryImpl.buildProperty { name = Name.identifier("value"); isVar = true }.apply {
            parent = container
            annotations = listOf(identity("carried-getter", "carried-setter"))
        }
        val getter = property.addGetter { returnType = t.defaultType }
        val setter = property.addSetter { returnType = unitType }.apply { addValueParameter("value", t.defaultType) }
        for ((declaration, expected) in listOf(method to "carried-method", getter to "carried-getter", setter to "carried-setter")) {
            val emitter = BirEmitter()
            emitter.typeArgSubst[t] = TypeNode.Fqn("identitytest.CallerOnly")
            emitter.liftedTypeArgSubst["type" to 0] = TypeNode.Tv("method", 7)
            val defaults = DefaultTypeFrame(values = { stringType })
            emitter.defaultTypeFrame = defaults
            assertEquals(expected, emitter.declarationId(declaration))
            assertEquals(TypeNode.Fqn("identitytest.CallerOnly"), emitter.typeArgSubst[t])
            assertEquals(TypeNode.Tv("method", 7), emitter.liftedTypeArgSubst["type" to 0])
            assertSame(defaults, emitter.defaultTypeFrame)
            val source = BirEmitter(sourceFragments = setOf(pkg))
            val own = source.declarationId(declaration)
            assertNotEquals(expected, own)
            assertEquals(own, source.declarationIdForPhysicalAllocation(declaration))
        }
        val sourcePkg = IrExternalPackageFragmentImpl(IrExternalPackageFragmentSymbolImpl(), pkg.packageFqName)
        fun generatedOverride(name: String) = IrFactoryImpl.buildFun { this.name = method.name; returnType = stringType }.apply {
            parent = owner(name, sourcePkg).apply {
                superTypes = listOf(container.symbol.typeWith(stringType))
                createThisReceiverParameter()
            }
            overriddenSymbols = listOf(method.symbol)
            annotations = method.annotations
        }
        val source = BirEmitter(sourceFragments = setOf(sourcePkg))
        val copied = IrFactoryImpl.buildFun { name = Name.identifier("copied"); returnType = stringType }.apply {
            parent = sourcePkg
            annotations = listOf(identity("imported-top-level", ""), carrier("ClrExternal", "Host", "Host"))
        }
        assertEquals(source.declarationId(copied), source.declarationIdForPhysicalAllocation(copied))
        val first = generatedOverride("First")
        val second = generatedOverride("Second")
        assertEquals("carried-method", source.declarationIdForPhysicalAllocation(method))
        assertNotEquals(source.declarationId(first), source.declarationId(second))
        assertEquals(source.declarationId(first), source.declarationIdForPhysicalAllocation(first))
        method.isFakeOverride = true
        assertEquals("carried-method", BirEmitter(sourceFragments = setOf(pkg)).declarationId(method))
        val materialized = IrFactoryImpl.buildFun {
            name = method.name; returnType = stringType; origin = IrDeclarationOrigin.FAKE_OVERRIDE
        }.apply { parent = first.parent; annotations = method.annotations; isFakeOverride = false }
        assertEquals("carried-method", source.declarationId(materialized))
        method.modality = Modality.ABSTRACT
        val ownObligation = BirEmitter().declarationId(method)
        assertNotEquals("carried-method", ownObligation)
        val caller = BirEmitter().apply { typeArgSubst[t] = TypeNode.Fqn("identitytest.CallerOnly") }
        assertEquals(ownObligation, caller.declarationIdForPhysicalAllocation(method))
    }

    @Test
    fun ownerAndOrderedSignatureStillDistinguishDeclarations() {
        val first = owner("First")
        val second = owner("Second")
        fun declaration(parent: IrClass, reverse: Boolean) =
            IrFactoryImpl.buildFun { name = Name.identifier("choose"); returnType = first.symbol.typeWith() }.apply {
                this.parent = parent
                val types = listOf(first.symbol.typeWith(), second.symbol.typeWith())
                (if (reverse) types.reversed() else types).forEachIndexed { index, type ->
                    addValueParameter("p$index", type)
                }
            }
        val base = BirEmitter().declarationId(declaration(first, false))
        assertEquals(base, BirEmitter().declarationId(declaration(first, false)))
        assertNotEquals(base, BirEmitter().declarationId(declaration(first, true)))
        assertNotEquals(base, BirEmitter().declarationId(declaration(second, false)))
    }

    @Test
    fun selectedDeclarationsIgnoreAmbientGenericFramesAndRestoreCallerState() {
        val outer = owner("Outer")
        val t = parameter(outer, "T", 0)
        outer.typeParameters = listOf(t)
        val inner = owner("Inner", outer, inner = true)
        val u = parameter(inner, "U", 0)
        inner.typeParameters = listOf(u)
        val method = IrFactoryImpl.buildFun { name = Name.identifier("read"); returnType = t.defaultType }
            .apply { parent = inner }
        val v = parameter(method, "V", 0).apply { superTypes = listOf(t.defaultType, u.defaultType) }
        method.typeParameters = listOf(v)
        method.returnType = v.defaultType
        method.addValueParameter("outer", t.defaultType)
        method.addValueParameter("inner", u.defaultType)
        val constructor = IrFactoryImpl.buildConstructor { returnType = inner.symbol.typeWith(u.defaultType, t.defaultType) }
            .apply { parent = inner; addValueParameter("value", u.defaultType) }
        val property = IrFactoryImpl.buildProperty { name = Name.identifier("value"); isVar = true }
            .apply { parent = inner }
        val getter = property.addGetter { returnType = u.defaultType }
        val unit = owner("Unit", IrExternalPackageFragmentImpl(IrExternalPackageFragmentSymbolImpl(), FqName("kotlin")))
            .symbol.typeWith()
        val setter = property.addSetter { returnType = unit }.apply { addValueParameter("value", u.defaultType) }
        val local = IrFactoryImpl.buildFun { name = Name.identifier("local"); returnType = v.defaultType }
            .apply { parent = method; addValueParameter("value", v.defaultType) }
        val localOwner = owner("Local", method)
        val localMember = IrFactoryImpl.buildFun { name = Name.identifier("read"); returnType = v.defaultType }
            .apply { parent = localOwner; addValueParameter("value", v.defaultType) }
        fun emitter() = BirEmitter().apply { liftedTypeArgParams[localOwner] = listOf(v) }
        val replacement = owner("CallerOnly").symbol.typeWith()
        val parameters = listOf(t, u, v)
        val declarations = listOf<IrFunction>(method, constructor, getter, setter, local, localMember)
        for (declaration in declarations) {
            val expected = emitter().declarationId(declaration)
            for (mode in 0..3) {
                val emitter = emitter()
                if (mode == 0 || mode == 3)
                    parameters.forEach { emitter.typeArgSubst[it] = TypeNode.Fqn("identitytest.CallerOnly") }
                if (mode == 1 || mode == 3) {
                    emitter.liftedTypeArgSubst["type" to 0] = TypeNode.Fqn("identitytest.CallerOnly")
                    emitter.liftedTypeArgSubst["method" to 0] = TypeNode.Tv("method", 7)
                }
                if (mode == 2 || mode == 3) {
                    emitter.defaultTypeFrame = DefaultTypeFrame(
                        values = { if (it.classifierOrNull in parameters.map { p -> p.symbol }) replacement else it },
                        arguments = { replacement }, stars = setOf(v.symbol),
                    )
                    emitter.defaultTypeArgumentDepth = 1
                }
                val substitutions = emitter.typeArgSubst.toMap()
                val lifts = emitter.liftedTypeArgSubst.toMap()
                val defaults = emitter.defaultTypeFrame
                val depth = emitter.defaultTypeArgumentDepth
                assertEquals(expected, emitter.declarationId(declaration), "${declaration.name}, mode=$mode")
                assertEquals(substitutions, emitter.typeArgSubst)
                assertEquals(lifts, emitter.liftedTypeArgSubst)
                assertSame(defaults, emitter.defaultTypeFrame)
                assertEquals(depth, emitter.defaultTypeArgumentDepth)
                emitter.typeArgSubst.clear()
                emitter.liftedTypeArgSubst.clear()
                emitter.defaultTypeFrame = DefaultTypeFrame()
                emitter.defaultTypeArgumentDepth = 0
                assertEquals(expected, emitter.declarationId(declaration), "first-visit cache")
            }
        }
    }
}
