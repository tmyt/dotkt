fun applyB(f: () -> Int): Int = f()
fun fromB(): Int { var flag = false; return applyB({ flag = true; if (flag) 20 else 0 }) }
