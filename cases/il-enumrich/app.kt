// IL parity: rich enum (ctor params + instance method) -> singleton class lowering.
enum class Planet(val mass: Int) {
	EARTH(5), MARS(1), JUPITER(9);
	fun heavy(): Boolean = mass > 3
}
fun main() {
	println(Planet.EARTH.mass)
	println(Planet.EARTH.heavy())
	println(Planet.MARS.heavy())
	println(Planet.JUPITER.name)
	println(Planet.MARS.ordinal)
	println(Planet.valueOf("JUPITER").mass)
	for (p in Planet.values()) println(p.name)
	println(Planet.EARTH == Planet.EARTH)
	println(Planet.EARTH == Planet.MARS)
}
