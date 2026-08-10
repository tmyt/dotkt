import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import mpp.app.passGenericOwnerCompanion
import mpp.app.passNamedCompanion
import roundtrip.atomictwin.atomic
import roundtrip.classnature.Circle
import roundtrip.classnature.Handler
import roundtrip.classnature.Shape
import roundtrip.classnature.Square
import roundtrip.classnature.runHandler
import roundtrip.dispatchsurface.Animal
import roundtrip.dispatchsurface.Dog
import roundtrip.dispatchsurface.Greeter
import roundtrip.dispatchsurface.ConstrainedGenericOwnerCompanionHost
import roundtrip.dispatchsurface.CompanionMarker
import roundtrip.dispatchsurface.DefaultCompanionHost
import roundtrip.dispatchsurface.EnumCompanionHost
import roundtrip.dispatchsurface.GenericSecretHost
import roundtrip.dispatchsurface.InternalGenericCompanionHost
import roundtrip.dispatchsurface.LateinitGenericCompanionHost
import roundtrip.dispatchsurface.NamedCompanionHost
import roundtrip.dispatchsurface.NestedCompanionOwners
import roundtrip.dispatchsurface.NestedGenericCompanionOwners
import roundtrip.dispatchsurface.PrivateCompanionHost
import roundtrip.dispatchsurface.PrivateGenericCompanionHost
import roundtrip.dispatchsurface.ProtectedCompanionHost
import roundtrip.dispatchsurface.ProviderDelegateCompanionHost
import roundtrip.dispatchsurface.roundtripDelegatedCounter
import roundtrip.dispatchsurface.roundtripNullableDelegated
import roundtrip.dispatchsurface.ProtectedGenericCompanionHost
import roundtrip.dispatchsurface.StarProjectedCompanionHost
import roundtrip.dispatchsurface.useStarProjectedCompanionHost
import roundtrip.dispatchsurface.localDefaultCompanionUse
import roundtrip.dispatchsurface.markerValue
import roundtrip.dispatchsurface.passGenericCompanion
import roundtrip.higherorder.Box
import roundtrip.higherorder.Router
import roundtrip.higherorder.alsoMap
import roundtrip.higherorder.applyBox
import roundtrip.higherorder.mapBox
import roundtrip.higherorder.pipe
import roundtrip.higherorder.times
import roundtrip.memberextensionsurface.ExtensionLibrary
import roundtrip.memberextensionsurface.InheritedPropertyLeaf
import roundtrip.memberextensionsurface.InheritedPropertyMiddle
import roundtrip.memberextensionsurface.RemappedMutableProperty
import roundtrip.memberextensionsurface.CovariantPropertyImplementation
import roundtrip.memberextensionsurface.CovariantPropertySlot
import roundtrip.memberextensionsurface.CovariantPropertyValue
import roundtrip.memberextensionsurface.CovariantExtensionPropertyImplementation
import roundtrip.memberextensionsurface.CovariantExtensionPropertySlot
import roundtrip.memberextensionsurface.NumberContext
import roundtrip.memberextensionsurface.PartialAccessorHolder
import roundtrip.memberextensionsurface.TextContext
import roundtrip.memberextensionsurface.ValueBox
import roundtrip.memberextensionsurface.topLevelComputed
import roundtrip.memberextensionsurface.topLevelCustomGetter
import roundtrip.memberextensionsurface.topLevelCustomSetter
import roundtrip.propertytypes.PropertyHolder
import roundtrip.receiverfunctions.Panel
import roundtrip.receiverfunctions.PanelBuilder
import roundtrip.receiverfunctions.applyPanel
import roundtrip.receiverfunctions.column
import roundtrip.receiverfunctions.defaultPanel
import roundtrip.suspendvalues.BlockHolder
import roundtrip.suspendvalues.invokeWideSuspend23
import roundtrip.suspendvalues.makeBlock
import roundtrip.suspendvalues.storedBlock
import roundtrip.suspendnothing.fail as suspendFail

private class ProtectedGenericCompanionConsumer : ProtectedGenericCompanionHost<Int>() {
    fun revealProtectedGenericCompanion(): Int = marker()
}

private class ProtectedCompanionConsumer : ProtectedCompanionHost() {
    fun revealProtectedCompanion(): Int = marker() + token

    fun revealMethodReference(): Int {
        val reference: () -> Int = Shield::marker
        return reference()
    }

    fun revealPropertyReference(): Int {
        val reference = Shield::token
        return reference.get()
    }

