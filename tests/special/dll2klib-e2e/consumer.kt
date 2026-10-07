package consumer

import Probe.Widget
import Probe.WidgetExtensions
import Probe.IAdder
import Probe.Bump
import Probe.IVisibleControl
import Probe.VisibilityProbe
import Probe.DefaultCarrier1
import Probe.DefaultCarrier2
import Probe.IPublicDefaultSlot
import Probe.GenericDefaultCarrier
import Probe.ExternalDefaultCarrier
import Probe.ExplicitDefaultCarrier
import Probe.IPublicGenericDefaultSlot
import Probe.Contracts.IVisibleGeneric
import Probe.Contracts.IExternalDefaultSlot
import Probe.Contracts.CrossAssemblyArity1
import GlobalWidgetExtensions
import GlobalBump
import Probe.ConstraintBox
import Probe.ConstraintApi
import Probe.ConstraintKind
import Probe.ConstrainedValue
import Probe.EnumConstraintBox
import Probe.FreshConstraintBox
import Probe.GoodConstraintSink
import Probe.MemberConstraintApi
import Probe.MemberConstraintHost
import Probe.MemberDefaultValue
import Probe.ReferenceConstraintBox
import Probe.StructConstraintBox
import Probe.PointerProbe
import Probe.PointerBase
import Probe.IInheritedFrameSlot
import Probe.IInheritedReadSlot
import Probe.IInheritedMutableSlot
import kotlin.clr.byref
import kotlin.clr.ClrPointer

class LocalDefaultConstraintValue {
    val value: Int = 16
}

open class LocalReferenceConstraintBase

class LocalReferenceConstraintValue : LocalReferenceConstraintBase()

class NestedConstraintOuter<T : LocalReferenceConstraintBase>(val value: T) {
    inner class NestedConstraintInner<U>(val other: U) {
        fun read(): Int = ReferenceConstraintBox<T>().Value
    }
}

fun <T : LocalReferenceConstraintBase> readOpenMemberConstraint(value: T): Int =
    MemberConstraintApi.Reference(value)

fun readMemberConstraintDelegates(): Int {
    val staticCall: (Int) -> Int = MemberConstraintApi::Struct
    val boundCall: (Int) -> Int = MemberConstraintHost()::Struct
    return staticCall(1) + boundCall(1)
}

class DefaultCarrierSubclass1 : DefaultCarrier1()

class DefaultCarrierSubclass2 : DefaultCarrier2()

class GenericDefaultCarrierSubclass : GenericDefaultCarrier()

class ExternalDefaultCarrierSubclass : ExternalDefaultCarrier()

class ExplicitDefaultCarrierSubclass : ExplicitDefaultCarrier()

class PointerOverride : PointerBase() {
    override fun Echo(value: ClrPointer<Unit>): ClrPointer<Unit> = value
}

fun consumePointers(): Int {
    val pointerProbe = PointerProbe()
    val pointer = pointerProbe.Null()
    pointerProbe.Value = pointerProbe.Echo(pointer)
    val nullablePointer = pointerProbe.EchoNullable(pointerProbe.NullNullable())
    return (if (pointerProbe.IsNull(pointerProbe.Value)) 1 else 0) +
        (if (pointerProbe.IsNullVoid(pointerProbe.NullVoid())) 1 else 0) +
        (if (pointerProbe.IsNullNested(pointerProbe.NullNested())) 1 else 0) +
        (if (pointerProbe.IsNullNullable(nullablePointer)) 1 else 0) +
        (if (pointerProbe.IsNullStruct(pointerProbe.NullStruct())) 1 else 0) +
        (if (pointerProbe.OverrideWorks(PointerOverride())) 1 else 0)
}

