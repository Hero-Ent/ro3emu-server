using System.Runtime.CompilerServices;

namespace Ro3Emu.Tests;

/// <summary>
/// Options every test host starts with unless the test sets its own. The web panel listens on a fixed port (5381) by
/// default, which hundreds of parallel test hosts cannot share, so it is off here; PanelTests turn it on with a free
/// port. The EXP bonus file is off too: otherwise every host reads the operator's scripts/exp_bonus.json, so rewards
/// carry the live server's bonus (or a missing file is created at 100x). Environment variables reach every
/// ServerHost.Create through its configuration; a test's configure wins.
/// </summary>
internal static class TestHostDefaults
{
    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable("Ro3Emu__Panel__Enabled", "false");
        Environment.SetEnvironmentVariable("Ro3Emu__ExpBonusFile", Ro3Emu.Server.ServerOptions.ExpBonusOff);
    }
}