    fun suspendReference(): suspend (Int) -> Int {
        val reference: suspend (Int) -> Int = Shield::suspendMarker
        return reference
    }
}

class RoundtripSurfaceTests {
    @TestAttribute
    fun referencedInheritedPropertyKeepsDeclarationOwnerAndVirtualDispatch() {
        val value: InheritedPropertyMiddle = InheritedPropertyLeaf()
        ClassicAssert.AreEqual(2, value.inheritedValue)

    }

    @TestAttribute
    fun referencedVarOverValSetterUsesTheGetterPropertyAllocation() {
        val value = RemappedMutableProperty()
        ClassicAssert.AreEqual(2, value.size)
        value.size = 9
        ClassicAssert.AreEqual(9, value.size)
    }

    @TestAttribute
    fun covariantPropertyBridgeKeepsPropertyIdentityAcrossModules() {
        val concrete = CovariantPropertyImplementation()
        ClassicAssert.AreEqual("narrow", concrete.covariantValue.text)
        val slot: CovariantPropertySlot<CovariantPropertyValue> = concrete
        ClassicAssert.AreEqual("narrow", slot.covariantValue.text)

        val extensionConcrete = CovariantExtensionPropertyImplementation()
        with(extensionConcrete) {
            ClassicAssert.AreEqual("extension-7", ValueBox(7).covariantExtensionValue.text)
        }
        val extensionSlot: CovariantExtensionPropertySlot<CovariantPropertyValue> = extensionConcrete
        with(extensionSlot) {
            ClassicAssert.AreEqual("extension-8", ValueBox(8).covariantExtensionValue.text)
        }
    }

    @TestAttribute
    fun topLevelCustomAccessorPropertyReferencesUseTheAccessorSurface() {
        val getter = ::topLevelCustomGetter
        getter.set(10)
        ClassicAssert.AreEqual(11, getter.get())

        val setter = ::topLevelCustomSetter
        setter.set(5)
        ClassicAssert.AreEqual(7, setter.get())
    }

    @TestAttribute
    fun higherOrderGenericShapesRoundTrip() {
        val convert: (Box<Int>) -> Box<String> = { Box(it.value.toString() + "!") }

        ClassicAssert.AreEqual("5!", applyBox(convert, Box(5)).value)
        ClassicAssert.AreEqual("6!", Router().route(convert, Box(6)).value)
        ClassicAssert.AreEqual("7!", Box(7).mapBox(convert).value)
        ClassicAssert.AreEqual("8!", (Box(8) pipe convert).value)
        ClassicAssert.AreEqual("9!", (Box(9) * convert).value)
        ClassicAssert.AreEqual(42, Box(1).alsoMap<Int, Int, String, Int>(convert, 42).value)
    }

    @TestAttribute
    fun receiverFunctionTypesRoundTripAtEveryPublicPosition() {
        val panel = applyPanel { margin = 4; padding = 1 }
        ClassicAssert.AreEqual(4, panel.margin)
        ClassicAssert.AreEqual(7, column({ margin = 7 }, {}))
        ClassicAssert.AreEqual(105, PanelBuilder(100).make { margin = 5 })

        val topLevel = Panel()
        defaultPanel.invoke(topLevel)
        ClassicAssert.AreEqual(9, topLevel.margin)

        val member = Panel()
        PanelBuilder(0).preset.invoke(member)
        ClassicAssert.AreEqual(8, member.margin)
    }

    @TestAttribute
    fun classNatureMetadataRoundTrips() {
        ClassicAssert.AreEqual(50, runHandler(Handler { value -> value * 10 }, 5))
        ClassicAssert.AreEqual("circle", classify(Circle(2)))
        ClassicAssert.AreEqual("square", classify(Square(3)))
    }

    private fun classify(shape: Shape): String = when (shape) {
        is Circle -> "circle"
        is Square -> "square"
    }

