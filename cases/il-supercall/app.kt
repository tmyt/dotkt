// issue #14: a `super.X()` call from an override must be a NON-virtual `call` to the resolved base slot.
// A `callvirt` re-dispatches by the receiver's runtime type back to the override -> infinite recursion.

open class Base {
	open fun greet() = "base"
	open fun twice(x: Int) = x * 2
	open val tag: String get() = "base-tag"
	open fun describe() = "Base"       // user-declared toString-like slot (NOT the Object slot)
}
class Derived : Base() {
	override fun greet() = "derived+" + super.greet()          // (1) value returned + used in an expression
	override fun twice(x: Int) = super.twice(x) + 1            // (2) super call with an argument
	override val tag: String get() = "derived[" + super.tag + "]"   // (3) super property getter
	override fun describe() = "Derived<" + super.describe() + ">"    // super to a USER base method
}

// (6) 3-level chain: A open / B override calls super / C override calls super.
open class A { open fun name() = "A" }
open class B : A() { override fun name() = super.name() + "B" }
class C : B() { override fun name() = super.name() + "C" }

// super.toString() to a USER base (anySlot + user-owner rename path — NOT the kotlin.Any Object slot, which is XFAIL).
open class Animal { override fun toString() = "animal" }
class Dog : Animal() { override fun toString() = "dog>" + super.toString() }

// super<IFace>.foo() to a Kotlin interface DEFAULT body (DIM): a non-virtual `call` to the bodied interface method.
interface Greeter { fun hi(): String = "hi-default" }
class Impl : Greeter { override fun hi() = "impl+" + super.hi() }

fun main() {
	val d = Derived()
	println(d.greet())        // derived+base
	println(d.twice(10))      // 21
	println(d.tag)            // derived[base-tag]
	println(d.describe())     // Derived<Base>
	println(C().name())       // ABC
	println(Dog().toString()) // dog>animal
	println(Impl().hi())      // impl+hi-default
	// (5) non-regression: a NORMAL virtual dispatch through a Base-typed variable still reaches the override.
	val b: Base = Derived()
	println(b.greet())        // derived+base
	println(b.twice(5))       // 11
}
