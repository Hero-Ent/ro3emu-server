using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scripted story scenes, with or without fights (Reconstructed/scripted-scenes.json; its "contract" section cites the client
/// code for every event used here). The original server's scene script is not in the client: each scene is rebuilt as
/// steps, each naming the DungeonQuestConfig quest the tracker shows, the scene actors shown during it, an optional
/// run-time story the client plays, and the client events that complete it. Nothing is keyed by a task id: a step
/// completes on any listed event (interaction, dungeon message, region, point or plot end), or after a time on its own
/// clock (After: the step's Id milliseconds after it began, paced like its cues).
/// </summary>
public sealed partial class ReconstructedDungeons
{
    public enum TriggerKind { Interaction, Message, Region, Point, Plot, After }

    /// <summary>A client event that completes (or, under "ignore", deliberately does not complete) a step.</summary>
    public sealed record StepTrigger(TriggerKind Kind, long Id, float Radius = 0);

    /// <summary>A scene actor: an NPCConfig id at a scene-editor point, with the buffs (BuffConfig ids) it carries.
    /// <paramref name="Dx"/>/<paramref name="Dz"/> (metres) place it beside the point when the scene has no point of its own
    /// for it (inferred placement).</summary>
    public sealed record SceneActor(int NpcId, long Point, IReadOnlyList<int> Buffs, float Dx = 0, float Dz = 0);

    /// <summary>
    /// One scripted step. <paramref name="Story"/> is a PlotInterpretation id the client plays at the step's start
    /// (0: none); <paramref name="TeleportAfter"/> a scene-editor point the participants are moved to when it
    /// completes (0: none). <paramref name="Goal"/> is the tracker's "(done/total)" while the step runs.
    /// <paramref name="Fight"/> are monsters spawned at the step's start: the step also waits until every one is
    /// defeated (a story dungeon that mixes fights with scripted parts). <paramref name="All"/>: every completing event
    /// is needed, not any one ("Have a Chitchat with them"). <paramref name="Strikes"/>: an interaction of an actor that
    /// casts a skill at the nearest monster of the step's fight; with Shots above 0, that many strikes defeat a monster
    /// whatever the skill's damage. <paramref name="Seek"/>: the step is a hide-and-seek game
    /// whose hiding places are its Point triggers; walking there does not complete it, winning the game does.
    /// <paramref name="Companions"/>: the actors marked "companion", shown as allied fighters (CompanionUnit) instead of
    /// plain scene actors; they are not in <paramref name="Actors"/>.
    /// <paramref name="React"/>: what the step's speaker says, and the effect shown, when it begins, when an ignored event
    /// arrives and when it completes.
    /// <see cref="Cannon"/>: an interaction of an actor that puts the player on it as a turret, firing at the step's fight
    /// (the airship's cannons, DungeonService.Cannon). <see cref="Cues"/>: what the step does on its own clock (lines,
    /// effects, passing units; DungeonService.Cues). <see cref="Allies"/>: scene units of the player's side that fight the
    /// step's monsters beside the player (DungeonService.Allies).
    /// </summary>
    public sealed record ScriptedStep(string Stage, int Quest, (int Done, int Total) Goal, IReadOnlyList<SceneActor> Actors,
        long Story, IReadOnlyList<StepTrigger> Complete, IReadOnlyList<StepTrigger> Ignore, long TeleportAfter,
        IReadOnlyList<Spawn> Fight, bool All, IReadOnlyList<(long Interaction, long Skill, int Shots)>? Strikes = null,
        Seek? Seek = null, Shelter? Shelter = null, Reaction? React = null)
    {
        public IReadOnlyList<SceneActor> Companions { get; init; } = [];

        /// <summary>A scene-editor guide route (m_kGuidePoints) the actor a player used to complete the step walks, the
        /// participants with it, before <see cref="TeleportAfter"/> and the next step (0: none; inferred).</summary>
        public long RideAfter { get; init; }

        /// <summary>Milliseconds the step stays on after it is completed, before the next step or the settlement (0: none;
        /// inferred): a bubble of the actors it showed is still being read, or a defeated enemy is still falling.</summary>
        public int HoldAfterMs { get; init; }

        public Cannon? Cannon { get; init; }
        public IReadOnlyList<Cue> Cues { get; init; } = [];
        public IReadOnlyList<SceneActor>? Allies { get; init; }
    }

    /// <summary>
    /// A step's reactions (inferred, Louie's 3120703 shell game: his lines and the right/wrong Bell effects are client rows
    /// that nothing in the client sends). <paramref name="Speaker"/> is the step's actor whose uid the lines carry;
    /// <paramref name="Start"/> ChatConfig rows go out one after another when the step begins, each when the previous one's
    /// _iTime has run out. <paramref name="Ignored"/> answers an event the step ignores, <paramref name="Complete"/> the
    /// event that completes it.
    /// </summary>
    public sealed record Reaction(int Speaker, IReadOnlyList<int> Start, Response? Ignored, Response? Complete);

    /// <summary>
    /// One reaction: the ChatConfig row <paramref name="Say"/> (0: none) and the BuffConfig row <paramref name="Buff"/>
    /// (0: none) put on the step's <paramref name="On"/> actor that stands at the point of the actor the player used. A
    /// completing reaction holds the next step (or the settlement) until its line's _iTime has run out.
    /// </summary>
    public sealed record Response(int Say, int Buff, int On);

    /// <summary>
    /// A unit riding a scripted fight's monster (a pirate on the enemy airship's deck): an NPCConfig id at an offset in
    /// metres from the monster, turned with its facing (x to its right, y up, z ahead). Where a deck lies on a model is
    /// not in the client data, so every offset is (inferred).
    /// </summary>
    public sealed record Rider(int NpcId, float Dx, float Dy, float Dz);

    /// <summary>
    /// Something a step does on its own clock, <paramref name="AtMs"/> after the step starts and again every
    /// <paramref name="EveryMs"/> while the step lasts (0: once): a ChatConfig line (<paramref name="Chat"/>, sent as
    /// 1022; a Bubbling row over the step's actor <paramref name="Speaker"/>), an effect each participant sees from
    /// their own unit or at <paramref name="Point"/> (<paramref name="Effect"/>, a Lua_Config_EffectData id, of
    /// enum_DisplayEffectType <paramref name="EffectType"/>), or a unit (<paramref name="Npc"/>) shown at
    /// <paramref name="Point"/> or flying the patrol path <paramref name="Patrol"/> for <paramref name="LifeMs"/> (0: until
    /// the scene ends; <paramref name="Vanish"/>: until its path ends), seen from <paramref name="Sight"/> metres (0: the
    /// usual view radius). The original's scene script is not in the client: every cue's timing is (inferred).
    /// A cue can also make an actor (<paramref name="Speaker"/>) play an interaction's head bubble (<paramref name="Bubble"/>,
    /// an NPCInteractConfig row whose 4001 action shows a DialogueConfig bubble), or send the crew of the cue units of NPC
    /// <paramref name="Dismiss"/> away. A unit cue may fly a route of its own (<paramref name="Path"/>, map coordinates)
    /// and carry <paramref name="Riders"/>. With <paramref name="HpBelow"/> the cue waits until the step's fight monster
    /// <paramref name="Of"/> is down to that share of its HP (percent), then <paramref name="AtMs"/> more.
    /// A cue can make a shown unit (an actor or an ally) walk a route from where it stands (<paramref name="Walk"/>, its
    /// NPC id, with <paramref name="Path"/> or <paramref name="Patrol"/>), or play a skill's show (<paramref name="Cast"/>,
    /// from the shown unit of NPC <paramref name="Speaker"/>: an actor, an ally, a cue unit or a rider). A cue unit can
    /// burn whoever stands within <paramref name="Burn"/> metres of it, every second, with its NPCConfig skill cast as
    /// the fight monster <paramref name="Of"/> while that one lives, and it can blow up, playing <paramref name="Blast"/>
    /// and leaving, when a fighting monster or a participant comes within <paramref name="Fuse"/> metres of it.
    /// </summary>
    public sealed record Cue(int AtMs, int EveryMs, int Chat, int Speaker, int Effect, int EffectType, int Npc, long Point,
        long Patrol, int LifeMs, bool Vanish, float Sight, int Bubble = 0, int Dismiss = 0, IReadOnlyList<MapPoint>? Path = null,
        IReadOnlyList<Rider>? Riders = null, int HpBelow = 0, int Of = 0, int Walk = 0, int Cast = 0, float Burn = 0,
        float Fuse = 0, int Blast = 0);

    /// <summary>
    /// A turret the player boards (the airship's cannons): <paramref name="Interaction"/> is the actor's NPCInteractConfig
    /// row whose _iStatusID is a GameplayStatusConfig of type 10 (UpperCannon). <paramref name="Fire"/> and
    /// <paramref name="Quit"/> are that status's GameplayStatusButtonConfig buttons, which only the client's Lua
    /// GamePlayStatusExtraConfig lists (status 12: m_kButtons {27, 28}). Every monster of the step's fight sinks after
    /// <paramref name="Shots"/> fire hits, whatever the skill's damage (inferred). <paramref name="Show"/> is the skill
    /// template a shot shows (0: the fire skill's own _iSkillID template). <paramref name="Shown"/> is the damage number a
    /// hit shows, give or take a little (0: the hit's own damage); the sinking still takes Shots hits.
    /// </summary>
    public sealed record Cannon(long Interaction, int Fire, int Quit, int Shots, long Show = 0, long Shown = 0);

    /// <summary>
    /// Cover from a boss's sweeping attack (inferred, the story Pharaoh's 3120702 after the raid Pharaoh's "Pharaoh is
    /// unleashing Cleansing Light! Quickly, hide behind the Pyramid!"): every <paramref name="EveryMs"/> while the fight's
    /// <paramref name="Boss"/> lives, the <paramref name="Tip"/> ChatConfig row goes out and the boss holds its ground for
    /// <paramref name="WarnMs"/>; then <paramref name="Skill"/> hits every participant not in the shadow of the
    /// <paramref name="Cover"/> actor: behind it as seen from the boss, within <paramref name="Width"/> metres of that line
    /// and <paramref name="Depth"/> metres past the cover.
    /// </summary>
    public sealed record Shelter(int Boss, int Cover, int Tip, long Skill, int EveryMs, int WarnMs, float Width, float Depth);

    /// <summary>
    /// A hide-and-seek game (inferred, Moonlight Flower's 3120802): the step's first actor opens with its
    /// <paramref name="Opening"/> bubbles; each round warns (<paramref name="Round"/>), counts down
    /// (<paramref name="Countdown"/>, a CombatCountdownTip row whose time is the count) and looks. Every participant must
    /// then stand at one of the step's points that this game has not searched yet. A found participant restarts the game
    /// (<paramref name="Found"/>, a bubble); a passed round searches the points within <paramref name="SearchedRadius"/>
    /// of each hiding place (<paramref name="Longer"/>), and the last of <paramref name="Rounds"/> wins
    /// (<paramref name="Won"/>), completing the step. Every message is a ChatConfig row sent as 1022.
    /// </summary>
    public sealed record Seek(int Rounds, float SearchedRadius, IReadOnlyList<int> Opening, int Round, int Countdown, int Longer,
        int Won, int Found);

    /// <summary>
    /// A scene. <paramref name="Unplayed"/> are DungeonQuestConfig quests of the scene that no step shows: the branch
    /// that shows them cannot be reached in the reconstruction (each has its reason in the json, "unplayed").
    /// </summary>
    public sealed record ScriptedScene(long Scene, string Name, string Confidence, IReadOnlyList<ScriptedStep> Steps,
        IReadOnlyList<int> Unplayed);

    private readonly Dictionary<long, ScriptedScene> _scripted = new();

    public IReadOnlyCollection<ScriptedScene> AllScripted => _scripted.Values;

    /// <summary>The scripted reconstruction of a scene, when the switch is on.</summary>
    public ScriptedScene? Scripted(long scene) => Enabled && _scripted.TryGetValue(scene, out var s) ? s : null;

    private void LoadScriptedScenes()
    {
        using var stream = typeof(ReconstructedDungeons).Assembly.GetManifestResourceStream("Ro3Emu.Server.Reconstructed.scripted-scenes.json")
            ?? throw new InvalidOperationException("Embedded Reconstructed/scripted-scenes.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        foreach (var entry in doc.RootElement.GetProperty("scenes").EnumerateObject())
        {
            long scene = long.Parse(entry.Name, CultureInfo.InvariantCulture);
            var steps = entry.Value.GetProperty("steps").EnumerateArray().Select(Step).ToArray();
            if (steps.Length == 0) throw new InvalidOperationException($"Scripted scene {scene} has no steps.");
            var unplayed = entry.Value.TryGetProperty("unplayed", out var u)
                ? u.EnumerateArray().Select(q => q.GetProperty("quest").GetInt32()).ToArray() : [];
            _scripted[scene] = new ScriptedScene(scene, entry.Value.GetProperty("name").GetString() ?? "",
                entry.Value.GetProperty("confidence").GetString() ?? "", steps, unplayed);
        }
    }

    private static ScriptedStep Step(JsonElement s)
    {
        var goal = s.TryGetProperty("goal", out var g) ? (g[0].GetInt32(), g[1].GetInt32()) : (0, 1);
        var listed = s.TryGetProperty("actors", out var a) ? a.EnumerateArray().ToArray() : [];
        bool IsCompanion(JsonElement x) => x.TryGetProperty("companion", out var c) && c.GetBoolean();
        var actors = listed.Where(x => !IsCompanion(x)).Select(Actor).ToArray();
        var complete = Triggers(s, "complete");
        var fight = s.TryGetProperty("fight", out var f) ? f.EnumerateArray().Select(FightSpawn).ToArray() : [];
        if (complete.Count == 0 && fight.Length == 0)
            throw new InvalidOperationException($"Scripted step '{s.GetProperty("stage").GetString()}' cannot complete.");
        return new ScriptedStep(s.GetProperty("stage").GetString() ?? "", s.GetProperty("quest").GetInt32(), goal, actors,
            s.TryGetProperty("story", out var story) ? story.GetInt64() : 0, complete, Triggers(s, "ignore"),
            s.TryGetProperty("after", out var after) && after.TryGetProperty("teleport", out var to) ? to.GetInt64() : 0,
            fight, s.TryGetProperty("all", out var all) && all.GetBoolean(),
            s.TryGetProperty("strike", out var strike)
                ? strike.EnumerateArray().Select(x => (x.GetProperty("interaction").GetInt64(), x.GetProperty("skill").GetInt64(),
                    x.TryGetProperty("shots", out var shots) ? shots.GetInt32() : 0)).ToArray()
                : null,
            s.TryGetProperty("seek", out var seek)
                ? new Seek(seek.GetProperty("rounds").GetInt32(), seek.GetProperty("searchedRadius").GetSingle(),
                    [.. seek.GetProperty("opening").EnumerateArray().Select(v => v.GetInt32())], seek.GetProperty("round").GetInt32(),
                    seek.GetProperty("countdown").GetInt32(), seek.GetProperty("longer").GetInt32(), seek.GetProperty("won").GetInt32(),
                    seek.GetProperty("found").GetInt32())
                : null,
            s.TryGetProperty("shelter", out var shelter)
                ? new Shelter(shelter.GetProperty("boss").GetInt32(), shelter.GetProperty("cover").GetInt32(), shelter.GetProperty("tip").GetInt32(),
                    shelter.GetProperty("skill").GetInt64(), shelter.GetProperty("everyMs").GetInt32(), shelter.GetProperty("warnMs").GetInt32(),
                    shelter.GetProperty("width").GetSingle(), shelter.GetProperty("depth").GetSingle())
                : null,
            s.TryGetProperty("react", out var react) ? ParseReaction(react) : null)
        {
            Companions = [.. listed.Where(IsCompanion).Select(Actor)],
            RideAfter = s.TryGetProperty("after", out var rideAfter) && rideAfter.TryGetProperty("ride", out var ride) ? ride.GetInt64() : 0,
            HoldAfterMs = s.TryGetProperty("after", out var holdAfter) && holdAfter.TryGetProperty("hold", out var hold) ? hold.GetInt32() : 0,
            Cannon = s.TryGetProperty("cannon", out var cannon)
                ? new Cannon(cannon.GetProperty("interaction").GetInt64(), cannon.GetProperty("fire").GetInt32(),
                    cannon.GetProperty("quit").GetInt32(), cannon.GetProperty("shots").GetInt32(),
                    cannon.TryGetProperty("show", out var show) ? show.GetInt64() : 0,
                    cannon.TryGetProperty("shown", out var shown) ? shown.GetInt64() : 0)
                : null,
            Cues = s.TryGetProperty("cues", out var cues) ? [.. cues.EnumerateArray().Select(CueOf)] : [],
            Allies = s.TryGetProperty("allies", out var allies) ? [.. allies.EnumerateArray().Select(Actor)] : null,
        };
    }

    private static SceneActor Actor(JsonElement x) => new(x.GetProperty("npc").GetInt32(), x.GetProperty("point").GetInt64(),
        x.TryGetProperty("buffs", out var b) ? b.EnumerateArray().Select(v => v.GetInt32()).ToArray() : [],
        x.TryGetProperty("offset", out var o) ? o[0].GetSingle() : 0, x.TryGetProperty("offset", out o) ? o[1].GetSingle() : 0);

    private static Reaction ParseReaction(JsonElement r) => new(r.GetProperty("speaker").GetInt32(),
        r.TryGetProperty("start", out var start) ? [.. start.EnumerateArray().Select(v => v.GetInt32())] : [],
        r.TryGetProperty("ignored", out var ignored) ? ParseResponse(ignored) : null,
        r.TryGetProperty("complete", out var complete) ? ParseResponse(complete) : null);

    private static Response ParseResponse(JsonElement r) => new(
        r.TryGetProperty("say", out var say) ? say.GetInt32() : 0,
        r.TryGetProperty("buff", out var buff) ? buff.GetInt32() : 0,
        r.TryGetProperty("on", out var on) ? on.GetInt32() : 0);

    private static Spawn FightSpawn(JsonElement x) => new(x.TryGetProperty("point", out var point) ? point.GetInt64() : 0,
        x.GetProperty("npc").GetInt32(), x.TryGetProperty("patrol", out var patrol) ? patrol.GetInt64() : 0, OptionalInt(x, "at"),
        x.TryGetProperty("riders", out var riders) ? [.. riders.EnumerateArray().Select(RiderOf)] : [],
        x.TryGetProperty("sink", out var sink) ? [.. sink.EnumerateArray().Select(v => v.GetInt32())] : [],
        x.TryGetProperty("face", out var face) ? face.GetSingle() : null, PathOf(x));

    /// <summary>A route of the scene's own: "path" [[x, y, z], ...] in map coordinates (null: none).</summary>
    private static MapPoint[]? PathOf(JsonElement e) => e.TryGetProperty("path", out var path)
        ? [.. path.EnumerateArray().Select(p => new MapPoint(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle(), 0))]
        : null;

    private static Rider RiderOf(JsonElement r)
    {
        var offset = r.GetProperty("offset");
        return new Rider(r.GetProperty("npc").GetInt32(), offset[0].GetSingle(), offset[1].GetSingle(), offset[2].GetSingle());
    }

    private static Cue CueOf(JsonElement c) => new(OptionalInt(c, "at"), OptionalInt(c, "every"), OptionalInt(c, "chat"),
        OptionalInt(c, "speaker"), OptionalInt(c, "effect"), OptionalInt(c, "type"), OptionalInt(c, "npc"),
        c.TryGetProperty("point", out var point) ? point.GetInt64() : 0, c.TryGetProperty("patrol", out var patrol) ? patrol.GetInt64() : 0,
        OptionalInt(c, "life"), c.TryGetProperty("vanish", out var vanish) && vanish.GetBoolean(),
        c.TryGetProperty("sight", out var sight) ? sight.GetSingle() : 0, OptionalInt(c, "bubble"), OptionalInt(c, "dismiss"),
        PathOf(c), c.TryGetProperty("riders", out var riders) ? [.. riders.EnumerateArray().Select(RiderOf)] : null,
        OptionalInt(c, "hpBelow"), OptionalInt(c, "of"), OptionalInt(c, "walk"), OptionalInt(c, "cast"),
        c.TryGetProperty("burn", out var burn) ? burn.GetSingle() : 0, c.TryGetProperty("fuse", out var fuse) ? fuse.GetSingle() : 0,
        OptionalInt(c, "blast"));

    private static int OptionalInt(JsonElement e, string field) => e.TryGetProperty(field, out var v) ? v.GetInt32() : 0;

    private static IReadOnlyList<StepTrigger> Triggers(JsonElement step, string field)
    {
        if (!step.TryGetProperty(field, out var list)) return [];
        var triggers = new List<StepTrigger>();
        foreach (var t in list.EnumerateArray())
        {
            var kinds = Enum.GetValues<TriggerKind>().Where(k => t.TryGetProperty(k.ToString().ToLowerInvariant(), out _)).ToArray();
            if (kinds.Length != 1) throw new InvalidOperationException($"A scripted trigger names {kinds.Length} kinds: {t}");
            var kind = kinds[0];
            triggers.Add(new StepTrigger(kind, t.GetProperty(kind.ToString().ToLowerInvariant()).GetInt64(),
                t.TryGetProperty("radius", out var r) ? r.GetSingle() : 0));
        }
        return triggers;
    }
}
