using System;

class ClickModifierTests {
    [Flags]
    enum Keys {
        None = 0, KeyCode = 65535, Shift = 65536, Control = 131072,
        ShiftKey = 16, ControlKey = 17, LShiftKey = 160, RShiftKey = 161,
        LControlKey = 162, RControlKey = 163, A = 65
    }

    // PRODUCTION_METHODS

    static void Main() {
        int checks = 0;
        foreach (bool control in new[] {true, false}) {
            Keys modifier = control ? Keys.Control : Keys.Shift;
            Keys key = control ? Keys.ControlKey : Keys.ShiftKey;
            Keys left = control ? Keys.LControlKey : Keys.LShiftKey;
            Keys right = control ? Keys.RControlKey : Keys.RShiftKey;
            foreach (Keys reported in new[] {modifier, key, left, right, modifier | Keys.A}) {
                if (!HasModifier(reported, Keys.None, modifier, key, left, right))
                    throw new Exception("reported modifier not recognized");
                checks++;
            }
            if (!HasModifier(Keys.None, modifier, modifier, key, left, right))
                throw new Exception("omitted event modifier has no live fallback");
            checks++;
            foreach (Keys reported in new[] {Keys.None, Keys.A, control ? Keys.Shift : Keys.Control}) {
                if (HasModifier(reported, Keys.None, modifier, key, left, right))
                    throw new Exception("ordinary or other modifier click misclassified");
                checks++;
            }
        }
        Console.WriteLine("PASS: " + checks + " click-modifier assertions");
    }
}
