using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// Reads of the skill shows exported to data/skills (Config/Skill/skill_*.lua and Lua_Config_EffectData): a template's
/// m_kShows entries, the display kinds an effect has content for, and a show's warning telegraph (m_kPoint.m_kWarningRange).
/// </summary>
public static class SkillShows
{
    /// <summary>
    /// A skill show's first m_kPoint.m_kWarningRange in Lua_Config_EffectData ([show*100..+99] or [show*1000..+99]):
    /// LC_PerformEffect_Mgr.PlayWarningRange (enum_DisplayEffectType.WarningRange = 6) reads m_eWarningType
    /// (enum_SkillWarningRange: Rectangle 0, Circle 1, Fan 2), m_eTargetType (enum_WarningRangeTarget: Self 0,
    /// Target 1, Position 2) and m_fDuration (seconds). InTimeline is true when the template's m_kShows already
    /// schedules the effect itself (kind 8: 53523012, 53526012); the other monster warning shows rely on 31119.
    /// </summary>
    public readonly record struct WarningInfo(
        int EffectId, bool InTimeline, int Type, int TargetType,
        float Radius, float Length, float Width, float Degree, int DurationMs);

    /// <summary>A template's m_kShows entries [effect, kind] of a kind (2: a Gfx point), in timeline order.</summary>
    public static IEnumerable<int> ShowEffects(JsonElement template, int kind)
    {
        if (!template.TryGetProperty("m_kShows", out var shows) || shows.ValueKind != JsonValueKind.Object) yield break;
        foreach (var moment in shows.EnumerateObject())
        {
            if (moment.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in moment.Value.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 2
                    && ConfigValue.Number(entry[1]) == kind && ConfigValue.Number(entry[0]) is > 0 and var effect)
                    yield return (int)effect;
        }
    }

    /// <summary>
    /// The enum_DisplayEffectType kinds a Lua_Config_EffectData effect has content for, in its m_kPoint or its
    /// m_kDamage.m_kActionDamge: AnimGroup 1 (m_kAni), Gfx 2 (m_kGfxList), ShakeCamera 3 (m_kCameraShake), Audio 4
    /// (m_kAudioList).
    /// </summary>
    public static int[] DisplayKinds(JsonElement effect)
    {
        var part = effect.TryGetProperty("m_kPoint", out var point) ? point
            : effect.TryGetProperty("m_kDamage", out var damage) && damage.TryGetProperty("m_kActionDamge", out var action) ? action
            : default;
        if (part.ValueKind != JsonValueKind.Object) return [];
        List<int> kinds = [];
        if (part.TryGetProperty("m_kAni", out _)) kinds.Add(1);
        if (part.TryGetProperty("m_kGfxList", out _)) kinds.Add(2);
        if (part.TryGetProperty("m_kCameraShake", out _)) kinds.Add(3);
        if (part.TryGetProperty("m_kAudioList", out _)) kinds.Add(4);
        return [.. kinds];
    }

    /// <summary>The show's first m_kPoint.m_kWarningRange in Lua_Config_EffectData, or default when it has none.</summary>
    public static WarningInfo Warning(JsonElement? template, IReadOnlyDictionary<string, JsonElement> effects, long show)
    {
        if (show <= 0) return default;
        foreach (long baseId in (ReadOnlySpan<long>)[show * 100, show * 1000])
            for (long id = baseId; id < baseId + 100; id++)
            {
                if (!effects.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var e)
                    || !e.TryGetProperty("m_kPoint", out var pt)
                    || !pt.TryGetProperty("m_kWarningRange", out var w) || w.ValueKind != JsonValueKind.Object) continue;
                bool inTimeline = template is { } t && ShowEffects(t, 8).Contains((int)id);
                int type = w.TryGetProperty("m_eShapeType", out _) ? ConfigValue.Integral32(w, "m_eShapeType")
                    : w.TryGetProperty("m_eWarningType", out _) ? ConfigValue.Integral32(w, "m_eWarningType")
                    : ConfigValue.Number(w, "m_fDegree") > 0 ? 2 : ConfigValue.Number(w, "m_fRadius") > 0 ? 1 : 0;
                double duration = ConfigValue.Number(w, "m_fDuration") is > 0 and var wd ? wd : ConfigValue.Number(pt, "m_fDuration");
                return new((int)id, inTimeline, type,
                    ConfigValue.Integral32(w, "m_eTargetType"), (float)ConfigValue.Number(w, "m_fRadius"),
                    (float)ConfigValue.Number(w, "m_fLength"), (float)ConfigValue.Number(w, "m_fWidth"),
                    (float)ConfigValue.Number(w, "m_fDegree"), (int)Math.Round(duration * 1000));
            }
        return default;
    }
}
