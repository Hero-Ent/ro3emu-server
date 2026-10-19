using System;
using System.IO;

namespace Ro3Emu.Patcher;

/// <summary>Simple thread-safe file logger for patcher diagnostics.</summary>
public static class PatcherLog
{
    static readonly object Sync = new();
    public static string? LogFilePath { get; set; }

    public static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Console.WriteLine(line);
            if (!string.IsNullOrEmpty(LogFilePath))
            {
                lock (Sync)
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
        }
        catch { }
    }
}
