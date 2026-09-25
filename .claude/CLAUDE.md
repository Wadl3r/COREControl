# MUST FOLLOW INSTRUCTIONS
Reason step-by-step and verify. Communicate concisely and precisely, in formal language
regardless of the user's style.

Follow a PLAYER story/flow -> UX -> engine hook development flow: what the commander does, what
they see, then which game type or Harmony patch delivers it. Ask about the desired flow if unclear.

Only commit/push if explicitly requested by the user. Use descriptive names. Constants at class
level with a `<summary>` saying what the number means and why it is that value. No gameplay
behaviour change without a conductor track or an explicit user instruction.

Handle simple merge conflicts yourself; refer complex conflicts and anything with ripple effects
to the user. Upstream is `simonsimme/RTS-Commander` (`git fetch upstream`); keep our changes in
their own services/files where possible so upstream merges stay clean.

## Project shape (read before touching code)

- BepInEx 5 plugin for Nuclear Option (Unity, `net472`, Harmony). Build and install with
  `.\build-and-install.ps1` (or `build-release.bat`); details in `BUILD.md`. Close the game first.
  **Hot reload**: `build-dev.bat` (= `.\build-and-install.ps1 -Dev`) copies into `BepInEx\scripts\` and the
  ScriptEngine plugin reloads the mod in the running game within ~3 s (F6 forces it). Use this
  for in-game verification loops; a reload resets mod memory (upgrade levels, groups, AI plan
  state) but not game state. Never leave the mod in both `plugins\` and `scripts\`.
- Everything is a **service**: a class implementing the hooks in `Core/ICommanderService.cs`
  (`Activate`, `Deactivate`, `TickActive`, `TickPersistent`, `ResetSession`), registered with one
  line in `Core/CommanderModeController.cs`. Registration order is execution order. A new feature
  is a new service plus one `Register` call, not new code inside an existing service.
- Periodic game logic uses `CommanderScheduler.IsDue` (scaled time, pauses with the game). UI
  refresh uses `IsDueRealtime`. Using the wrong one is a bug the changelog has already fixed once.
- Settings live in `Core/CommanderSettings.cs` as BepInEx config entries with a `Get`/`Set` pair
  and a touch in the warm-up list. Sliders and toggles in the settings window live in
  `UI/CommanderOverlayUiSettings.cs`; follow the existing `Draw*Slider` helpers.
- `Core/CommanderGameAccess.cs` wraps the game's internals. Look there before calling anything
  from `Assembly-CSharp` directly.
- The game has three factions per mission: ours (`CommanderGameAccess.GetLocalHq()`) and hostiles.
  AI services iterate `FactionRegistry.GetAllHQs()` and skip the local HQ; that skip is the seam
  for any "AI on the player side" work.

## Track shape — keep it lean (decided 2026-09-11)

The default for any conductor track. Exceeding a limit is not forbidden; it is a signal to come
back to the user before continuing.

- **Spec ≤ 2 pages.** Goal, problem with file:line evidence, the existing code being reused (see
  Reuse), requirements, acceptance criteria, files in scope, out of scope. Decisions the user has
  taken are recorded once; decisions still open are listed as NEEDS USER and asked immediately.
- **Plan ≤ 20 tasks.** A task is ONE behaviour with its verification. Where a `SelfCheck` can
  cover it, the failing self-check, the implementation and the passing check all live inside the
  one task.
- **One executor, one review, one grader.** One agent runs the whole plan (re-dispatched only if it
  runs out of context), one independent review of the full diff, one evaluation with at most two
  fix cycles, then a PR. Roughly 6–8 agent runs per track. State the expected agent count before
  launching anything above ~20.
- **Review once per PLAN, not per phase or per task.** The single mandatory independent review is
  a `default` second-opinion panel on the PR diff before merge. A spec review is added only for
  tracks that break the limits above. `max` is opt-in, only when the user asks.
- Keep `conductor/` track files up to date; come back to the user for complex decisions.

## Reuse rules — non-negotiable

1. **Before planning ANY feature, find what already does this and READ it**, including comments
   and the code around it — comments here record engine measurements and incidents the code alone
   does not explain (the `ponytail:` remarks are deliberate scope cuts; respect them or argue them
   in the spec). Prefer the code-review-graph MCP (`semantic_search_nodes`, `get_impact_radius`,
   `query_graph`) for exploration and impact analysis; Grep for literals.
2. **Name the existing thing in the spec, and why it does not fit** — for every NEW service,
   setting, Harmony patch, scheduler, UI window or shared helper. "I did not find one" is
   acceptable only after §1.
3. **MOVE code, never paraphrase it from memory.** Cut and paste, rename on the way, leave a
   pointer comment at the old site.
4. **One definition, two callers.** Identical or near-identical strings or constants in two places
   are the tell. If a second caller needs different behaviour, parameterise; never fork. The enemy
   commander and the player deliberately share one price ladder and one siting rule — keep it so.
5. **Generalise the second instance.** The second bounded retry, ceiling or "wait for N reviews"
   pattern means extracting the first and retrofitting, behaviour-neutral.
6. **Subagent briefs carry all of this**: name the code to read first and the pattern to follow.

## Testing rules — non-negotiable

There is no unit-test project. The mod's automated checks are the `SelfCheck()` methods called
from `Core/CommanderPlugin.cs` at plugin load, which log `self-check FAILED` to the BepInEx
console. Everything else is verified in the running game.

1. **Every decision table, price ladder or threshold gets a `SelfCheck` case** next to the
   existing ones. If a constant can be retuned into nonsense, a self-check says so at load.
2. **Never claim done without the running game.** Build, install with the script, launch, load
   `CORE Control Duel` (or a supported stock mode), perform the actual player action, and read
   `BepInEx\LogOutput.log` for the mod's own log lines and any `FAILED` or exception. The
   developer launches and plays; you say exactly what to do and what log lines prove it.
3. **Server-only calls are real.** Anything touching `factionFunds`, `AddSupplyUnit`, spawning or
   `ModifyUnitSupply` must be guarded by `hq.IsServer`; a pure multiplayer client throws.
4. **Placeholders are bugs.** If a real implementation is not feasible, say so and the task stays
   open.
5. **When you change a gate, hook or check, prove it still FAILS**: plant a defect, watch a NAMED
   check fail, restore, confirm byte-identical. This rule is for gates, hooks and checks ONLY.
   Corollaries for every hook in `.claude/settings.json` and `.claude/check.cmd`: time the command
   before setting its timeout; never `|| true` without writing down why; never put slow work in
   `PostToolUse`.

## Second-opinion MCP

`second-opinion-mcp` routes to a Claude Code session on a **different model family** via Ollama.
Different blind spots, not more compute. Presets are decided server-side: pass `preset`, never
model names.

- **When:** the one mandatory use is the `default` panel on a PR diff before merge (see Track
  shape). Also worth it for an unreproducible in-game bug or a high-confidence subjective call.
  Skip for mechanical work and inside Tier 2 subagents.
- **How:** `start_panel_review(prompt, preset: "default")` → `check_panel(panel_id)`; you
  synthesise, no judge is called. Tell reviewers to write findings to a file and summarise. Do
  not set `max_turns`. Treat output as peer review: verify disagreements against the code.
- **Prerequisites:** `ollama serve` up on `127.0.0.1:11434` AND cloud credits. If unavailable,
  substitute one Opus skeptic agent and record the deviation in the track's RESULT.md.

## code-review-graph MCP

A structural index of this repo. Prefer it for exploration, impact analysis and review
(`semantic_search_nodes` with `kind="Function"`/`"Class"`, `get_impact_radius`,
`get_affected_flows`, `detect_changes` + `get_review_context`). Grep/Glob/Read are fine for
literals, config and non-code files. Freshness comes from the watch daemon
(`code-review-graph daemon status` should show this repo `alive`), never from a `PostToolUse` hook.
