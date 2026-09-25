# CORE Control

A BepInEx mod for **Nuclear Option** that adds RTS-style command gameplay on top of the base
game: a free camera, unit selection, orders, group control, ground/naval production and an
air-tasking layer. Intended mainly for the **Escalation** and **Terminal Control** game modes,
and it ships with a 1v1 base-against-base mission of its own, **CORE Control Duel**, in two
versions: the original close-quarters one and a far-start one with the two sides at opposite
corners of the map.

CORE Control is a fork of **Ground Control (RTS)**; see [Credits](#credits) for the upstream
projects it builds on.
## Requirements

- Nuclear Option
- BepInEx 5
- BepInEx Configuration Manager (optional, for editing settings outside the game)

## Installation

1. Download `COREControl.zip` from the latest release.
2. Steam → right-click Nuclear Option → Manage → Browse local files.
3. If there is no `BepInEx` folder, install BepInEx 5 first.
   CORE Control does not load while **Ground Control (RTS)** (`GroundControlRts`) or
   **Nuclear Option Commander** (`NuclearOptionCommander`) is loaded: all three patch the same game
   code, so every patch would run twice. Move those folders out of `BepInEx\plugins` (for example
   to `BepInEx\disabledPlugins`) to play CORE Control; the log names the one that blocked it.
   Nothing else is shared — each mod has its own folder, config file, missions and saves.
4. Copy the `COREControl` folder from the zip into:

```
Nuclear Option\BepInEx\plugins
```

Copy the whole folder, not just the DLL: the missions that ship with the mod sit next to it
and are installed into your mission list the first time the game loads the plugin.

---

## Quick start

| Action | Default |
| --- | --- |
| Enter/leave RTS mode | `CMD` button on the left edge (while outside an aircraft) |
| Move camera | `W` `A` `S` `D` `Q` `E`, hold `Shift` to boost |
| Zoom | Mouse wheel (moves toward the cursor) |
| Look around / orbit | Hold `MMB` |
| Select a unit | `LMB` |
| Add to selection | `Shift` + `LMB` |
| Remove one unit from the selection | `Shift` + `LMB` on a selected unit |
| Select every unit of that type on screen | Double-click a unit |
| Select every unit of that type, anywhere | `Ctrl` + `LMB` |
| Box select (3D view) | Drag `LMB` |
| Box select (map) | `Ctrl` + drag `LMB` — a plain drag pans the map |
| Travel point | `RMB` |
| Multi-point route | Hold `Shift` and `RMB` each point in order |
| Attack order | `RMB` on a hostile unit, structure or objective |
| Guard order | `RMB` on a friendly unit |
| Stop and hold the selection | `X` |
| Jump to the next unit with no orders | `.` |
| Recall control group | `1` – `9` |
| Store control group | `Ctrl` + `1` – `9` |
| Recall camera view | `F1` – `F4` |
| Store camera view | `Ctrl` + `F1` – `F4` |
| Centre camera on selection | Tap `Space` (hold to centre and follow) |
| Fullscreen map | `M` |
| Cycle UI visibility | `H` |

Every binding is remappable in **CMD → Settings → Controls**, and they only apply while
RTS mode is active — aircraft controls are never touched.

**CMD → Settings → Shortcuts** lists every shortcut in the mod in one scrollable reference,
including the ones that are not remappable (control groups, camera bookmarks, double-click).
It reads the live bindings, so it shows your keys, not the defaults.

---

## Command and control

### Selection

- Click units in the 3D world, on the tactical map, or in the **Order of Battle** window.
- **Box select** by dragging the left mouse button in the camera view. On the map a plain drag
  pans, so hold `Ctrl` while dragging to draw a box there.
  A box that catches any friendly unit selects only friendlies.
- **Order of Battle** (`CMD → ORDER OF BATTLE`) lists every unit the faction owns, filtered by
  ground / air / naval / structures, plus tracked hostiles. Select one, select all, or recall a
  control group from the same window. Clicking a row also puts the camera on that unit.
- **Select by type**: double-clicking a unit selects every unit of that type currently on
  screen; `Ctrl` + click selects every one the faction owns, wherever it is.
- **Shift-click a unit that is already selected** to drop it, instead of redrawing the whole box.
- **Type chips**: a mixed selection shows one chip per unit type in the selection bar. Click a
  chip to narrow the selection to that type, or `Shift` + click it to drop that type.
- **`X` stops** the selection where it stands, without going to the selection bar.
- **`.` cycles idle units**: it selects and jumps to the next friendly ground or naval unit
  that holds no RTS order, so vehicles left at a depot or parked at the end of an old
  route are easy to find.

### Control groups

- Nine groups. `Ctrl` + `1`–`9` stores the current selection, `1`–`9` recalls it, `Shift` + a
  number adds the group to the current selection.
- A unit belongs to exactly one group, so recalling a group is unambiguous.
- Any order given to a selection applies to every unit in it, so a group is ordered as one.
- The current group is shown in the selection bar and as a `[n]` badge in the Order of Battle.

### Travel routes

- A single `RMB` replaces the route with one travel point.
- Holding the queue key (`Shift` by default) appends points, so you can plan a full path:
  the unit drives to point 1, then 2, then 3.
- Routes are drawn as numbered markers and lines in the 3D view **and** on the tactical map.
- Multiple selected units spread into a formation around each point instead of stacking.
- A unit that cannot reach a point (blocked, bad terrain) gives up after 60 s and moves on to
  the next one rather than stalling the whole route.
- Routes keep running when you leave RTS mode, so a convoy still arrives while you fly.
- Every order flashes a marker at the point that was ordered, in the 3D view and on the map, so
  a swallowed click is obvious. Toggle in **Settings → Gameplay**.
- **PATROL** turns a multi-point route into a loop. The unit keeps walking it while you are
  away flying, instead of parking at the last point.
- **Formations** are picked from the selection bar: ring, line, column or wedge, oriented along
  the direction of travel so the group arrives facing the right way. Column is the one that
  matters on roads.
- **Arrive together**: a unit that gets more than the cohesion distance ahead of the rearmost
  member of its order waits for it, so a group does not string out along the route.
- **Waypoint actions**: the `WP` button attaches an action to the *next* travel point you place
  — hold for a set time, radar off, or radar on. EMCON at a waypoint lets a battery drive to its
  firing position dark and only light up where you tell it to.

### Stances

Cycled from the selection bar, per unit, and they stick whether or not the unit has an order.

- **Free Fire** (default): shoots freely, and **attack-moves** — while travelling it breaks off
  to engage a hostile that comes inside its own weapon range, then resumes the route once the
  target is dead or has broken contact. Toggle the attack-move part in **Settings → Gameplay**.
- **Hold Fire**: turrets acquire nothing, so the unit stays quiet near a SAM belt. Radar-guided
  batteries fed by a fire-control truck also need the radar switched off in Unit Systems.
- **Hold Pos**: holds the ground it stands on and ignores travel orders, but still shoots.

### Guard and retreat

- `RMB` on a friendly unit — in the 3D view or on the map — tells the selection to escort it in
  formation and engage whatever shoots at it. Toggle in **Settings → Gameplay**.
- **RETREAT** sends the selection to the nearest friendly vehicle that can repair or rearm it.
  Turn on automatic retreat in **Settings → Gameplay** to have damaged units pull back on their
  own once they drop below the condition threshold.

### Attack orders

- `RMB` on a hostile unit, building or objective issues an attack order. Anything hostile can
  be targeted, including structures that cannot normally be selected.
- If the last point of a queued route lands on a hostile, the whole route becomes an approach
  and the unit attacks after walking it.
- Ordered units drive to roughly 70 % of their own weapon range and hold there, instead of
  driving onto the target. This can be turned off in **Settings → Gameplay**.
- Turrets on an ordered unit keep the commanded target as long as their own fire control
  considers it engageable, so a group focuses fire instead of scattering.
- The order re-tracks: if the target drives away, the attackers follow.
- When the target dies, the order does not: the attackers pick the nearest hostile the faction
  can see inside their own weapon range and keep firing. If nothing is in reach they hold the
  ground they took instead of reverting to the base-game AI and driving off. Toggle in
  **Settings → Gameplay**.

### Alerts and the battle log

- **Combat alerts** appear as clickable toasts in an `ALERTS` window in the top-right corner:
  `GROUP 3 UNDER ATTACK`, units lost, kills scored, reinforcements ready. Drag its title bar to
  move it anywhere (**Settings → Reset UI layout** puts it back). They are drawn **while you are
  flying too**, which is the point — otherwise you never find out you are losing units. Clicking
  one selects the unit and jumps to it. Toggle in **Settings → Gameplay**.
- **Base captures** raise their own toast the moment an airfield changes hands, anywhere on the
  map and either way round: `CAPTURED MARIS AIRPORT`, `LOST K92 HIGHWAY STRIP`,
  `PRIMEVA TOOK SANDRIFT AIRBASE`. These ignore the combat-alerts toggle — losing a base is not
  something to find out about later.
- **Battle log**: the `LOG` tab in the Order of Battle keeps the last events with mission
  timestamps. Click an entry to jump to the unit.
- **Condition and ammo**: the selection bar shows condition, ammo and the current order for the
  selection, and every Order of Battle row carries condition and ammo, so the supply layer is
  visible instead of invisible. Aircraft add a fuel reading.
- **Loadout**: with exactly one unit selected the bar lists what it is carrying - every weapon by
  name with rounds remaining, `R-27ER   2 / 4` - merged to one row per weapon type rather than
  one per pylon. Fuel only exists on aircraft in this game, so ground units and ships show no
  fuel reading.

### Aircraft

- Selected friendly AI aircraft accept the same travel points and attack orders. Aircraft are
  not directly commandable in the base game, so orders are executed through the Air Command
  mission layer, which drives the AI pilot.
- Ordering an untasked aircraft tasks it automatically: an attack order on an aircraft or
  missile becomes an Air Superiority mission, anything else becomes CAS, and a plain travel
  point uses the mission type currently selected in Air Command.
- Aircraft that are already in the air can be given a mission from **Air Command → AIR
  MISSIONS → IDLE**: pick the mission type, press `TASK`, and place the mission area on the
  map exactly as if you had just spawned it.
- An aircraft only acts on a travel point while it has no target of its own, and only once its
  AI pilot is in its combat state (not while taking off, taxiing or landing).
- **RESUPPLY**, on the selection bar whenever the selection holds aircraft, sends them to the
  nearest airbase your faction holds and lands them there. Landing is how the game recovers an
  airframe — it goes back into your stock with its cost refunded, ready to relaunch fully armed
  and fuelled — so this is the rearm, not a refuelling truck. The route to the field each aircraft
  picked is drawn as an ordinary yellow travel line, so you can see where they are going.
  Right-clicking aircraft onto a base you already own does exactly the same thing.
- **Right-clicking aircraft onto a base you do not own is a capture order**: they fly there, land,
  and hold the ring. See *Capturing bases*.
- **AI aircraft you launch enter the map already airborne over the base you launched them from**,
  at flying speed and pointed at their mission area, rather than taxiing out of a hangar. The
  game's AI pilot cannot reliably get off a highway strip — the taxi and takeoff states answer any
  scrape or stall by ejecting the pilot, which is why commander-launched aircraft used to die a few
  metres from the hangar while the ones you fly yourself never do. Which airframes a base offers is
  unchanged, and it still has to be a base that accepts the type.

---

## Camera

- Free camera with 3D unit selection. Pan speed scales with how high you are, so the camera nudges
  down among the vehicles and crosses the map from survey height.
- **Zoom on the mouse wheel**, toward whatever the cursor is over, so zooming in also recentres on
  the thing you were pointing at.
- **Hold `MMB` to orbit** the point under the cursor, so what you were studying stays on screen.
  Prefer turning in place? **Settings → Camera**.
- **Follow without the yank**: selecting a unit follows it but leaves your view where you put it.
  The camera only travels when the unit is off screen or too far away to read, and then it glides
  there rather than cutting. Selecting several units frames the whole group so a convoy fits on
  screen. Both toggles live in **Settings → Camera**.
- `Space` centres on the selection immediately; hold it to centre and follow.
- **Optional edge scrolling** (**Settings → Camera**, off by default).
- Pan speed, zoom speed, look sensitivity, smoothing and map drag speed are all live sliders in
  **Settings → Camera**.
- **Camera bookmarks**: `Ctrl` + `F1`–`F4` stores the current viewpoint, `F1`–`F4` jumps back to
  it. Useful for your front line, your airbase and your carrier. Toggle in
  **Settings → Gameplay**.
- POV camera attached to the selected unit, with snapping to the head position of available
  crew members.

## Map

- Movable tactical minimap, kept open alongside the RTS UI.
- **Resizable**: drag the grip in the bottom-right corner of the map. The size is saved.
- `LMB` clicks icons, drag `LMB` (or `MMB`) to pan the map, `Ctrl` + drag `LMB` draws a
  selection box, and the base-game zoom and keyboard-pan bindings still work.
- `RMB` sets travel points and attack orders, same rules as the 3D view.
- Selected units draw their full remaining route on the map, numbered in the order they will
  drive it.
- Radar coverage overlay generated from Unit Systems.
- **This is the only map the mod asks you to work on.** Air mission areas and naval rally points
  are placed on it, so no order ever swaps the screen out for the game's fullscreen map and back.
  `M` still opens the fullscreen map when you want it, and `Esc` comes back.

---

## Production and logistics

### Depot spawning

- Buy ground units with the faction money pool, or deploy vehicles from the faction reserve.
- Vehicles are grouped by their base-game categories.
- New vehicles stage beside the depot until ordered; optional rally points override that, plus a spawn queue.
- **Reinforce group**: set a depot to put every unit it builds straight into a control group,
  so a battlegroup rebuilds itself without re-boxing it every time.
- Faction reserve system that holds certain unit types back after a factory produces them.

### Supply heli

- Custom supply runs with cargo-capable helicopters and configurable cargo loadouts.
- Landing deliveries or parachute airdrops, with landing-zone selection in 3D.

### Naval

Nobody puts a ship in the water without a **naval dock** — not you, not the enemy commander.

- Build one from `CMD → BUILD → ECONOMY`. It has to stand on dry land at the **water's edge**, and
  it is the one building allowed further out than the ordinary build radius: it gets its own,
  larger one (12 km by default), because the coast usually is.
- It upgrades three times, and each level opens a heavier class of hull:

  | Dock level | Unlocks |
  | --- | --- |
  | 1 | Patrol boats, landing craft |
  | 2 | Corvettes, frigates |
  | 3 | Destroyers, carriers, assault ships |

- Hulls you cannot afford the dock for stay on the naval list with the level they need written
  next to them, so the ladder is something to build toward rather than a list that mysteriously
  grows.
- **Purchased ships enter at the sea lane nearest your dock** and sail to the rally point you pick
  on the fullscreen map. The dock is the harbour, so a hull arrives beside the thing that paid for
  it rather than somewhere on the map edge.
- Naval resupply missions for selected ships are unchanged and need no dock.

If a faction on your map cannot reach the coast within 12 km of anything it holds, raise **naval
dock radius** in `Settings → Gameplay` (or `Economy / NavalDockRadiusKm` in the config file) —
capturing a base nearer the sea works too.

### Air Command

Dispatch aircraft with custom loadouts on a specific mission:

- **Air Superiority** and **AWACS / Jammer** stay inside their assigned area (blue circle) but
  engage anything in range.
- **CAS**, **ARAD** and **Strike** only attack targets inside their assigned area (red circle).
- ARAD supports saturation attacks; missions in progress can be edited, relocated or recalled.
- Opening **AIR COMMAND** brings the tactical map up beside the window, and mission areas are placed
  on that map or in the 3D view. Right-click, or the game's Cancel binding, backs out.
- Aircraft come from the faction reserve when possible, otherwise they are purchased. Aircraft
  that return successfully restore the airframe or refund the money.
- **Fixed-wing only.** Everything that steers a commanded aircraft is built on the game's aeroplane
  pilot AI; helicopters and VTOLs fly a different one entirely and cannot be given a mission from
  here — by you or by the enemy commander. Helicopters do their own job through the supply heli
  missions instead, and the enemy commander buys them and lets the game's own helicopter AI fly
  them. The one airframe kind the enemy will not buy is one whose pilot the base game gives no AI
  flight state to at all — it would simply fall out of the sky. Which aircraft that actually covers
  is data inside the game's asset files, so the mod reads it at runtime rather than guessing, and
  writes the whole list to the BepInEx console once per mission: pilot type, role and price for
  every airframe, with anything it refuses to buy marked.

### Build (economy)

`CMD → BUILD` opens the build menu. It has three tabs — **ECONOMY**, **STRUCTURES** and
**REPAIR**. Everything in all three is paid for out of the faction money pool, and the enemy
commander builds, upgrades and repairs under exactly the same rules and prices.

Placement works the same everywhere: buy, then click the site in the 3D view. A see-through copy
of the building follows the cursor — **green** where the ground is clear, **red** where it is
not, with the reason in the window's status line. A site is refused when it sits on a road, when
it sits on **any airbase's runway or taxiway** (a strip with a refinery on it is a strip nothing
can land on), when it overlaps another unit or building, or when it is **more than 2.5 km from an
airbase your faction holds** — you build around the bases you have taken, so taking ground is what opens up
somewhere new to build. Trees, rocks and scenery are ignored, because clearing those to build is
normal. Buildings you place land unrotated, so what the preview shows is what you get. The enemy
commander is held to all the same rules, and the radius is the **build radius** slider in
`Settings → Gameplay` (`Economy / BuildRadiusKm` in the config file).

