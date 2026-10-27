using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The mobile client reads a plan's manual loads by wheel slot without a guard: LC_System_Skill._refreshSkillRedDotLoad
/// indexes manualLoadSkill from PBSkillWheelSlotType.SkillWheelNormal1 to SkillWheelNormal6 and reads .skillId.
/// </summary>
public sealed class SkillWheelLoadTests
{
    [Fact]
    public void AManualLoadListHoldsOneEntryPerDefaultWheelSlot()
    {
        var plan = new SkillPlan();
        SkillService.PadManualLoads(plan);
        Assert.Equal(SkillService.DefaultBarSlots, plan.ManualLoadSkill.Count);
        Assert.All(plan.ManualLoadSkill, load => Assert.Equal(0, load.SkillId));
    }

    [Fact]
    public void PaddingKeepsTheLoadsAlreadyThere()
    {
        var plan = new SkillPlan();
        plan.ManualLoadSkill.Add(new SkillData { SkillId = 10002, SkillLevel = 1 });
        SkillService.PadManualLoads(plan);
        Assert.Equal(SkillService.DefaultBarSlots, plan.ManualLoadSkill.Count);
        Assert.Equal(10002, plan.ManualLoadSkill[0].SkillId);

        for (int slot = plan.ManualLoadSkill.Count; slot < SkillService.BarSlots; slot++) plan.ManualLoadSkill.Add(new SkillData());
        SkillService.PadManualLoads(plan);
        Assert.Equal(SkillService.BarSlots, plan.ManualLoadSkill.Count); // a longer bar is never cut
    }
}