    @TestAttribute
    fun memberExtensionPropertiesAndSuspendFunctionsRoundTrip() {
        topLevelCustomGetter = 100
        ClassicAssert.AreEqual(101, topLevelCustomGetter)
        topLevelCustomSetter = 100
        ClassicAssert.AreEqual(102, topLevelCustomSetter)
        ClassicAssert.AreEqual(33, topLevelComputed)

        val partial = PartialAccessorHolder()
        partial.customGetter = 100
        ClassicAssert.AreEqual(103, partial.customGetter)
        partial.customSetter = 100
        ClassicAssert.AreEqual(104, partial.customSetter)
        ClassicAssert.AreEqual(55, partial.computed)

        val library = ExtensionLibrary(10)
        with(library) {
            ClassicAssert.AreEqual("value=17", ValueBox(7).label)
            ClassicAssert.AreEqual("operator=17", this[ValueBox(7)])
            ClassicAssert.AreEqual(30, ValueBox(3).scaled)
            ValueBox(0).scaled = 5
            ClassicAssert.AreEqual(15, last)
            with(TextContext("!")) {
                ClassicAssert.AreEqual("text=17!", ValueBox(7).contextual)
            }
            with(NumberContext(3)) {
                ClassicAssert.AreEqual("number=20", ValueBox(7).contextual)
            }
            ClassicAssert.AreEqual(15, runCrossModuleSuspend { ValueBox(5).fetch() })
        }
        ClassicAssert.AreEqual(210, runCrossModuleSuspend { library.useHidden(ValueBox(2)) })

    }

    @TestAttribute
    fun suspendFunctionValuesRoundTrip() {
        ClassicAssert.AreEqual(42, runCrossModuleSuspend(makeBlock()))
        ClassicAssert.AreEqual(30, runCrossModuleSuspend(storedBlock))
        ClassicAssert.AreEqual(107, runCrossModuleSuspend(BlockHolder().block))
        ClassicAssert.AreEqual(24, runCrossModuleSuspend {
            invokeWideSuspend23 { p1, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, p23 -> p1 + p23 }
        })
    }

    @TestAttribute
    fun consumedTypesMayReferenceKotlinStdlibTypes() {
        val number = atomic(0)
        ClassicAssert.AreEqual(1, number.incrementAndGet())
        number.value = 41
        ClassicAssert.AreEqual(42, number.value + 1)

        val reference = atomic<String?>(null)
        ClassicAssert.IsTrue(reference.compareAndSet(null, "ready"))
        ClassicAssert.AreEqual("ready", reference.value)
    }

    @TestAttribute
    fun suspendNothingReturnRoundTrips() {
        val value: Int = runCrossModuleSuspend { if (true) 7 else suspendFail() }
        ClassicAssert.AreEqual(7, value)
    }

    @TestAttribute
    fun propertyTypesRoundTripDirectly() {
        val holder = PropertyHolder()
        holder.text = null
        ClassicAssert.IsNull(holder.text)
        ClassicAssert.AreEqual(7, runCrossModuleSuspend(holder.block))

        val extension: suspend Int.() -> Int = holder.extension
        ClassicAssert.AreSame(extension, holder.extension)
    }

    @TestAttribute
    fun virtualDispatchAndInterfaceCompanionRoundTrip() {
        val animal: Animal = Animal("a")
        val dog: Animal = Dog("d")
        ClassicAssert.AreEqual("generic", animal.sound())
        ClassicAssert.AreEqual("woof", dog.sound())
        ClassicAssert.AreEqual("a:generic", animal.describe())
        ClassicAssert.AreEqual("d:woof", dog.describe())

        ClassicAssert.AreEqual("Anon", Greeter.Companion.DEFAULT)
        ClassicAssert.AreEqual("Hi, Vec", Greeter.Companion.create().greet("Vec"))
        ClassicAssert.AreEqual("Hi, Anon", Greeter.Companion.create().greet(Greeter.Companion.DEFAULT))
    }

