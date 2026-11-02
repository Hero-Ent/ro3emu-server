# Fake players (`!fakeplayer`)

Fake players fill the world during the beta and double as a stress test. Each one is a real protocol client: it logs
in, owns a character with a real-looking name, class, hair and stats, counts as online (`/ro3emu/status`, the
launcher, `!online`), walks, talks in map chat, hunts, dies and revives.

## GM command (GM/admin only)

| Command | Effect |
|---|---|
| `!fakeplayer 1000` | Keep 1000 fake players online (more or fewer than now: adds or removes the difference) |
| `!fakeplayer 0` | Remove all of them (they log out within seconds) |
| `!fakeplayer status` | Target, fake players online on the server, and the process counters (online, in town, hunting, dead, deaths, revives, attacks, chats, failures) |
| `!fakeplayer stop` | Remove them and stop the fake player process |

Like every `!` command it is refused to accounts that are not GM (`Ro3Emu:GmAccounts` or website group ≥ 10,
docs/guide/gm-access.md). `!online` shows the total, how many are fake, and the real players by name.

## What they do

The same number always gives the same player (name, sex, hair, class, level, behaviour):

| Share | Behaviour |
|---|---|
| ~30% | Walk around Prontera |
| ~15% | Stand still in Prontera (and talk more) |
| ~20% | Walk to a spot in Prontera, stay there a while, walk again |
| ~35% | Hunt on a field map that fits their level (Prontera/Geffen/Payon/Morroc fields up to Orc Village, Larva Canyon, Payon Forest...), revive after dying |

- **Levels and classes:**

  | Share | Class | Levels |
  |---|---|---|
  | ~10% | Novice | 1–9 |
  | ~22% | first class | 10–39 |
  | ~30% | second class | 40–74 |
  | ~18% | transcendent (Lord Knight, High Wizard...) | 60–89 |
  | ~20% | third class (Rune Knight, Warlock, Ranger, Archbishop, Guillotine Cross, Mechanic, Royal Guard) | 70–100 |

  Job levels follow the base level.
- **Skills:** their skill points go into their class's attack skills, which they cast between normal hits (bolts and
  Thunderstorm for mages, Bash and Magnum Break for swordsmen, Double Strafe and Arrow Shower for archers...).
- **Targets:** the fake players share which monster each one is on, so a group spreads over the spawns.
- **Stats:** all their points go into their class's usual build.
- **Chat:** about 60% of them talk in map chat now and then, in Portuguese (`alguem pt?`, `vendo carta poring`...).
- **Accounts:** `fp_00001`, `fp_00002`... The server creates them the first time. Their password is random and is
  kept in `<DataDir>/fakeplayers.key`; deleting that file makes the existing fake accounts unusable.
- **Character names** stay reserved in the database once created.

## How it works

