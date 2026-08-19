@file:Suppress("OVERRIDE_DEPRECATION", "UNCHECKED_CAST")

package explicitslotprobe

import Probe.ExplicitCollisionCarrier
import Probe.ConstructedExplicitCollisionCarrier
import Probe.ExplicitEventCollisionCarrier
import Probe.ExplicitEventReimplementationBase
import Probe.ExplicitIndexerCollisionCarrier
import Probe.ExplicitIndexerReimplementationBase
import Probe.ExplicitPropertyCollisionCarrier
import Probe.ExplicitPropertyReimplementationBase
import Probe.ExplicitReimplementationBase
import Probe.ILeftExplicitSlot
import Probe.IConstructedExplicitSlot
import Probe.IExplicitOverHiddenDefaultSlot
import Probe.ILeftExplicitEventSlot
import Probe.ILeftExplicitIndexerSlot
import Probe.ILeftExplicitPropertySlot
import Probe.IReimplementedEventSlot
import Probe.IReimplementedIndexerSlot
import Probe.IReimplementedPropertySlot
import Probe.IReimplementedSlot
import Probe.IPublicAndExplicitMethodSlot
import Probe.IPublicAndExplicitPropertySlot
import Probe.IPublicAndExplicitIndexerSlot
import Probe.IPublicExplicitEventSlot
import Probe.IRightExplicitSlot
import Probe.IRightExplicitEventSlot
import Probe.IRightExplicitIndexerSlot
import Probe.IRightExplicitPropertySlot
import Probe.ExplicitOverHiddenDefaultCarrier
import Probe.PublicAndExplicitMethodCarrier
import Probe.StaticAndExplicitMethodCarrier
import Probe.PublicAndExplicitPropertyCarrier
import Probe.PublicAndExplicitIndexerCarrier
import Probe.PublicAndExplicitEventCarrier
import kotlin.clr.clrEvent

class KotlinReimplementation : ExplicitReimplementationBase(), IReimplementedSlot {
    override fun M(): Int = 7
}

class KotlinUnlistedMethod : ExplicitReimplementationBase() {
    override fun M(): Int = 9
}

class KotlinRightReimplementation : ExplicitCollisionCarrier(), IRightExplicitSlot {
    override fun Pick(): Int = 20
}

class KotlinPublicMethodReimplementation :
    PublicAndExplicitMethodCarrier(), IPublicAndExplicitMethodSlot {
    override fun Read(): Int = 33
}

class KotlinUnlistedPublicMethod : PublicAndExplicitMethodCarrier() {
    override fun Read(): Int = 38
}

class KotlinPublicPropertyReimplementation :
    PublicAndExplicitPropertyCarrier(), IPublicAndExplicitPropertySlot {
    override var Number: Int = 34
}

class KotlinUnlistedPublicProperty : PublicAndExplicitPropertyCarrier() {
    override var Number: Int = 39
}

class KotlinPublicIndexerReimplementation :
    PublicAndExplicitIndexerCarrier(), IPublicAndExplicitIndexerSlot {
    private var item = 35
    override operator fun get(index: Int): Int = item + index
    override operator fun set(index: Int, value: Int) { item = value - index }
}

class KotlinUnlistedPublicIndexer : PublicAndExplicitIndexerCarrier() {
    private var item = 40
    override operator fun get(index: Int): Int = item + index
    override operator fun set(index: Int, value: Int) { item = value - index }
}

class KotlinPublicEventReimplementation :
    PublicAndExplicitEventCarrier(), IPublicExplicitEventSlot {
    override val Changed by clrEvent()
}

class KotlinUnlistedPublicEvent : PublicAndExplicitEventCarrier() {
    override val Changed by clrEvent()
}

class KotlinPropertyReimplementation : ExplicitPropertyReimplementationBase(), IReimplementedPropertySlot {
    override var Number: Int = 10
}

class KotlinUnlistedProperty : ExplicitPropertyReimplementationBase() {
    override var Number: Int = 11
}

class KotlinIndexerReimplementation : ExplicitIndexerReimplementationBase(), IReimplementedIndexerSlot {
    private var item = 12
    override operator fun get(index: Int): Int = item + index
    override operator fun set(index: Int, value: Int) { item = value - index }
}

class KotlinUnlistedIndexer : ExplicitIndexerReimplementationBase() {
    private var item = 13
    override operator fun get(index: Int): Int = item + index
    override operator fun set(index: Int, value: Int) { item = value - index }
}

class KotlinEventReimplementation : ExplicitEventReimplementationBase(), IReimplementedEventSlot {
    override val Updated by clrEvent()
}

class KotlinUnlistedEvent : ExplicitEventReimplementationBase() {
    override val Updated by clrEvent()
}

