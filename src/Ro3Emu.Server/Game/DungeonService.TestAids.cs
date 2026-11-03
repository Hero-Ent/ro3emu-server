namespace Ro3Emu.Server.Game;

/// <summary>Test aids for scripted scenes (GM commands), not original behaviour.</summary>
public sealed partial class DungeonService
{
    /// <summary>
    /// GM !step: the player's scripted scene goes to its step <paramref name="step"/> (1-based), as if the steps before it
    /// had completed: riders get off, and the step begins with its actors, cues and timers. The current step's own end
    /// (its teleport, show or fall) does not play. Returns what happened, for the GM reply.
    /// </summary>
    public async Task<string> JumpToStepAsync(GameSession s, int step)
    {
        if (Active(s) is not { } a) return "Not in a running dungeon.";
        if (Step(a) == null) return $"Scene {a.Plan.SceneEntry} has no scripted steps.";
        if (step < 1 || step > a.Plan.Waves.Count) return $"Scene {a.Plan.SceneEntry} has steps 1..{a.Plan.Waves.Count}.";
        if (a.Spawning || a.Advancing || a.Killed.Count != a.Spawned) return "The scene is busy (spawning or fighting); try again.";
        await EndStoryStateAsync(a); // off the slide and the vehicle before the actors change
        a.StepGeneration++; // the old step's timers find a later generation and do nothing
        a.Wave = step - 1;
        await SpawnWaveAsync(a);
        return $"Scene {a.Plan.SceneEntry}: step {step} '{Step(a)?.Stage}' (test aid).";
    }
}
