using System;

namespace StoredCallbacks {
    public static class Legacy {
        public static int Calls;
        public static Action GetAction() => () => Calls++;
    }
}
