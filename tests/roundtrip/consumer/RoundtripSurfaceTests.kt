import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.atomictwin.atomic
import roundtrip.classnature.Circle
import roundtrip.classnature.Handler
import roundtrip.classnature.Shape
import roundtrip.classnature.Square
import roundtrip.classnature.runHandler
import roundtrip.dispatchsurface.Animal
import roundtrip.dispatchsurface.Dog
import roundtrip.dispatchsurface.Greeter
import roundtrip.higherorder.Box
import roundtrip.higherorder.Router
import roundtrip.higherorder.alsoMap
import roundtrip.higherorder.applyBox
import roundtrip.higherorder.mapBox
import roundtrip.higherorder.pipe
import roundtrip.higherorder.times
import roundtrip.memberextensionsurface.ExtensionLibrary
import roundtrip.memberextensionsurface.ValueBox
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

class RoundtripSurfaceTests {
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
        val library = ExtensionLibrary(10)
        with(library) {
            ClassicAssert.AreEqual("value=17", ValueBox(7).label)
            ClassicAssert.AreEqual(30, ValueBox(3).scaled)
            ValueBox(0).scaled = 5
            ClassicAssert.AreEqual(15, last)
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
}