    @TestAttribute
    fun companionIdentityAndSourceNamesRoundTrip() {
        ClassicAssert.AreEqual(42, NamedCompanionHost.Key.marker())
        ClassicAssert.AreEqual(6, NamedCompanionHost.Key.token)
        val namedRef: (String) -> String = NamedCompanionHost.Key::id
        ClassicAssert.AreEqual("named", namedRef("named"))
        val namedSuspendRef: suspend (Int) -> Int = NamedCompanionHost.Key::suspendMarker
        ClassicAssert.AreEqual(42, runCrossModuleSuspend { namedSuspendRef(1) })
        val named: CompanionMarker = NamedCompanionHost.Key
        ClassicAssert.AreSame(named, NamedCompanionHost.Key)
        ClassicAssert.AreEqual(42, markerValue(named))
        ClassicAssert.AreSame(named, passNamedCompanion(NamedCompanionHost.Key))

        val default = DefaultCompanionHost.Companion
        ClassicAssert.AreSame(default, DefaultCompanionHost.Companion)
        ClassicAssert.AreEqual(24, default.marker())
        ClassicAssert.AreEqual(38, localDefaultCompanionUse())

        ClassicAssert.AreEqual(73, EnumCompanionHost.Key.marker())
        ClassicAssert.AreSame(EnumCompanionHost.Key, EnumCompanionHost.Key)
        ClassicAssert.AreEqual(EnumCompanionHost.ENTRY, enumValues<EnumCompanionHost>().single())

        val generic = ConstrainedGenericOwnerCompanionHost.Companion
        ClassicAssert.AreSame(generic, ConstrainedGenericOwnerCompanionHost.Companion)
        ClassicAssert.AreEqual(91, generic.marker())
        ClassicAssert.AreEqual(90,
            generic.peek(ConstrainedGenericOwnerCompanionHost<NamedCompanionHost.Key>()))
        ClassicAssert.AreSame(generic, passGenericCompanion(generic))
        // Assembly B named the hoisted carrier as a TypeRef it never declared; the identity must survive that too.
        ClassicAssert.AreSame(generic, passGenericOwnerCompanion(generic))

        ClassicAssert.AreEqual(101, NestedCompanionOwners.NestedInterface.marker())
        ClassicAssert.AreEqual(102, NestedCompanionOwners.NestedEnum.marker())
        ClassicAssert.AreEqual(104, StarProjectedCompanionHost.dotkt_star.marker())
        ClassicAssert.AreEqual(7, useStarProjectedCompanionHost(StarProjectedCompanionHost(1)))
        ClassicAssert.AreEqual(103, NestedGenericCompanionOwners.Inner.Key.marker())
        ClassicAssert.AreSame(NestedGenericCompanionOwners.Inner.Key, NestedGenericCompanionOwners.Inner.Key)

        // A generic owner's companion keeps ONE state and its lexical access to the owner's private declarations,
        // across an assembly boundary and a hoisted physical carrier.
        val before = GenericSecretHost.opened
        ClassicAssert.AreEqual(12, GenericSecretHost.peek(GenericSecretHost.open(1)))
        ClassicAssert.AreEqual(before + 1, GenericSecretHost.opened)
        ClassicAssert.AreSame(GenericSecretHost.Companion, GenericSecretHost.Companion)
        ClassicAssert.AreEqual(13, runCrossModuleSuspend {
            GenericSecretHost.suspendPeek(GenericSecretHost.open(1))
        })
        ClassicAssert.AreEqual(before + 2, GenericSecretHost.opened)

        ClassicAssert.AreEqual(5, PrivateCompanionHost().reveal())
        ClassicAssert.AreEqual(4, PrivateGenericCompanionHost(1).reveal())
        ClassicAssert.AreEqual(9, InternalGenericCompanionHost(1).reveal())
        ClassicAssert.AreEqual("filled/derived:filled",
            LateinitGenericCompanionHost.fill(LateinitGenericCompanionHost()))
        val bumped = ProviderDelegateCompanionHost.bump()
        ClassicAssert.AreEqual(bumped + 1, ProviderDelegateCompanionHost.bump())
        // The provider field stays private in the producer's file facade; the exported top-level property survives
        // DLL -> KLIB as one accessor-routed declaration and is consumed here through get_/set_.
        ClassicAssert.AreEqual(bumped + 1, roundtripDelegatedCounter)
        roundtripDelegatedCounter = bumped + 2
        ClassicAssert.AreEqual(bumped + 2, roundtripDelegatedCounter)
        // The Property row must carry its nullable type metadata through DLL -> KLIB. Without root-level property
        // stamping this null assignment is rejected by the consuming frontend as a write to String.
        roundtripNullableDelegated = null
        ClassicAssert.IsNull(roundtripNullableDelegated)
        roundtripNullableDelegated = "restored"
        ClassicAssert.AreEqual("restored", roundtripNullableDelegated)
        val provider = ProviderDelegateCompanionHost<Int>()
        ClassicAssert.AreEqual(106, provider.selfProvided)
        ClassicAssert.AreEqual(107, ProviderDelegateCompanionHost.updatePrivateProvider(provider))
        ClassicAssert.AreEqual(107, provider.selfProvided)
        ClassicAssert.AreEqual(105, ProtectedGenericCompanionConsumer().revealProtectedGenericCompanion())
        val protected = ProtectedCompanionConsumer()
        ClassicAssert.AreEqual(21, protected.revealProtectedCompanion())
        ClassicAssert.AreEqual(10, protected.revealMethodReference())
        ClassicAssert.AreEqual(11, protected.revealPropertyReference())
        ClassicAssert.AreEqual(20, runCrossModuleSuspend { protected.suspendReference()(1) })
    }
}
