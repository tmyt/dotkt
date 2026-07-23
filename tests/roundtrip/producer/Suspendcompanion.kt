package suspendcompanion

class CompanionSuspendApi {
    companion object {
        suspend fun compute(input: Int): Int = input + 1
    }
}
