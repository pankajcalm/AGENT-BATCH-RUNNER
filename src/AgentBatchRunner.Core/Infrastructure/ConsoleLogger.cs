namespace AgentBatchRunner.Infrastructure;

public sealed class ConsoleLogger
{
    public void Info(string message)
    {
        Write("INFO", message, ConsoleColor.Gray);
    }

    public void Warning(string message)
    {
        Write("WARN", message, ConsoleColor.Yellow);
    }

    public void Error(string message)
    {
        Write("ERROR", message, ConsoleColor.Red);
    }

    private static void Write(string level, string message, ConsoleColor color)
    {
        var previousColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {level} {SensitiveDataRedactor.Redact(message)}");
        Console.ForegroundColor = previousColor;
    }
}
