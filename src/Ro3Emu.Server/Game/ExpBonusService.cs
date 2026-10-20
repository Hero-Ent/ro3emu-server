using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Controls the server-wide EXP bonus rate for the closed beta or special events.
/// Base EXP configuration tables are never modified; this applies a configurable multiplier bonus.
/// Multiplier 0 = no bonus (1x standard rate).
/// Multiplier 1 = +1x bonus (2x total rate).
/// Multiplier 10 = +10x bonus (11x total rate).
/// Multiplier 100 = +100x bonus (101x total rate).
/// </summary>
public sealed class ExpBonusService
{
    private readonly IOptions<ServerOptions> _options;
    private readonly ILogger<ExpBonusService>? _log;
    private readonly string _configFilePath;
    private long _multiplier = 100;
    private DateTime _lastFileCheck = DateTime.MinValue;
    private DateTime _lastFileWriteTime = DateTime.MinValue;
    private readonly object _lock = new();

    public ExpBonusService(IOptions<ServerOptions> options, ILogger<ExpBonusService>? log = null)
    {
        _options = options;
        _log = log;

        string baseDir = AppContext.BaseDirectory;
        string cwd = Directory.GetCurrentDirectory();

        // Check if scripts/ folder exists or can be created in cwd or baseDir
        string scriptsDir = Path.Combine(cwd, "scripts");
        if (!Directory.Exists(scriptsDir) && Directory.Exists(Path.Combine(baseDir, "scripts")))
        {
            scriptsDir = Path.Combine(baseDir, "scripts");
        }

        try
        {
            if (!Directory.Exists(scriptsDir)) Directory.CreateDirectory(scriptsDir);
        }
        catch { /* Fallback to cwd */ }

        _configFilePath = Path.Combine(scriptsDir, "exp_bonus.json");
        LoadOrInitialize();
    }

    /// <summary>The active bonus multiplier (0 = no bonus, 100 = 100x bonus).</summary>
    public long Multiplier
    {
        get
        {
            CheckFileReload();
            return _multiplier;
        }
    }

    /// <summary>Calculates the additional bonus EXP given the base exp amount.</summary>
    public long CalculateBonus(long exp)
    {
        long m = Multiplier;
        if (exp <= 0 || m <= 0) return 0;
        try
        {
            return checked(exp * m);
        }
        catch (OverflowException)
        {
            return long.MaxValue - exp;
        }
    }

    /// <summary>Applies the bonus to both base and job EXP.</summary>
    public void ApplyBonus(ref long baseExp, ref long jobExp)
    {
        if (Multiplier <= 0) return;
        if (baseExp > 0) baseExp = checked(baseExp + CalculateBonus(baseExp));
        if (jobExp > 0) jobExp = checked(jobExp + CalculateBonus(jobExp));
    }

    /// <summary>Updates the bonus multiplier and persists to scripts/exp_bonus.json.</summary>
    public void SetMultiplier(long newMultiplier, bool saveToFile = true)
    {
        lock (_lock)
        {
            _multiplier = Math.Max(0, newMultiplier);
            _log?.LogInformation("EXP bonus multiplier updated to {Multiplier}x", _multiplier);
            if (saveToFile) Save();
        }
    }

    private void LoadOrInitialize()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("multiplier", out var prop) && prop.TryGetInt64(out long val))
                    {
                        _multiplier = Math.Max(0, val);
                        _lastFileWriteTime = File.GetLastWriteTimeUtc(_configFilePath);
                        _log?.LogInformation("Loaded EXP bonus multiplier: {Multiplier}x from {Path}", _multiplier, _configFilePath);
                        return;
                    }
                }

                // File does not exist or invalid: write default (100x)
                _multiplier = 100;
                Save();
                _log?.LogInformation("Initialized default EXP bonus multiplier: 100x in {Path}", _configFilePath);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Failed to load {Path}, using default multiplier {Multiplier}x", _configFilePath, _multiplier);
            }
        }
    }

    private void CheckFileReload()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFileCheck).TotalSeconds < 2) return;
        _lastFileCheck = now;

        try
        {
            if (!File.Exists(_configFilePath)) return;
            var writeTime = File.GetLastWriteTimeUtc(_configFilePath);
            if (writeTime == _lastFileWriteTime) return;

            lock (_lock)
            {
                _lastFileWriteTime = writeTime;
                string json = File.ReadAllText(_configFilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("multiplier", out var prop) && prop.TryGetInt64(out long val))
                {
                    long prev = _multiplier;
                    _multiplier = Math.Max(0, val);
                    if (prev != _multiplier)
                    {
                        _log?.LogInformation("EXP bonus multiplier hot-reloaded: {Prev}x -> {Multiplier}x from {Path}", prev, _multiplier, _configFilePath);
                    }
                }
            }
        }
        catch { /* Ignore transient file locks */ }
    }

    private void Save()
    {
        try
        {
            var data = new
            {
                multiplier = _multiplier,
                description = "Multiplicador de bônus de EXP para o servidor Hero Saga (fase Beta). Exemplo: 0 = sem bônus (1x normal), 1 = +1x bônus (2x total), 10 = +10x bônus (11x total), 100 = +100x bônus (101x total)."
            };
            string dir = Path.GetDirectoryName(_configFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
            _lastFileWriteTime = File.GetLastWriteTimeUtc(_configFilePath);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to save EXP bonus config to {Path}", _configFilePath);
        }
    }
}