`FakePlayerService` (server) registers the missing accounts, then starts the `Ro3Emu.FakePlayers` process. It looks
for it in:
1. `Ro3Emu:FakePlayers:Executable`, if set.
2. `fakeplayers\` next to the server.
3. The repository's `src\Ro3Emu.FakePlayers\bin\{Release,Debug}\net10.0` (the newest of the two).

The service steers the process over stdin/stdout (`target <n>`). When a player reports `@ready`, the service applies
its class, levels and stats with the same code as the GM commands. The players connect to `127.0.0.1`, which is exempt
from the per-IP connection and login limits, and run in their own process, so their client work does not take the
server's CPU.

Build it once (`COMPILAR_DEBUG.cmd` does it too):

```
dotnet build src/Ro3Emu.FakePlayers -c Release
```

Options (`appsettings.json` or `--Ro3Emu:FakePlayers:...`):

| Option | Default | Meaning |
|---|---|---|
| `Executable` | (search) | Path of `Ro3Emu.FakePlayers(.exe/.dll)` |
| `MaxBots` | 100000 | Most one command may ask for |
| `AccountPrefix` | `fp_` | Fake account names: prefix + 5 digits |
| `LoginConcurrency` | 12 | Logins in flight at once (each one is a password hash on the server) |
| `LoginGapMs` | 1000 | Milliseconds between two logins (0: none). Each login loads, the first time creates, a character on the game loop: 140 at once left real players' requests unanswered until their clients reconnected. 1000 fake players take about 17 minutes to arrive |
| `ChatSpeed` | 1 | Chat pace (higher = more often) |
| `ViewCount` | 20 | Players each fake player asks to see. Real players still see the fake players, and monsters are not limited |
| `Skills` | true | Learn and cast the class's damage skills |
| `Buffs` | false | Also learn and cast self buffs (off: every buff starts a realm-wide expiry timer); never a hiding one |

## Load: what the server does differently for fake players

- **Profile at login.** Class, levels, skills and stats are written while the role loads (the role select already
  owns it). An earlier version applied them through realm-wide operations after login. Each such operation waits
  for the whole world to be idle, so with hundreds arriving at once the server stalled.
- **Little to see, fewer steps.** Each fake player sees 20 players and walks in 3 m steps every 0.6 s. Every step
  is sent to everyone who sees the walker, so this traffic grows with the square of the crowd.
- **Spread out.** Town players spread over six towns. Hunters take the field of their level band with the fewest
  hunters, and move on when it is crowded or when they keep dying.
- **Damage skills only.** Fake players cast only skills that leave no buff, no debuff and no energy cost behind.
- **Server fixes.** The energy recovery notice and the card library sync were realm-wide operations. They now run
  under the player's own role, which also helps real players on a busy server.
- **Remaining limit: the revive.** The revive is still realm-wide and waits under heavy load. To die less, hunters
  break off at 35% HP and rest.

Measured with `StressWithManyFakePlayers` (Debug server, 28 cores):

| Fake players | All online after | Game loop busy | Queue at the end | RAM server / bots |
|---|---|---|---|---|
| 300 | 20 s | 51% | 19 | 1.4 GB / 0.8 GB |
| 1500 | 201 s | 95% | 104 | 4.2 GB / 2.5 GB |

The game loop is single-threaded and is the limit: about 1500 active players is its ceiling on this hardware.

## Stress test

`tests/Ro3Emu.Tests/FakePlayerTests.cs` has an opt-in run. It starts a server with the real maps, asks for N fake
players and prints every 10 s:
- fake players online and the process counters;
- the game loop's busy ratio and queue;
- CPU and memory of the server and of the bot process.

The report is also written to `.loadtest/fakeplayers-<N>-<time>.txt`.

```
$env:RO3EMU_FAKE_STRESS="1000"; $env:RO3EMU_FAKE_STRESS_SECONDS="300"
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~StressWithManyFakePlayers" --logger "console;verbosity=normal"
```

On a live server, `!fakeplayer <n>` plus `GET /ro3emu/metrics` (loopback) measure the same thing.

## Guerra simulada (`!gvg`, GM/admin only)

A full guild war against fake players, to test the castle siege (scene 2230101) end to end: two guilds of fake
third classes fight it with skills, cannons and vehicles, and real players can join either side.

| Command | Effect |
|---|---|
| `!gvg iniciar` | 70 fake players per side (`!gvg iniciar 60` for 60), city 1001 (`!gvg iniciar 70 1002` for another) |
| `!gvg iniciar 70 1001 minha=atacante` | Your own guild is the attacker (or `minha=defensor`): fake players fill it up to 70 members, and only the other side gets a fake guild |
| `!gvg entrar atacante` / `!gvg entrar defensor` | You apply to that side's guild (it accepts at once) and get its cannon permission (Firearm slot 9 or 10); then Activities → Royal City War → go to battle |
| `!gvg maquina veiculo` / `!gvg maquina canhao` | Your war machine permission in your guild becomes a vehicle (or a cannon) one: the siege battle bag then lists those machines |
| `!gvg puxar 20` | 20 more fake players join your guild (`!gvg puxar 20 atacante`, `defensor` or a guild id for another one) |
| `!gvg status` | Both guilds (members/maximum) and the fake players' counters: online, in a guild, in the siege, entries, deaths, skills, heals, cannon shots, machines boarded |
| `!gvg parar` | The war fake players log out and the test siege closes |

What `!gvg iniciar` does, in order (a few minutes; `!gvg status` follows it):
1. Registers the accounts `fp_90001`… (attackers) and `fp_91001`… (defenders) and starts the fake player process.
2. Player 1 of each side founds its guild (`Ataque` / `Defesa`) through the real protocol (32210) and opens it to
   applications with auto-accept (32230 type 10); the others apply (32223) and join at once. The guild cap is 80, so
   70 per side leaves 10 places for real players.
3. Runs the siege test aids `!siege close`, `!siege seed <city> <attacker> <defender>` and `!siege open`, and fills
   both war warehouses (50 cannons, 10 Madogears, 6 tanks, 6 Peco Pecos, 10 repair kits; never lowered).
4. The fake players enter the siege (30400, six at a time) and fight it.

The fake players log in one per `LoginGapMs` (1 s), so 140 take a little over two minutes. They wait for the siege in
Alberta (10600), not Prontera, so their arrival does not land in the real players' view or map chat; there each walks to
a spot of its own up to 60 m away and mostly stands there, silent (they talk in the siege only). If the GM reconnects meanwhile, the war's next
steps and answers go to the GM's new session (they also go to the server log, `Guild war: ...`).

**The fake players** are third classes at the server's top base and job level (Rune Knight, Royal Guard, Guillotine
Cross, Mechanic, Warlock, Ranger and Archbishop, about 2:2:1 front line, ranged and healers), with all their attack
skills and self buffs learned. No fake player learns or casts a hiding buff (BuffConfig category 31): Disguise, the
Assassin line's, hid it from the other camp and left it translucent for its own, so it looked invisible. In the siege each one:
- fights as its camp (1 attackers, 2 defenders): enemy players first (within 24 m; hurt ones and the camp's focus
  target first: an enemy below 45% HP becomes everybody's target for 6 s), then castle units (crystal, ram and ladders,
  guardians, barricades, archers, enemy machines; the City Gate last, it is the ram's work);
- casts its skills, a third class's own skills (rank 5) four times as often as first-class ones and area skills three
  times as often on a crowd; ranged classes and healers keep their distance;
- Archbishops heal themselves below 70% and the most hurt ally within 20 m;
- steps back below 30% HP with an enemy close, revives after death and goes back to the front;
- walks around the standing air walls (var 133 of 30401) as the real client must, so nobody passes a closed wall;
- follows the stages: attackers gather in their camp (Prepare), then go to the ladders and the gate (the middle lane
  escorts the Battering Ram, which only moves with an attacker within 32 m), the Knight Guardians and the crystal;
  defenders hold the wall walk and the gateway, then the guardians and the crystal;
- players 2–9 of a side are **gunners**: player 1 gives them Firearm slots 1–8 (32282), they build a cannon (32270 /
  32271) at a spot in range of the stage's fight, board it (32700) and fire ballistic volleys (32703, gravity, speed and
  direction computed to land on the target) at enemy crowds, castle objects or where the other side gathers;
- players 12–19 are **drivers**: Vehicle slots 1–8, they board a free vehicle of their camp (a fixed Madogear or their
  own, 32281 from the warehouse otherwise) and drive it into the fight with the seat's skills.

Slots 9 and 10 of each machine type stay for real players: `!gvg entrar` and `!gvg maquina` put you in one (the server
does what the leader's Tactics panel would, since the leader is a fake player). A member holds one slot at a time, so
switching to a vehicle gives up the cannon (and the cannon you built).

In `minha=` mode or with `!gvg puxar`, nobody fake gives the machine slots of your guild: give them yourself in the
guild's Tactics panel if you want the fake gunners and drivers there to use machines. If your guild does not
auto-accept, the server approves the fake players' applications in your name.

The fake players use no GM shortcut: their guild, siege and machine actions are the real client messages, so what
works for them works for players. Accounts `fp_90001`–`fp_99999` are reserved for the war and never
used by `!fakeplayer`.

## Limits

- **Map lines:** Prontera's first line takes about 200 players. Above that, new arrivals go to line 2+, as real
  players would, and a player on line 1 does not see the players on other lines.
- **Logout delay:** a removed fake player can stay listed online for up to `ReconnectGraceSeconds` (60 s) after it
  disconnects.
