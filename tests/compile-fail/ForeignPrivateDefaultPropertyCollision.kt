import CompileFailForeignPrivateDefault.IPrivateDefaultProperty

open class ForeignPrivateDefaultPropertyCollision : IPrivateDefaultProperty {
    // An open function is a CLR virtual MethodDef and captures IPropertySlot.get_Value unless the inherited default
    // receives a class-level explicit MethodImpl. Its selected body is private in another assembly.
    open fun get_Value(): Int = 99
}
