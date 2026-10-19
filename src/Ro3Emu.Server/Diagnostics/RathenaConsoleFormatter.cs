using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>
/// Formats console logs in the classic rAthena emulator style:
/// [Status]  (Green)
/// [Info]    (Cyan)
/// [Notice]  (White)
/// [Warning] (Yellow)
/// [Error]   (Red)
/// [Debug]   (Magenta)
/// </summary>
public sealed class RathenaConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "rathena";

    public RathenaConsoleFormatter() : base(FormatterName) { }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string? message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception == null)
            return;

        string tag;
        string color;

        switch (logEntry.LogLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                tag = "[Debug]";
                color = "\x1b[1;35m"; // Magenta
                break;
            case LogLevel.Information:
                if (IsStatus(logEntry.Category, message))
                {
                    tag = "[Status]";
                    color = "\x1b[1;32m"; // Bold Green
                }
                else
                {
                    tag = "[Info]";
                    color = "\x1b[1;36m"; // Bold Cyan
                }
                break;
            case LogLevel.Warning:
                tag = "[Warning]";
                color = "\x1b[1;33m"; // Bold Yellow
                break;
            case LogLevel.Error:
                tag = "[Error]";
                color = "\x1b[1;31m"; // Bold Red
                break;
            case LogLevel.Critical:
                tag = "[Critical]";
                color = "\x1b[1;41;37m"; // White on Red background
                break;
            default:
                tag = "[Notice]";
                color = "\x1b[1;37m"; // Bold White
                break;
        }

        const string reset = "\x1b[0m";

        if (!string.IsNullOrEmpty(message))
        {
            textWriter.WriteLine($"{color}{tag}{reset}: {message}");
        }

        if (logEntry.Exception != null)
        {
            textWriter.WriteLine($"\x1b[1;31m[Error]{reset}: {logEntry.Exception.GetType().Name}: {logEntry.Exception.Message}");
            if (!string.IsNullOrEmpty(logEntry.Exception.StackTrace))
            {
                textWriter.WriteLine(logEntry.Exception.StackTrace);
            }
        }
    }

    private static bool IsStatus(string category, string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (category.Contains("Lifetime", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("listening", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("preloaded", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("ready", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("started", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("Data dir", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("Database backed up", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("shutting down", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
