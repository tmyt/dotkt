package clr
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)

@Clr("Probe.Vec2") class Vec2(x: Int, y: Int) {
    @Clr("op_Addition") operator fun plus(o: Vec2): Vec2 = TODO()   // .NET operator (static op_*)
    @Clr("Mag2") fun mag2(): Int = TODO()                          // struct (value-type) instance method
}
@Clr("Probe.Ext") object Ext { @Clr("tripled") fun Int.tripled(): Int = TODO() }
@Clr("Probe.Util") object Util {
    @Clr("Echo") fun <T> echo(x: T): T = TODO()
    @Clr("Sum") fun sum(vararg xs: Int): Int = TODO()
    @Clr("AddDef") fun addDef(a: Int, b: Int = 10): Int = TODO()
}
