// Producer source for the migrated il-inherit case. Template-method base (Run() calls a PROTECTED VIRTUAL the
// Kotlin subclass overrides — the WinUI Application.OnLaunched pattern) + subtype assignability (Button IS-A
// Widget). Own namespace.
namespace Inherit {
    public class Base {
        public string Run() => "run:" + Tag();
        protected virtual string Tag() => "base";
    }
    public class Widget { public virtual string Name() => "widget"; }
    public class Button : Widget { public override string Name() => "button"; }
    public class Host { public string Show(Widget w) => "show:" + w.Name(); }
}