fun consume(): Int {
    val maybe: String? = "x"
    val widget = Widget(3)
    val definitely: String = widget.Echo("x")
    widget.Value = 5
    widget.Inherited = 11
    widget.Field = 7
    Widget.Global = 8
    widget[2] = 6
    val nested = Widget.Nested()
    val transformed = widget.Apply({ it + 2 }, 4)
    val externalTransformed = widget.ApplyExternal({ it * 3 }, 4)
    val externalGenericTransformed = widget.ApplyExternalGeneric({ it + 5 }, 4)
    val externalArity = widget.ApplyExternalArity({ CrossAssemblyArity1(it + 6) }, 4).Value
    val nullable: String? = widget.MaybeNull(true)
    val required: String = widget.Required()
    var changed = 0
    val subscription = widget.Changed.subscribe { changed = it; it }
    widget.Raise(5)
    subscription.close()
    subscription.close()
    widget.Raise(99)
    val adder: IAdder = widget
    var incremented = 10
    widget.Increment(byref(incremented))
    val shifted = widget + 4
    val staticBump = WidgetExtensions.Bump(widget, 1)
    val globalExtensionBump = widget.GlobalBump(1)
    val globalStaticBump = GlobalWidgetExtensions.GlobalBump(widget, 1)
    val visibility = VisibilityProbe()
    val visibleControl: IVisibleControl = visibility
    val visibleGeneric: IVisibleGeneric<String> = visibility
    val defaultCarrier1: IPublicDefaultSlot = DefaultCarrierSubclass1()
    val defaultCarrier2: IPublicDefaultSlot = DefaultCarrierSubclass2()
    defaultCarrier1.M()
    defaultCarrier2.M()
    val genericDefaultCarrier: IPublicGenericDefaultSlot<String> = GenericDefaultCarrierSubclass()
    genericDefaultCarrier.Echo("ok")
    val externalDefaultCarrier: IExternalDefaultSlot = ExternalDefaultCarrierSubclass()
    externalDefaultCarrier.Value()
    val explicitDefaultCarrier: IExternalDefaultSlot = ExplicitDefaultCarrierSubclass()
    explicitDefaultCarrier.Value()
    val pointerResult = consumePointers()
    val genericConstraints = ConstraintBox<GoodConstraintSink>().Value +
        StructConstraintBox<Int>().Value +
        EnumConstraintBox<ConstraintKind>().Value +
        ReferenceConstraintBox<String>().Value +
        FreshConstraintBox<LocalDefaultConstraintValue>().Create().value +
        ConstraintApi.Read(ReferenceConstraintBox<String>()) +
        NestedConstraintOuter(LocalReferenceConstraintValue()).NestedConstraintInner(1).read() +
        MemberConstraintApi.Struct(1) +
        MemberConstraintApi.Enum(ConstraintKind.First) +
        MemberConstraintApi.Reference("member") +
        MemberConstraintApi.Fresh<MemberDefaultValue>().Value +
        MemberConstraintApi.NominalStruct(MemberConstraintApi.MemberValue()) +
        MemberConstraintApi.Unmanaged(1) +
        widget.ConstrainedValue(1) +
        readOpenMemberConstraint(LocalReferenceConstraintValue()) +
        readMemberConstraintDelegates()
    return widget.Add(4) + Widget.Twice(5) + definitely.length +
        widget.Value + widget.Inherited + widget.Field + Widget.Global + adder.Add(1) + widget.Identity(2) +
        widget[2] + nested.Triple(2) + transformed + widget.Bump(1) +
        externalTransformed + externalGenericTransformed + externalArity + staticBump + globalExtensionBump + globalStaticBump +
        (nullable?.length ?: 0) + required.length + changed + incremented + shifted.Add(0) +
        visibility.Read() + visibleControl.Read() + (if (visibleGeneric === visibility) 1 else 0) +
        genericConstraints + pointerResult
}

interface LocalFrameSlot<A, B> : IInheritedFrameSlot<B>
interface TwiceLocalFrameSlot<A, B> : LocalFrameSlot<B, A>
class IntFrameSlot : TwiceLocalFrameSlot<Int, String>
class StringFrameSlot : TwiceLocalFrameSlot<String, Int>

fun <A, B> inheritedOwnerCall(slot: LocalFrameSlot<A, B>, value: B): B = slot.Echo(value, "marker")
fun <A, B> inheritedIntCall(slot: LocalFrameSlot<A, B>, value: Int): String = slot.Echo(value, "marker")
fun <A, B> inheritedOwnerReference(slot: LocalFrameSlot<A, B>): (B, String) -> B = slot::Echo
fun <A, B> inheritedIntReference(slot: LocalFrameSlot<A, B>): (Int, String) -> String = slot::Echo

interface RedeclaredReadSlot : IInheritedReadSlot {
    override fun <T> Read(value: T): Int
}
interface InheritsReadDeclaration : RedeclaredReadSlot
class LocalReadSlot : InheritsReadDeclaration {
    override fun <T> Read(value: T): Int = 31
}
fun readLocalDeclaration(slot: InheritsReadDeclaration): Int = slot.Read(7)

interface DirectMutableSlot : IInheritedMutableSlot
class LocalMutableSlot : DirectMutableSlot {
    override var Number: Int = 0
    override fun <T> Read(value: T): Int = 31
    override fun <T> Accept(value: T) { Number = 71 }
}
fun readDirectMutableSlot(slot: DirectMutableSlot): Int = slot.Read("text")
fun directMutableReference(slot: DirectMutableSlot): (String) -> Int = slot::Read

fun checkInheritedNativeSlots() {
    val slot: LocalFrameSlot<String, Int> = IntFrameSlot()
    check(inheritedOwnerCall(slot, 37) == 37)
    check(inheritedIntCall(slot, 37) == "int-overload")
    check(inheritedOwnerReference(slot)(39, "marker") == 39)
    check(inheritedIntReference(slot)(39, "marker") == "int-overload")
    check(slot.Count == 43)
    val concrete = StringFrameSlot()
    check(concrete.Echo(38, "marker") == "int-overload")
    check(concrete.Echo("owner", "marker") == "owner")
    val values = intArrayOf(7, 9)
    check(slot.Array(values) === values)
    check(readLocalDeclaration(LocalReadSlot()) == 31)
    val direct: DirectMutableSlot = LocalMutableSlot()
    check(readDirectMutableSlot(direct) == 31)
    check(directMutableReference(direct)("text") == 31)
    direct.Accept("text")
    check(direct.Number == 71)
    direct.Number = 72
    check(direct.Number == 72)
}

fun main() {
    checkInheritedNativeSlots()
    println(consume())
}
