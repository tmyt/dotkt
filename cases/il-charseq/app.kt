class S(val s:String):CharSequence{
  override val length:Int get()=s.length
  override fun get(index:Int):Char=s[index]
  override fun subSequence(startIndex:Int,endIndex:Int):CharSequence=S(s.substring(startIndex,endIndex))
}
fun show(cs:CharSequence):Int = cs.length     // CharSequence-typed param (polymorphic)
fun main(){
  val c=S("hello")
  println(c.length)             // 5
  println(c[1])                 // e  (operator get)
  val sub:CharSequence=c.subSequence(1,4)
  println(sub.length)           // 3
  println(sub[0])               // e
  println(show(c))              // 5  (passed as CharSequence)
}
