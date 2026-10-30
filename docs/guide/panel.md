# Web panel

The server has a website built in: a player portal and an admin area, in the launcher's look. It runs inside the
server process on a port of its own, and every change it makes goes through the running server: a ban disconnects
the player at once, and the panel never writes the database behind the server's back.

| Who | Pages |
|---|---|
| everyone | Home (status, version, players online, uptime, accounts and characters; latest news, who's online, top 5 by level) · Who's online · Rankings · News · Items · Monsters |
| a player, logged in with the game account | My account (created, last played, characters with class, level, map and guild) · password change |
| an admin | Dashboard (the server's own metrics) · Accounts (search, detail, ban and unban) · Audit log · Mail & rewards · Gift codes · GM console · News editor · Broadcasts · Economy log |

Players log in with the same account name and password as in the game. An account the game created logs in here,
and an account made on the panel's sign-up page logs in to the game: both paths write the same record
(`AccountStore.RegisterAsync`, the code of the game's first login).

## Turning it on

It is on by default, on port **5381**, next to the server's HTTP port 5380:

| Option | Default | What |
|---|---|---|
| `Ro3Emu:Panel:Enabled` | `true` | `false` keeps the port closed |
| `Ro3Emu:Panel:Port` | `5381` | the panel's port; it must differ from the server's other ports |
| `Ro3Emu:Panel:BindAddress` | the server's | the interface it listens on; empty uses the one the other ports use (loopback for local play, every interface on a public host) |
| `Ro3Emu:Panel:TrustedProxies` | none | proxy addresses whose `X-Forwarded-For` and `X-Forwarded-Proto` are believed (see below) |
| `Ro3Emu:Panel:SessionHours` | `12` | hours a login stays valid without use |
| `Ro3Emu:Panel:Registration` | `true` | `false` hides the sign-up page; accounts are then created only by a first login in the game |

Set them in `appsettings.json` under `Ro3Emu`, as environment variables (`Ro3Emu__Panel__Port=8081`) or as
arguments (`--Ro3Emu:Panel:Port=8081`). The startup log says `Web panel on http://...`.

`RO3Emu.exe start`, `host` and `install` take `--panel-port <n>` and `--no-panel` and pass them on; so do
`server.sh` on Linux and `scripts/server.sh`. RO3Emu.exe checks the panel's port with the others before it starts the
server, so a second server on the same PC needs its own (`--panel-port 5391`) or `--no-panel`.

When you play on your own PC the panel listens on loopback only: open `http://127.0.0.1:5381`. On a public host it
listens on every interface like the game ports; open TCP 5381 in the firewall, or better, put it behind a proxy with
https as below and keep 5381 closed to the outside.

## Admins

The admin area is for the accounts listed in `Ro3Emu:GmAccounts` ([GM access](gm-access.md)), spelled as stored.
**When the list is empty, nobody is an admin in the panel**, whatever `GmOpenWhenEmpty` says: the open-GM rule of
local play applies to the game only. To administer a server, list your account even on your own PC.

Every admin action is written to the audit log (`admin_audit`: who, what, when, on which account, the reason) in the
same database transaction as the action itself, so a recorded action always happened and a performed one is always
recorded.

## Bans

A ban from the Accounts page (reason, and 1 day to 30 days or no end):

- writes the `account_ban` row ([account bans](../systems/account-bans.md)) and its audit row in one transaction;
- closes the account's game session at once: a connected client is kicked (`BE_KICKED`) and its character logged
  out, a character waiting for a reconnect is logged out, and no reconnect brings it back;
- ends the account's panel sessions;
- refuses every later login of the account, in the game and on the panel, until it ends or is lifted.

Lifting a ban deletes the row and is audited the same way.

## Admin tools

Each tool is a page of the admin area's sidebar; each change is written to the audit log, with the admin's name.

- **Mail & rewards.** In-game mail from the server (sender name, title, text) with up to 10 items and Zeny (item 1)
  attached, to one character, all characters of an account, or everyone (optionally only those in the game, or from a
  base level up). It goes through `MailService.SendRawAsync`, as `!mail` does: stored first, then announced, so a
  player in the game sees it at once and the others when they log in. Item ids are checked against the item table.
  A mail to more than one character is sent only after the admin confirms the recipient count the page showed.
- **Gift codes.** The list of codes with their rewards, uses (counted from `gift_code_redemption`), limits, expiry and
  state, and the switch to disable or enable one. New codes are typed (up to 500, letters, digits, `-` and `_`) or
  generated (up to 500 random codes with an optional prefix, without the look-alike characters 0, O, 1, I, L), with
  rewards, max uses, uses per account and optional start and expiry. All the codes of one request are created or none.
  The server writes `gift_code` through its writer ([gift codes](../systems/gift-codes.md)).
- **GM console.** Any `!` chat command ([GM access](gm-access.md)) run as the admin's own character, or for any
  character in the game, with the replies the command sends shown on the page; arrow up and down recall earlier
  lines. It runs on the game loop like `POST /ro3emu/gm`. The admin is the authority: the chosen character's account
  needs no GM rights. The page lists the admin's past commands (from the audit log).
- **News editor.** Create, edit, delete and pin news (category news, event, maintenance or patch, and a publish time;
  a time in the future keeps an item hidden until then). The public News and Home pages show pinned items first.
- **Broadcasts.** A one-line notice on the screen of everyone in the game: the GM marquee (`BROADCAST_NOTIFY` 83270,
  the message GM replies use). It can repeat every few minutes, up to 100 more times; repeats are kept in the
  server's memory (a restart ends them) and can be cancelled.

## Items, monsters and the economy log

**Items** and **Monsters** are public databases built from the server's own game data and English texts the first
time someone opens them, then kept in memory. An item shows its quality, type, level, trade rule, stats (equipment
and cards), the monsters that drop it and the shops that sell it. A monster shows the HP and stats the server gives it
(AttriMonsterConfig scaled by the NPC's `_kAttriScale`, as `MonsterService` does), its drops with the chance per kill
that at least one drops (from the pools `RewardService` rolls, outside bonus time and without a player's level and
class limits), and the maps whose scene editor spawns it, with how many stay alive and the respawn.

The admin's **Economy log** reads the server's ledger (`economy_entry` with its `operation_receipt`) on the read
pool, newest first: filter by account, character, item (an id, or part of a name) and a range of UTC days. The totals
add up everything the filter matches; they only mean something for one item or currency.

## Behind nginx with https

The panel speaks plain HTTP. On a public server put a TLS-terminating proxy in front of it, bind the panel to
loopback and tell it to trust the proxy:

```bash
Ro3Emu__Panel__BindAddress=127.0.0.1
Ro3Emu__Panel__TrustedProxies__0=127.0.0.1
```

`Ro3Emu:Panel:BindAddress` moves only the panel; the game ports keep the server's address. With a trusted proxy
the panel takes the client's address from the last entry of `X-Forwarded-For` (for the login limit) and marks its
cookies `Secure` when `X-Forwarded-Proto` is `https`. From any other address both headers are ignored.

```nginx
server {
    listen 443 ssl http2;
    server_name panel.example.com;
    ssl_certificate     /etc/letsencrypt/live/panel.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/panel.example.com/privkey.pem;
    add_header Strict-Transport-Security "max-age=31536000" always;

    location / {
        proxy_pass http://127.0.0.1:5381;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Keep `Host` as the browser sent it: the panel refuses a POST whose `Origin` names another host. Forward only the
panel's port. The server's own HTTP port (5380) has loopback-only operator endpoints that a proxy on the same machine
would open to everyone ([security model](../systems/security.md)).

Logins share the server's credential budget (`Ro3Emu:LoginAttemptsPerMinute` per address, the same counter as the
game's login). Behind a trusted proxy that is per player address; without one, every player behind a proxy shares the
proxy's.

## Security notes

- **Sessions.** A random 256-bit id in an `HttpOnly`, `SameSite=Strict` cookie, `Secure` over https. Sessions live in
  the server's memory: a restart logs everyone out. A login always issues a new id. A password change signs out the
  account's other browsers, a ban all of them.
- **CSRF.** Every POST carries a token bound to the session cookie (before a login, to a visitor cookie) and is
  refused without it, or when its `Origin` is another site.
- **Passwords** are checked and stored by the server's own code (PBKDF2-SHA256, 100 000 rounds). A wrong password, an
  unknown account and a banned account get the same answer. The panel never logs a password.
- **Sign-up.** The game accepts any account name that is not empty once trimmed. A sign-up on the panel also refuses
  names over 64 characters or with control characters and passwords under 6 or over 128 characters (the panel's own
  rule, not the game's). A name that is taken in any letter case, or banned, is refused.
- **Pages.** No inline script or style, so the `Content-Security-Policy` allows only the panel's own files;
  `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: same-origin`. Every value from the server reaches the page as
  text, never as HTML. Request bodies are capped at 16 KB; API answers are not cached.
- **Data.** Who's online shows character names, classes, levels and maps to everyone; the account behind a
  character only to admins.
- **Load.** The online list is read on the game loop at most once every 5 seconds, the level board from the database
  at most every 30; the other boards are RankService's own cache. Heavy traffic does not reach the loop.

## What it stores

| Table | What |
|---|---|
| `admin_audit` | one row per admin action |
| `panel_news` | the news the public pages show, written with the news editor; `pinned` keeps an item on top |

Both are created by the server at startup like its other tables and are part of every backup.

## For developers

`src/Ro3Emu.Panel` holds the website: the static files (`www/`, embedded in the assembly; the fonts are the
launcher's; the colours, spacing scale, type scale and components are the tokens at the top of `app.css`), the JSON API under `/api/` (`PanelSite`), sessions and CSRF. It knows nothing of the game: everything
goes through `IPanelBackend`, which the server implements in `Http/PanelBackend.cs` over its stores, writer and loop.
`ServerHost` routes every request that arrives on the panel's port to `PanelSite` before the server's own
middleware, so neither port serves the other's paths. Tests: `PanelTests`; the test hosts keep the panel off unless a
test turns it on with a free port (`TestHostDefaults`).