The **naval dock** is the single exception: it has its own, larger radius and its own shoreline
rule, because a dock that cannot reach the sea is not a dock. See [Naval](#naval).

Hold the repeat key (Left Shift by default) while clicking to stay in placement mode and site
another one; right-click or Escape backs out.

**Anything you build can be selected** — clicked, or caught in a box select, like a vehicle.
Its unit panel carries the level and upgrade button for a mine or factory, and a **DESTROY
BUILDING** button that asks for a second click and gives no refund. (Buildings you did not build
stay unselectable; the map would be unusable otherwise.)

#### ECONOMY

- **Gold mine.** Buy one, then click a spot on the ground in the 3D view to site it. It looks
  like an ordinary industrial building and it pays your faction a standing income for as long as
  it is standing — so it is worth defending, and an enemy one is worth bombing.
- **Factory.** Pick what it should build with the `PRODUCES` arrows — the list is your
  own faction's ground vehicles — then buy it and click a site, the same way as a mine. From
  then on it drops that unit into the faction reserve every production cycle, and your depots
  deploy it. Both the product and the cadence are fixed the moment it is built, so choose
  before you site it. Selecting a built factory shows the time left on the current run and how
  long a run takes — `NEXT 2 x AGM IN 3:12 (EVERY 4:00)` — above the upgrade button.
- **Naval dock.** The gate on every ship purchase, for both commanders. Site it on dry land at
  the water's edge; three levels, each opening a heavier class of hull. See [Naval](#naval).
- **Three levels, for mines, factories and docks alike.** Upgrading a mine raises its income;
  upgrading a
  factory raises how many units it drops into the faction reserve each production cycle, from one
  per cycle up to three.
- Levels last as long as the building does. Destroy the mine or the factory and the investment
  goes with it.

#### STRUCTURES

Every other building the game ships, buildable for a price: radars, depots, hangars, bunkers,
ammunition dumps, industry, civilian structures. They are listed under the same categories the
game files them under, and each one does whatever its own prefab does — a radar you build sees
for you, a depot you build supplies for you, a hangar services aircraft.

Prices are not a table in the mod. Each building costs what the game itself values it at, times
`BuildingCostMultiplier`, so the ladder stays sane and a game patch that adds a building adds a
row without any work here.

#### REPAIR

Buildings in the base game do not heal on their own — a repair vehicle has to drive out to them.
The REPAIR tab lists every damaged building you own with its condition, and **SEND CREW** hires
one of your faction's repair trucks for a flat fee and drops it beside that building. It drives
in, fixes it, and stays yours afterwards; it can also be shot on the way. A building that already
has a crew coming says so, so you do not pay twice.

The enemy commander repairs too, and puts its most valuable damaged building first — so a
refinery you bombed does not stay bombed unless you keep hitting it.

Prices, the income rate and the production cycle live in the `Economy` section of the BepInEx
config file (`GoldMineCost`, `GoldMineIncomePerMinute`, `FactoryBuildCost`,
`FactoryProductionSeconds`, `FactoryUpgradeCost`, `BuildingCostMultiplier`, `RepairCrewCost`,
`BuildRadiusKm`), because every mission authors its own faction balance and the defaults are
tuned for the large strategic ones.

## Capturing bases

Taking an airbase means putting a unit that carries troops inside its capture ring and keeping it
there — the game's own rule, not the mod's. What the mod adds is the two things that were missing:
knowing which bases are takeable, and anyone actually going to take them.

**You have to find a base before you can take it.** The game draws no map icon for an airbase you
do not own, so nothing marks them until one of your units gets close: aircraft spot a base from
12 km, ground units from 4 km. Once found it is marked `CAPTURABLE <name>` — on the tactical map
while it is open, in the 3D view while it is not, yellow when nobody holds it and orange when
somebody does — and it **stays** marked for the rest of the mission, whether or not you still have
anything nearby. Finding one shows up in the battle log. Scouting the map is therefore worth doing
on its own.

**Right-click a base you have found and do not own** — in the 3D view or on the tactical map — and
the selected units go and take it. That is the whole interface, and it is deliberately just an order, so it
composes with everything else: queue travel points across the map with the waypoint key and make
the last one a base, and the route ends in a capture. The order snaps to open ground inside the
capture ring, so your units stop somewhere that actually counts rather than wherever the cursor
landed — and somewhere they are not trying to drive through the terminal building to reach.

**A base being taken shows a progress bar** on its marker, in the 3D view and on the map:
`CAPTURING MARIS AIRPORT [####------] 40%` in green while it is going your way, `CONTESTED` in red
while somebody else is pulling it the other way. The game itself shows this nowhere, so without it
a squad standing in the ring looks like a squad doing nothing.

**To send a capture squad somewhere else, just order it there.** The capture snap covers the whole
ring, so a squad already standing on a base would otherwise have every order near that base pulled
straight back to the middle of it. A squad already committed to a base is treated as being
redirected instead, and you get a `LEAVING <BASE>` toast to confirm it. Adding units to the
selection that were *not* sent there still reads as reinforcing the capture.

There is no CAPTURE button. Placing the order on the marker *is* the interface, and a button that
did the same thing to the nearest base only hid that.

The mod tells you how many of the selected units can actually move the capture bar. It does not
refuse the order when none of them can — which units carry troops is not obvious, and an order
that silently does nothing is worse than one that warns. The first time a mission loads, the
BepInEx console also names every vehicle in your faction that carries capture strength.

**Aircraft can take a base too.** Put the order on a capture marker with aircraft selected and they
fly to that airfield, land on it, and hold it until it falls, then take off again on their own —
give them any other order and they take off immediately. Each aircraft parked in the ring is worth
about a light vehicle to the capture — **aircraft capture strength** in `Settings > Gameplay`,
where 0 turns it off entirely. It exists because in the base game an aeroplane contributes nothing
at all to a capture unless it happens to be carrying a troop pod. An airfield taken this way still wants ground units to hold it,
because parked aeroplanes are easy to kill.

Capturing works on any mission, and it is a core feature, so it is there whether or not advanced
mode is on.

**The enemy commander expands too.** It reviews every twenty seconds: nearest base nobody holds,
up to three of its capture-capable units committed, and it keeps them pointed at the ring until
the base changes hands. If it has nothing that can take ground it buys one before anything else —
and an empty base always outranks a defended one, however far away. Captures by either side raise
a toast and land in the battle log.

Capturing is worth doing beyond denying the other side: a base you hold is somewhere to launch
from, a new circle to build in, and one more base the enemy has to take off you before the
lose condition below catches you.

> The enemy commander's expansion is still carried by ground vehicles, so its expansion is a drive,
> not a raid. Landing troops from a helicopter is the fast version and is not built for it yet —
> say if you want it.

## Winning and losing

**A faction that is left holding no airbase loses the match**, and everyone else wins it. This is
the mod's own rule and it applies on every mission, so the round always has an end even where the
mission author never wrote a capture objective for a particular base. It is also why the build
radius matters: lose your last base and you can neither launch nor build, so the game is over.

## CORE Control Duel (the missions that ship with the mod)

The mod installs two missions of its own, **CORE Control Duel** and **CORE Control Duel Far**,
into your mission list the first time it loads. Nothing extra to download — but the whole `COREControl` folder has
to be in `BepInEx\plugins`, not just the DLL. Host it from the normal mission list.

It is a **1v1 commander duel, base against base**:

- Each side starts with a single highway airstrip, two vehicle depots and a few AA mounts.
  Every other airbase on the map is shut, so there is nowhere else to fly from and nowhere to
  divert to. The two strips sit about 20 km apart.
- Nothing else is placed. No armies, no industry, no front line. Both sides open with the same
  faction balance, the same aircraft pool and the same buildings.
- You grow from there with `CMD → BUILD`: gold mines for income, factories for units, radars
  and defences from the structures catalogue, and the depots to buy ground forces outright. That
  is the whole match — economy first, then the army it pays for.
- **Win by capturing the enemy airstrip.** A faction left holding no airbase loses the match
  outright — on this map that is the one strip it started with. Everything either commander builds is a building, so
  bombing the other commander's economy is as legitimate as killing their tanks — and both of you
  can pay a crew to put it back up.
- **Three neutral airbases are in play**: Maris Airport, Sandrift Airbase and South Boscali
  General Aviation, sitting between and around the two strips. Nobody owns them at the start and
  either side can take one. The map's stock airbases are not symmetric — Maris is 9 km from the
  Boscali strip, while Primeva's nearest two are 18 and 23 km — so this is a compromise rather
  than a mirror. Tell me if it plays lopsided.
- **The sky starts empty.** Neither side is given a free AI air force the way a normal mission is,
  and neither side is given free airframes: every AI aircraft in the duel is one a commander paid
  for and launched. Yours come out of the AIR window, the opponent's out of its own funds. The jet
  **you** fly is unaffected — that comes out of your own allocation, as in any mission.
- No nukes: the escalation thresholds are out of reach and the warheads are restricted.
- Advanced features are on automatically, and **the enemy commander is already MATCHED when the
  match starts** — on the duel it is the opponent, not a setting you have to find. The Gameplay
  button reads `MATCHED  (SET BY MISSION)`, and its first purchase and first gold mine go in
  immediately rather than up to half a minute later.

**The duel opponent plays harder than the enemy commander does elsewhere**, on purpose — a duel
you are never attacked in is not a duel:

- It opens with **half again your starting balance**, and it keeps building its economy to four
  gold mines and two factories instead of stopping at two and one.
- It spends **45% of its pot every 30 seconds, on up to five vehicles**, instead of a quarter on
  three, so its convoys keep coming out of its depots rather than trickling.
- It **buys and launches its own aircraft**, up to eight in the air at once, choosing the airbase
  first and then an airframe that airbase will actually take — so nothing it buys writes itself off
  trying to use a strip it does not fit — and then **gives each aeroplane a strike mission** over
  your base, which is what turns bought aircraft into air raids.
- It **composes a wing rather than repeating one aeroplane**: air superiority the moment you put
  an aircraft up and it has no fighter, a couple of transport helicopters while you have an army on
  the ground, ground attack the rest of the time. Inside a role it buys the cheapest airframe that
  fits until it is running two of them and only then spends up, so the opening minutes are cheap
  light aircraft and the expensive ground-attack jets arrive when its economy can carry them.
- It **knows where your base is** — every building you own is on its map from the moment you put
  it down. Your tanks and aircraft are not: it knows the address, not your army. That is what
  points its convoys and its strike aircraft at you instead of leaving them milling around their
  own strip.

Everything after the opening balance is still earned at your rates, so it is a head start, not a
cheat: kill its convoys and bomb its mines and it stalls exactly like you would.

Only the host can construct buildings, because buildings are spawned server-side. In a
two-human duel the guest commander plays the depots, the orders and the air war, and the host
builds.

### CORE Control Duel Far (the far-start version)

**CORE Control Duel Far** is the same match with the two sides pulled apart. On the original map
the strips are 20 km apart, close enough that both sides' standing patrols meet within the first
minutes and the match can settle into one long air battle over the middle before either economy has
grown.

- Boscali starts at **North Boscali Airbase** in the north-west, Primeva at **Sandrift Airbase** in
  the south-east, about **69 km apart**. Both are proper airbases rather than highway strips.
- Each side still opens with two vehicle depots and a few AA mounts at its own base, the same
  funds, the same empty sky and the same MATCHED enemy commander.
- **Four neutral airbases sit in between**: K92 Highway Strip and Dustbowl Highway Strip (the two
  starting strips of the original duel) plus Maris Airport and South Boscali General Aviation. All
  four are unowned and capturable. They are not evenly shared out - Dustbowl is 18 km from
  Sandrift while Boscali's nearest is Maris at 34 km - so expect a long-range map, not a mirror.
- Win the same way: capture the enemy airbase, or leave them holding none.

Because a mission is only copied into your mission list when the game starts normally, **restart
the game** after updating the mod if the far-start map is not in the list yet.

The missions are mod content: each is rewritten from the plugin folder whenever the shipped copy
changes, so save one under a different name before editing it.

## Enemy commander

Off by default on every mission except **CORE Control Duel** and **CORE Control Duel Far**,
which force it on. Cycle it in
**Settings → Gameplay**.

The base game only ever deploys the fixed vehicle reserve a mission was authored with — no
opposing faction ever spends money. Turning this on gives every hostile faction a commander that
reviews its funds every 30 seconds and buys reinforcements into its reserve.

**It starts where you start, and it earns what you earn.** There is no difficulty slider and no
stipend: both commanders buy ground units out of `factionFunds` at the same rate, so the only
thing that decides the ground war is what each of you spends it on.

- **Matched** — the enemy is put on your faction's economy the first time it reviews: your
  faction's authored starting balance, your kill reward, your tax rate. Turn it on at mission
  start; it mirrors the opening position, not whatever you happen to be holding.
- **Mission funds** — same commander, but it keeps the balance and income the mission author
  gave it. Use this on missions that are meant to be lopsided.

Both modes spend a quarter of the pot per review, up to three vehicles, which is a tempo limit
rather than an advantage — it stops either side dumping its whole balance in the first minute.

### Tactical plans

Every 30 seconds the enemy commander reads what you are fielding and commits to the plan that
counters it. It takes **two** reviews of the same read to switch, so a counter you just paid for
gets a minute to work before it answers.

| You are fielding | It builds | Because |
| --- | --- | --- |
| Aircraft | **Air defence** — SAMs and AAA | An umbrella is the cheapest answer to air power |
| Massed armour | **Fire support** — artillery behind a screen | Guns break a column faster than trading tank for tank |
| A static line of guns and launchers | **Spearhead** — MBTs and AFVs | Armour runs through a line before it can range |
| Nothing dominant | **Recon screen** — light vehicles | Cheap mass, take ground while you decide |

Its current plan and balance are shown under your funds readout, openly — a plan you cannot see
is a plan you cannot answer. Shift your own composition and its plan flips, which is the game:
massing armour pulls it onto artillery, so bring air; leaning on air pulls it onto SAMs, so bring
armour. Whatever the plan says, it will still buy one launcher first if you are flying and it has
no air defence left at all.

Bought units are deployed and driven by the base game's own depot and ground AI, so they behave
like any other enemy convoy. Requires advanced features, and only runs in singleplayer or when
hosting.

### What it does with what it owns

Four things the commander does that are not "buy another tank":

- **It defends its base.** A share of its ground force is posted on a ring around every base it
  holds instead of being sent at you — air-defence vehicles first, because a launcher gives an
  attack the least and a base the most. Anything hostile inside 15 km of one of its bases **on its
  own radar picture**, or any hit on anything it owns, puts it in a defence posture for two
  minutes: the ring roughly doubles, pulled back out of the attack, and stands down again once the
  raid is over so it does not turtle for the rest of the match. Its readout on the HUD says
  **DEFENDING** while it is up. Come in low, under its radar, and you meet the resting ring
  instead. If it cannot man the ring out of what it owns it buys AAA and SAM vehicles for it,
  ahead of whatever plan it is running, and it keeps one **radar building at every base** —
  rebuilding it when you bomb it, because a base with no radar cannot see you coming. It puts
  defensive structures around its bases too, once its economy is running.

- **It flies its aircraft at you.** On the duel, every airframe it owns is given a real strike
  mission over your territory out of the same Air Command machinery your own aircraft use, with
  one in three flying air superiority instead once you have aircraft up. Without that the game's
  own pilot AI turns for home after fifteen ticks with nothing found, which is most of the way
  across the map — an enemy that bought aircraft and never once bombed anything. Duel-only: every
  other mission launches its own AI aircraft and may script what they do.
- **It puts out a radar screen.** It is short of a radar vehicle before it is short of anything
  else in its plan, and it drives the ones it has out to standing overwatch posts on the
  approaches from your territory, on the highest ground near each post. It has always been handed
  the location of your *buildings* — without that it has nothing to attack — but never anything
  about your army. This is how it earns that instead: everything it sees that way, it sees because
  a truck is parked somewhere you can shoot it.
- **It goes to sea.** It builds its own naval dock on the nearest coast to a base it holds,
  upgrades it, and buys hulls under exactly the same level gate you are on.

Airframes and hulls are both bought out of **saved** funds rather than a per-review slice: an
aircraft is worth several ground vehicles, and a share that expires with the review it was set in
never once adds up to one. Either fund hands its surplus back to the ground spender after a few
reviews, so a faction that can never put anything up does not withhold money from its convoys
forever.

Its **buildings** are saved for the same way, and for the same reason. The unit spender used to
take its share off the top every review, so the balance never climbed to a factory's price and the
enemy commander spent whole matches building nothing but gold mines. Whatever it wants next — a
mine, then a factory, then a naval dock — is now held back from the unit spender until it is
bought, and released again if the map turns out to have nowhere to put it.

### Player commander

Off by default. **Settings → Gameplay → PLAYER COMMANDER**, or the **Player commander** binding in
**Controls** (unbound until you set it). Everything above — the buy review, the tactical plans, the
home guard, the radar screen, the naval dock, the strike missions, the expansion drives — then runs
for **your** faction as well, against the best-funded faction opposing you. Its plan and balance sit
on a **YOU** row under your funds.

It is a co-commander, not an autopilot. Your own orders, your BUILD window and your depot purchases
keep working, and any unit you have given an order to is left where you sent it — the home guard
will not re-pin a vehicle, the radar screen will not recall a truck, and a capture squad will not
take a troop carrier off your route until it arrives. It gets no head start and no fund reset: your
economy stays exactly what the mission authored, and everything it buys comes out of the balance you
are also spending. Turn it off and it stops, leaving whatever it bought and positioned where it
stands. Host only, same as the enemy commander.

Config keys: `Gameplay/PlayerCommanderEnabled` and `Keybinds/TogglePlayerCommander`.

## Strategic points

Map-driven economy: geography, not proximity to your own base, decides what is worth building on
and fighting over. Runs once per mission, host only; a pure multiplayer client sees no points at
all.

### What is on the map

- **Resource sites** (diamond markers) — every industrial or ammunition-storage building on the
  map, plus generated fill sites so a map with little industry still gets a spread. A gold mine can
  only be built on a site: arm BUILD GOLD MINE and the ghost snaps to the nearest free site within
  reach (`Points/MineSnapMeters`, 1 km by default) and refuses everywhere else. A map that yields no
  sites at all is treated as a failed pass rather than a rule: discovery retries twice more at
  half-minute intervals, and while there are no sites the pre-points rule (build anywhere inside
  your base radius) stays in force for the player ghost and both AI commanders alike, so nobody is
  ever locked out of building a mine.
- **Villages** (square markers) — clusters of `Points/VillageMinBuildings` or more civilian
  buildings within `Points/VillageClusterMeters` of each other, at the cluster's centroid.
- **Hilltops** (dot markers, labelled HILLTOP n) — local high points found on a height-map scan: the highest point
  within `Points/HilltopRingMeters` and at least `Points/HilltopMinProminenceMeters` above the ring's
  mean height.
- **Outposts** (labelled OUTPOST n) — a civilian cluster too small to qualify as a village (a
  farmstead or hamlet), still worth marking rather than dropping.
- **Crossroads** (labelled CROSSROADS n) — a road-network junction with at least
  `Points/CrossroadsMinRoads` roads meeting or passing through it, found on the level's road network
  rather than on any building or terrain feature.
- **Roadside points** (labelled ROAD POINT n) — generated every `Points/RoadsideSpacingMeters` along
  a road, so a long empty stretch still has something to hold; skipped near a crossroads or inside
  an airbase.
- **Bases** — every airbase. Ownership is read live from the game, the same as everywhere else in
  the mod; it is never stored here.

Discovery spacing (grid size, minimum distance between points, the exclusion ring around an
airbase) is config-file-only, under the `Points` section — retuning it is a map-authoring decision,
not a player setting. Resource sites and control points are capped separately
(`Points/MaxResourceSites` and `Points/MaxControlPoints`, with per-kind ceilings `MaxCrossroads`, `MaxOutposts` and `MaxRoadPoints` inside the latter): one shared cap let a full set of sites
starve the control points entirely. Villages, crossroads and outposts get first claim on that cap,
then hilltops, then roadside points last.

### Holding and earning

A resource site is owned by the mine standing on it and pays through the mine's own income and
upgrades, unchanged. Every other kind of point — village, hilltop, outpost, crossroads or roadside —
is held by **presence**: one faction alone with at least `Points/MinGarrison` ground vehicles (2 by
default) in the ring, for `Points/HoldSeconds` (60 by default) cumulative — driving through takes
nothing. Below the minimum, or both factions present at once, and the point pays nobody; two
factions present at once freezes it as **contested** until one side leaves. Villages and crossroads
pay 10/min held, hilltops and outposts 5/min, roadside points 3/min, bases 30/min, all on the same
15 s tick the gold mine income already used. Every rate is a slider, along with the minimum garrison
and hold seconds, on **Settings → POINTS**.

Both commanders — the enemy AI and your own AI when the player commander switch is on — build their
mines on sites instead of stacking them at the base, and post a small garrison (one vehicle over the
minimum) on the nearest few control points within reach of a base they hold, capped at three points
per commander so the home guard is never starved. A mine's build reach also extends to any control
point that commander currently holds, on top of the ordinary base radius.

Owners are not saved across a mission reload — the same stance the mod already takes with mine
upgrade levels.

### Watching the AI

**CMD → COMMANDER LOG**, next to ORDER OF BATTLE. One tab per faction — **YOU** first (filled in
once the player commander switch is on), then every other commanded faction — showing funds, income
per minute by source (bases, villages, hilltops, a combined CONTROL PTS figure for outposts,
crossroads and roadside points, and mines), the current buy plan and the reserve
target, above a scrolling log of every decision that faction's commander has made, newest first,
with mission-time stamps. The same lines still go to `BepInEx\LogOutput.log` exactly as before; the
window reads from the same log, it does not replace it. If the console lines are not appearing
where you expect them, check that the BepInEx console is enabled
(`BepInEx/config/BepInEx.cfg`, `[Logging.Console]` → `Enabled = true`) — this is a one-time
developer setting, not something the mod's own settings window controls.

## Platoon operations

Replaces "the AI buys a vehicle, the game's convoy brain drives it at the nearest enemy" with a
front line the AI commanders form, hold and push. Host-only, like every AI feature; nothing here is
saved across a mission reload.

### What a platoon is

Every ground vehicle an AI-commanded HQ owns is claimed into that commander's pool the moment it
registers, and platoons are formed from the pool by recipe: 3 armour (MBT/AFV), 1 carrier or light
vehicle (APC/LCV, preferring one that can actually take a point), 2 air defence, size 6 by default.
An unfillable slot takes any combat vehicle rather than waiting. A platoon is named (`1ST
PLATOON`…) and always has exactly one objective and one state: Forming, Moving, Holding, Attacking
or Withdrawing. Under half strength it withdraws to the nearest friendly forward base or held base
and requisitions replacements; at zero it is dissolved and its mission reopens.

### The front line

Every held or reachable control point is ranked by distance to the nearest enemy-held point or base
and by income: a point within the front range (15 km by default) of the enemy is front line,
everything else is rear. Front points get a platoon as a forward base — two if the point has seen a
recent hostile contact — with a munitions truck parked in the ring where it is covered. Rear points
get a two-vehicle picket instead, cheap enough that it never competes with the platoons for the
forward-base share (half of all platoons by default); surplus front points beyond that share picket
instead of holding, and a picketed point that comes under threat promotes back to a forward base.

### Offensives

The commander picks a target — the nearest adjacent enemy-held point, or an enemy base whose
observed defence (its own tracking database, not the true count) it can beat — and sizes the attack
to 1.5x what it can see, floored at one platoon for a point or two for a base and capped at six.
Two or three axes are chosen from the commander's forward bases and held bases so they arrive at
roughly the same time from different directions; with only one candidate a second group forms off
to one side. Each group stops five kilometres short on its own side and waits for the others (four
minutes at most, then it goes without them), flipping through any point it passes close to on the
way. A beaten attack (under 40% strength) withdraws and the next attempt on the same target sizes
itself larger; a taken target becomes a new forward base. A pressure clock builds every review and
forces the commander's best available attack with at least two platoons every 12 minutes,
whatever it can see — the guard against a commander that never attacks at all.

### What you can watch

Platoon markers (`2ND PLATOON 5/6 HOLDING`) at each platoon's leader, an `FOB` tag on a forward
base's point label, and small crosses at your own live attacks' release points. The COMMANDER LOG's
OPERATIONS block shows pressure, platoon and FOB counts, open requisitions, and one line per live
mission. Five sliders on the POINTS tab (platoon size, forward-base share, front range, pressure
interval, offensive spend); the recipe itself is a config-file setting. The player's own AI
commander runs the same doctrine once its switch is on, and an order given by hand to one of its
platoon's vehicles is respected — it drops out of the platoon and is not re-recruited until the
hands-off window expires.

## Unit systems

- Toggle compatible radar systems on or off, and show radar coverage on the map at an
  adjustable target altitude.
- Force Jacknifes to repair the nearest valid target instead of the highest-priority one.
- Relocate containers using nearby tractors or flatbeds.

## Game speed

`CMD` carries a **1x / 2x / 4x** row. An RTS spends a lot of its time watching a convoy cross a
map. It writes the game's own time scale — the same knob its slow-motion binding uses — so it is
**host only**, and it drops back to 1x when you leave commander mode, so nothing carries a
fast-forward into flying or into the next mission.

## Experimental

- Heavily WIP tool for automatically building SAM sites via Jacknifes, with landing pads for
  ammo deliveries. Intended to be used later by the enemy AI for balancing purposes.

---

## Notes and known limits

- Advanced features (production, Air Command, supply, naval, SAM analyzer) are gated to large
  strategic missions. Other missions start in **core mode**, which still has camera, selection,
  groups, routes and attack orders. `CMD → UNLOCK ALL FEATURES` overrides the gate, but it can
  break missions that were not built for it.
- The mod is client-side and issues the same networked orders a player would; it is not a
  server plugin.
- Combat alerts for units taking fire come from a server-side code path, so they work in
  singleplayer and when hosting. A pure multiplayer client still sees losses and arrivals.
- Only structures are repairable in the base game, so a retreating vehicle goes to a Jacknife or
  rearm truck to top up ammo rather than to heal.
- Automatic retreat only triggers for units that currently hold a RTS order. The RETREAT
  button works on anything.
- Buildings and repair crews are spawned through the server, so only a host (or singleplayer)
  can build or dispatch them. The BUILD menu says so rather than failing quietly.
- A repair crew is a real vehicle, not a heal timer. If the faction fields no repair truck, or the
  crew is destroyed on the way in, the repair does not happen.
- A factory's production interval is registered once, when it is built, and cannot be changed
  afterwards. That is a base-game limitation, and it is why an upgrade raises the batch size
  instead of the rate.
- Nothing here is balanced yet.

## Building from source

Requires the .NET SDK and a Nuclear Option installation with BepInEx.

```bash
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option"
```

`GameDir` can also come from a `NUCLEAR_OPTION_DIR` environment variable. The output DLL lands
in `bin/Release/net472/` and goes into `BepInEx/plugins/COREControl/`.

See [CLAUDE.md](CLAUDE.md) for the architecture notes and [CHANGELOG.md](CHANGELOG.md) for the
release history.

## Credits

CORE Control is a fork. Almost everything in it was written upstream, in this order:

| Project | Author | Contribution |
| --- | --- | --- |
| [NOCommander](https://github.com/DontKnowWhatImDoingHere/NOCommander) (Nuclear Option Commander) | DontKnowWhatImDoingHere | The original mod: RTS camera, selection, orders, depots, Air Command, supply helicopters, SAM site tools. |
| [RTS-Commander](https://github.com/simonsimme/RTS-Commander) (Ground Control (RTS)) | simonsimme | Renamed it Ground Control; economy and building, the enemy commander, base capture, the Ground Control Duel mission, camera rework. |
| [RTS-Commander](https://github.com/AMAUKDev/RTS-Commander) (Ground Control (RTS)) | AMAUKDev | Build tooling, player-side AI commander, strategic points, platoon operations, air wing, the far-start duel and strategic save. The direct upstream of this fork. |

The upstream history is kept intact in this repository's git log. Like its upstreams, CORE Control
is released into the public domain under the [Unlicense](LICENSE).
