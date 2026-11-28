interface ArrayInspection {
    fun inspect(value: Any): Any?
}

open class KotlinArrayInspection : ArrayInspection {
    override fun inspect(value: Any): Any? = (value as Array<*>)[0]
}
