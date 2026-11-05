inline fun withSeed(f: (Int) -> Int): Int {
    val seed = 40
    val result = f(seed)
    return result + 1
}
