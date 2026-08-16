# Notes from the 100% TMX Project fork

## Shipped features (this fork)

See the main [README](README.md) for full documentation. Short recap:

- **Replay delivery workaround**: server saves the best ghost replay and hands out an HTTP
  download link in chat after a finish (backup currently).
- **Leaderboard**: persistent finish counts, `/top`, `/rank`.
- **Impossible/hard map reporting**: `/imp` (excludes for current session + Discord alert), `/hard` (flags for review, no exclusion), backed by a shared Google Sheet refreshed periodically.
- **Curated genre presets** with instant admin switch (`/preset`) or player vote (`/votepreset`,
  `/yes`, `/no`) - Standard, Short & LOL, Lunatic, RPG, Fullspeed, Tech.
- **`/rounds`**: switches the *current* map to Rounds mode if it's a real multilap challenge
  (checked via the map's own `LapRace`/`NbLaps`, not TMX's community tag - see findings below),
  so a full finish produces a valid replay. Reverts to TimeAttack automatically on the next map.
- **Clickable manialink widgets** via `TrackMania.PlayerManialinkPageAnswer`: Yes/No vote popup,
  a clickable preset list, an auto-suggested Rounds-mode prompt on multilap maps, and a map-info
  panel (difficulty color-coded, TMX award count, author time).
- **Live status webpage**: current map + TMX preview image, active preset, everyone racing with
  live checkpoint progress, top finishers.
- **Clean shutdown** on `SIGTERM`/`systemctl stop` - no orphaned dedicated server process, no
  core dump.
  - **Excluding Unlimiter maps** Otherwise the server will be unaccessible for Vanilla Players.

## Ideas I didn't build (yet)

Roughly in the order we'd prioritize them if we kept going:

1. **Session Tracking** - For time in a session, time till session ends etc.
2. **Rate limiting on `/imp`/`/hard`** - currently unlimited per player, could be griefed against good maps.
3. **TMX outage fallback** - if tm-exchange.com is down, fall back to a small locally cached pool of last-known-good maps instead of hard-failing the fetch.
4. **Session summary on `/stop`** - maps played, finishes, top performer for that session.
5. **Admin web panel** - start/stop/skip/preset from the status page instead of only chat
   commands, with basic auth.
6. **Adding towards Unlimiter exclution**: Sometimes excluded maps still load up with missing blocks -> Vanilla players get kicked... a fix would be needed for that...
7. Load up maps by ID (and voting) to play e.g. hard maps together
6. Presets for specific upload-years.


## Technical findings (might save you time on the rewrite)

Things Claude hit and verified empirically against the live TMF dedicated server / TMX API, in case they're not already known:

- **TMX's `Routes` community tag (Multilap) often disagrees with the GBX's actual `LapRace`/
  `NbLaps` fields.** Tested 6 `Routes=1`-tagged tracks; all 6 came back `LapRace=false` in their
  real challenge info. The tag reflects level-design style (a physically looping route), not
  necessarily the technical lap configuration. If you want to detect real multilap challenges,
  check `GetCurrentChallengeInfo().LapRace`/`NbLaps`, not the TMX tag.
- **`routes=0,1,2,3` (listing all 4 possible values) returns a 500** from TMX's API - a bug on
  their end that only triggers when the filter covers every possible value. Omitting the
  parameter entirely works fine and is equivalent.
- **Manialink coordinates in TMF are roughly ±60-64 (X) / ±40-48 (Y)**, not ManiaPlanet's wider
  range - values outside that render off-screen with no error. Use `posn`/`sizen`, not `pos`/
  `size`. `textcolor`/`bgcolor` take 4-hex RGBA-ish values.
- **`SendHideManialinkPageToId` expects an int UId, not the string `id` you set in your XML** -
  passing a string throws `Value of type STRING supplied where type INT was expected`. To hide a
  specific manialink by its own `id`, send a replacement `<manialink id="...">` with an empty
  body instead (documented behavior: a previously displayed manialink with a matching id gets
  deleted when the replacement has no content).
- **Clickable manialink buttons**: `action="<int>"` on a `<quad>`/`<label>`, click comes back via
  `TrackMania.PlayerManialinkPageAnswer(playerUid, login, answer)` where `answer` is that same
  int. No built-in scoping - you dispatch on the int yourself.
- **`SetGameMode`, `SetRoundForcedLaps`, `SetNbLaps`, `SetForceShowAllOpponents` all require a
  challenge restart/change to actually take effect** - setting them mid-challenge is a no-op
  until the next `ChallengeRestart`/`NextChallenge`.
- **`HttpClient` parses the `Content-Disposition: filename=` header as Latin-1** per the historic
  HTTP header spec. A TMX map name with stylized Unicode (common) comes through as mojibake, and
  if that string gets written to disk *and* separately re-encoded as UTF-8 elsewhere (e.g. into a
  generated match settings XML), the two don't refer to the same bytes - we hit a dedicated
  server boot failure ("Track unknown") from exactly this. Stripping non-ASCII from generated
  filenames sidesteps it entirely.
- **`TrackMania.BeginRace` doesn't reliably fire for a challenge that finished loading before any
  player was connected** - observed both locally (in a truly empty session) and live (a finish on
  the very first map after boot recorded nothing and didn't auto-skip). Worth fetching
  `GetCurrentChallengeInfo()` on demand wherever you'd otherwise depend on `BeginRace` having
  already populated your state, rather than assuming it always fires before it's needed.
