namespace WukongAutoBench;

/// <summary>Лог прогона: в консоль (с цветом) и в run.log в папке отчёта.</summary>
internal static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _file;

    public static void OpenFile(string path)
    {
        _file = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public static void Info(string message) => Write(message, null, "    ");
    public static void Step(string message) => Write(message, ConsoleColor.Cyan, "==> ");
    public static void Ok(string message) => Write(message, ConsoleColor.Green, " ok ");
    public static void Warn(string message) => Write(message, ConsoleColor.Yellow, " !! ");
    public static void Error(string message) => Write(message, ConsoleColor.Red, "ERR ");

    private static void Write(string message, ConsoleColor? color, string prefix)
    {
        lock (Sync)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{time}] ");
            if (color != null) Console.ForegroundColor = color.Value;
            else Console.ResetColor();
            Console.WriteLine(prefix + message);
            Console.ResetColor();
            _file?.WriteLine($"[{time}] {prefix}{message}");
        }
    }
}
