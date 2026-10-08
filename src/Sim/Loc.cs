namespace Primordium;

// Interface language. English by default; Russian is the other one. Both texts sit side by side at
// the place they are used: Loc.T("born", "рождено"). Code that builds text once (window layouts,
// cached arrays) must build it again when Changed fires, or read Loc.T each time it draws.
public static class Loc
{
    const char Sep = '\u001f';

    public static bool En { get; private set; } = true;
    public static string Code => En ? "en" : "ru";
    public static event System.Action Changed;

    public static void Set(string code)
    {
        bool en = code != "ru";
        if (en == En) return;
        En = en;
        Changed?.Invoke();
    }

    public static string T(string en, string ru) => En ? en : ru;
    public static T[] T<T>(T[] en, T[] ru) => En ? en : ru;

    // Text kept past the moment (chronicle events, saved with the world): both languages in one string,
    // shown by Show in the language of the moment of reading. A string without the separator (old saves)
    // is shown as it is.
    public static string Both(string en, string ru) => en + Sep + ru;

    public static string Show(string s)
    {
        if (s == null) return null;
        int i = s.IndexOf(Sep);
        return i < 0 ? s : En ? s[..i] : s[(i + 1)..];
    }
}
