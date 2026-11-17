using System.Text;

internal static class InteractivePassword
{
    internal static string Read(Func<ConsoleKeyInfo> readKey)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = readKey();
            if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                throw new InvalidOperationException("Password entry cancelled.");
            if (key.Key == ConsoleKey.Enter)
            {
                if (buffer.Length == 0) throw new InvalidOperationException("Test-only password is required.");
                return buffer.ToString();
            }
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (char.IsControl(key.KeyChar)) continue;
            if (buffer.Length >= 1024) throw new InvalidOperationException("Test-only password is too long.");
            buffer.Append(key.KeyChar);
        }
    }
}