fun main() {
    val carrier = ExplicitCollisionCarrier()
    val constructed = ConstructedExplicitCollisionCarrier()
    val right = KotlinRightReimplementation()
    val unlisted = KotlinUnlistedMethod()
    val propertyCarrier = ExplicitPropertyCollisionCarrier()
    val property = KotlinPropertyReimplementation()
    val unlistedProperty = KotlinUnlistedProperty()
    val indexerCarrier = ExplicitIndexerCollisionCarrier()
    val indexer = KotlinIndexerReimplementation()
    val unlistedIndexer = KotlinUnlistedIndexer()
    val eventCarrier = ExplicitEventCollisionCarrier()
    var observed = 0
    (eventCarrier as ILeftExplicitEventSlot).Updated.subscribe { observed += it }
    (eventCarrier as IRightExplicitEventSlot).Updated.subscribe { observed += it * 10 }
    eventCarrier.RaiseLeft(1)
    eventCarrier.RaiseRight(2)
    val event = KotlinEventReimplementation()
    (event as IReimplementedEventSlot).Updated.subscribe { observed += it * 100 }
    event.Updated.invoke(3)
    val unlistedEvent = KotlinUnlistedEvent()
    (unlistedEvent as IReimplementedEventSlot).Updated.subscribe { observed += it * 1000 }
    unlistedEvent.Updated.invoke(4)
    unlistedEvent.RaiseBase(5)
    check((carrier as ILeftExplicitSlot).Pick() == 1)
    check((carrier as IRightExplicitSlot).Pick() == 2)
    check((constructed as IConstructedExplicitSlot<Int>).Read() == 21)
    check((constructed as IConstructedExplicitSlot<String>).Read() == "twenty-two")
    check((constructed as IConstructedExplicitSlot<Int>).Value == 23)
    check((constructed as IConstructedExplicitSlot<String>).Value == "twenty-four")
    check((ExplicitOverHiddenDefaultCarrier() as IExplicitOverHiddenDefaultSlot).Resolve() == 32)
    check((right as ILeftExplicitSlot).Pick() == 1)
    check((right as IRightExplicitSlot).Pick() == 20)
    check((KotlinReimplementation() as IReimplementedSlot).M() == 7)
    check(unlisted.M() == 9)
    check((unlisted as IReimplementedSlot).M() == 3)
    check((KotlinPublicMethodReimplementation() as IPublicAndExplicitMethodSlot).Read() == 33)
    check(StaticAndExplicitMethodCarrier.Read() == 42)
    check((StaticAndExplicitMethodCarrier() as IPublicAndExplicitMethodSlot).Read() == 43)
    val unlistedPublicMethod = KotlinUnlistedPublicMethod()
    check(unlistedPublicMethod.Read() == 38)
    check((unlistedPublicMethod as IPublicAndExplicitMethodSlot).Read() == 26)

    val leftProperty = propertyCarrier as ILeftExplicitPropertySlot
    val rightProperty = propertyCarrier as IRightExplicitPropertySlot
    leftProperty.Number = 40
    rightProperty.Number = 50
    check(leftProperty.Number == 40)
    check(rightProperty.Number == 50)
    val reimplementedProperty = property as IReimplementedPropertySlot
    reimplementedProperty.Number = 100
    check(property.Number == 100)
    val inheritedProperty = unlistedProperty as IReimplementedPropertySlot
    inheritedProperty.Number = 60
    check(inheritedProperty.Number == 60)
    check(unlistedProperty.Number == 11)
    val publicProperty = KotlinPublicPropertyReimplementation() as IPublicAndExplicitPropertySlot
    publicProperty.Number = 36
    check(publicProperty.Number == 36)
    val unlistedPublicProperty = KotlinUnlistedPublicProperty()
    val inheritedPublicProperty = unlistedPublicProperty as IPublicAndExplicitPropertySlot
    inheritedPublicProperty.Number = 40
    check(inheritedPublicProperty.Number == 40)
    check(unlistedPublicProperty.Number == 39)

    val leftIndexer = indexerCarrier as ILeftExplicitIndexerSlot
    val rightIndexer = indexerCarrier as IRightExplicitIndexerSlot
    leftIndexer[1] = 70
    rightIndexer[1] = 80
    check(leftIndexer[1] == 70)
    check(rightIndexer[1] == 80)
    val reimplementedIndexer = indexer as IReimplementedIndexerSlot
    reimplementedIndexer[1] = 90
    check(indexer[1] == 90)
    val inheritedIndexer = unlistedIndexer as IReimplementedIndexerSlot
    inheritedIndexer[1] = 100
    check(inheritedIndexer[1] == 100)
    check(unlistedIndexer[1] == 14)
    val publicIndexer = KotlinPublicIndexerReimplementation() as IPublicAndExplicitIndexerSlot
    publicIndexer[1] = 37
    check(publicIndexer[1] == 37)
    val unlistedPublicIndexer = KotlinUnlistedPublicIndexer()
    val inheritedPublicIndexer = unlistedPublicIndexer as IPublicAndExplicitIndexerSlot
    inheritedPublicIndexer[1] = 42
    check(inheritedPublicIndexer[1] == 42)
    check(unlistedPublicIndexer[1] == 41)
    val publicEvent = KotlinPublicEventReimplementation()
    (publicEvent as IPublicExplicitEventSlot).Changed.subscribe { observed += it * 10000 }
    publicEvent.Changed.invoke(1)
    val unlistedPublicEvent = KotlinUnlistedPublicEvent()
    (unlistedPublicEvent as IPublicExplicitEventSlot).Changed.subscribe { observed += it * 100000 }
    unlistedPublicEvent.Changed.invoke(2)
    unlistedPublicEvent.RaiseExplicit(2)
    check(observed == 215321)
    println(463)
}
