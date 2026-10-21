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
| `ChatSpeed` | 1 | Chat pace (higher = more often) |
| `ViewCount` | 20 | Players each fake player asks to see. Real players still see the fake players, and monsters are not limited |
| `Skills` | true | Learn and cast the class's damage skills |
| `Buffs` | false | Also learn and cast self buffs (off: every buff starts a realm-wide expiry timer) |

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

## Limits

- **Map lines:** Prontera's first line takes about 200 players. Above that, new arrivals go to line 2+, as real
  players would, and a player on line 1 does not see the players on other lines.
- **Logout delay:** a removed fake player can stay listed online for up to `ReconnectGraceSeconds` (60 s) after it
  disconnects.
