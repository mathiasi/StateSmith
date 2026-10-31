# North Star: one diagram, every target

**Goal.** A Manetta-style controller is drawn once. The diagram file holds everything: types, variables, behaviour and timing.
StateSmith generates idiomatic, allocation-free Rust for the board and TypeScript for the app from it, with **no per-language glue**.
Nobody hand-writes enums twice or keeps a C# expansion file next to a Rust and TypeScript codebase. Nothing new has to be learned beyond the diagram.

This document has four parts:
1. what "best" looks like;
2. the principles behind it;
3. a phased roadmap with effort;
4. what is already working on branch `north-star`.

The first step, **portable code**, is implemented there. With it, the Manetta example regenerates with zero glue,
and its trace is byte-identical to the expansion-based version (Rust == TypeScript == before).

## 1. What the diagram should say

Status markers: ✅ works on this branch. ◻ proposed (phase number).

```
/'! $CONFIG : toml
[RenderConfig.Portable]                                      ' ✅ declared once, in the diagram
Types = """
enum Reply { NONE, OK, BUSY, NOT_READY, FAULT, INVALID }
enum Power { OFF, HEATING, READY, COOLING, ON }
"""
Variables = """
uint now_ms = 0;                  // host time
uint heartbeat_timeout_ms = 10000;
Reply reply = Reply.NONE;
Power power = Power.OFF;
bool pump_on = false;
"""
Events = """                                                 ' ◻ P3 typed event payloads
START_BREW(uint max_ms)
ENTER_STEAM(uint max_ms)
"""
Functions = """                                              ' ◻ P3 host calls -> Rust trait / TS interface
void log(StopReason why);
"""
'/

ManettaSm : PING / reply = Reply.OK;                         ' ◻ P4 root behaviours in PlantUML (no wrapper state)

IDLE --> BREWING : START_BREW [max_ms != 0 && power == Power.READY] / reply = Reply.OK; brew_cmd_max_ms = max_ms;
                                                             ' ✅ portable code   ◻ P3 `max_ms` payload (today: `cmd_max_ms` var)
BREWING : enter / pump_on = true;                            ' ✅
BREWING --> IDLE : after(brew_cmd_max_ms) [start_source == StartSource.APP] / stop_reason = StopReason.COMMAND_CAP;
                                                             ' ◻ P2 `after()` trigger (today: `do [... && after(brew_cmd_max_ms)]` ✅)
BREWING --> IDLE : do [elapsed(last_write_at) >= heartbeat_timeout_ms] / stop_reason = StopReason.HEARTBEAT;   ' ✅
IDLE --> OFF : do [elapsed(auto_off_from) >= auto_off_min * 60000]                                              ' ✅ wrapping u32 math
```

Generated Rust (✅ today, from the real example):

```rust
pub enum Reply { #[default] NONE, OK, BUSY, NOT_READY, FAULT, INVALID }   // + #[repr(u8)], derives, REPLY_COUNT

// uml: 1. do [start_source == StartSource.APP && after(brew_cmd_max_ms)] / { stop_reason = StopReason.COMMAND_CAP; } TransitionTo(IDLE)
if self.vars.start_source == StartSource::APP && ((self.vars.now_ms.wrapping_sub(self.vars.BREWING_entered_at)) >= self.vars.brew_cmd_max_ms) {
    self.brewing_exit();
    self.vars.stop_reason = StopReason::COMMAND_CAP;
    self.idle_enter();
    return;
}
```

TypeScript from the same line: `this.vars.start_source == StartSource.APP && ((((this.vars.now_ms - this.vars.BREWING_entered_at) >>> 0)) >= this.vars.brew_cmd_max_ms)`.

What stays out on purpose:
- **Orthogonal regions.** Manetta already decided on one hierarchical machine (brew and steam are mutually exclusive on a single heater).
  The parallel concerns are variables and guards, and with portable code plus time helpers those read cleanly. See P5 for when regions would be worth it.
- **The heartbeat stays a host concern.** "Any BLE write is a heartbeat" is a transport fact. The host sets `last_write_at`; the diagram only says what a quiet link means.

## 2. Principles

1. **The diagram is the single source.** Types, variables and behaviour live in the diagram file (`$CONFIG` TOML plus states). Generator settings choose only the target and paths.
2. **Diagram code is translated, not echoed.**
   - StateSmith already turns every machine into GIL, a small C# subset, and has a transpiler per language.
     Portable code makes the diagram's own action and guard code GIL too, so it gets the same treatment as the generated code.
   - The language is deliberately small: assignments, comparisons, arithmetic, `&& || !`, enum members as `Type.MEMBER`, `true` / `false`.
   - Later (P3): calls to declared host functions and event payload names.
3. **Time is first-class and wraps.** `after()`, `elapsed()` and unsigned arithmetic behave like a u32 millisecond counter in every language
   (`wrapping_sub` in Rust, `>>> 0` in TypeScript, natural in C).
4. **Idiomatic, constrained output.** Rust stays `core`-only, allocation-free, `const fn new()` and clippy-clean. TypeScript stays `strict`.
5. **Small upstreamable steps.** Each phase is useful on its own, opt-in, and changes no output for existing diagrams.

## 3. Roadmap

| Phase | What | How | Effort | Status |
|---|---|---|---|---|
| P0 | Rust target | GIL → Rust visitor (`RUST_SPIKE.md`) | 14–34 days to upstream quality (earlier estimate) | spike done (`rust-spike`) |
| **P1** | **Portable code**: `[RenderConfig.Portable]` Types + Variables; all diagram code is GIL | declarations emitted into GIL; vars auto-expanded with the GIL path; action and guard code wrapped as GIL instead of raw echo | spike: done. Upstream: 1–2 weeks (make every transpiler handle field initialisers, unsigned math and user enums; explicit opt-in switch; docs) | **✅ Rust + TS** |
| **P2** | **Time**: `after(ms)`, `elapsed(t)` | transform adds `{STATE}_entered_at` plus an entry action; text rewrite at expansion; transpilers map unsigned `+ - *` to wrapping ops | done as guard functions. Next: `after(ms)` as a real trigger (grammar), and a generated `next_deadline_ms()` so a low-power host can sleep until the next timer instead of ticking `do`: about 1 week | **◐ Rust + TS** |
| P3 | Host calls and event payloads | `Functions` → a Rust trait as a generic parameter (`ManettaSm<H: ManettaHost>`) plus a TS interface; `START_BREW(uint max_ms)` → typed `dispatch_start_brew(max_ms)` | 2–3 weeks; needs generics support in the Rust visitor and an agreed design for each target | ◻ |
| P4 | Diagram ergonomics | root behaviours in PlantUML; accept `left to right direction`; `in(STATE)` guard (subtree-id range check); event groups `(COMMANDS)` | about 1 week, mostly grammar plus small transforms | ◻ |
| P5 | Orthogonal regions | per-region state ids, dispatch to each region, cross-region exit, `in()` across regions (see the earlier analysis) | 4–8 weeks, core algorithm; only if a truly independent concern appears | ◻ (not needed for Manetta) |

Upstream path: P1 and P2 help all eight languages, not just Rust, so they make a good first discussion with the maintainer
(next to StateSmith/StateSmith#475 for Rust). They extend the existing `$gil(...)` mechanism (wiki "mixing GIL and user code"), rather than adding a new language.

## 4. Started: what works on branch `north-star`

**Portable code mode** (on when `[RenderConfig.Portable]` has Types or Variables):
- `RenderConfigPortableVars` (`Types`, `Variables`, `TimeVariable`) is reachable from diagram TOML as `[RenderConfig.Portable]`.
- **Declarations:**
  - Types are emitted as nested GIL types, so each transpiler hoists them like its own enums. Rust gets `#[repr(u8)]`, derives and `*_COUNT`; TypeScript gets `export enum`.
  - Variables become real GIL fields with initialisers. In Rust that's a `pub const fn new()`; in TypeScript, typed fields.
- **Expansion:** every declared variable is auto-expanded to `this.vars.x`. User expansions and default expansions also use the GIL path in this mode.
- **Wrapping:** `WrappingExpander` marks all action and guard code as GIL. Explicit `$gil(...)` sections merge in.
- **`PortableTimeProcessor`:** `after(ms)` (from the owning state's entry) and `elapsed(t)`. It validates that the time variable is declared and that `after()` is only used in states.
- **Transpilers:** Rust and TypeScript map unsigned `+ - *` to `wrapping_*` and `>>> 0` / `Math.imul`. TypeScript maps `uint` and similar types to `number`.
  The Rust target declares `Vars` as a class in GIL, so fields can have initialisers; the Rust output is unchanged.

**Manetta example converted** (`src/test-misc/manetta-example/`):
- **Removed:** the generator's variable table, both expansion classes, both render configs and both hand-written enum files. The generator test is now just "pick a target".
- **Diagram text:** `set_reply(OK)` → `reply = Reply.OK`; `power_is(READY)` → `power == Power.READY`; `brew_started_at` bookkeeping → `after(brew_cmd_max_ms)`.
- **Trace:** byte-identical to the expansion-based version from `manetta-example`, and Rust == TypeScript (140 lines, 12 scenarios).
- **Builds:** clippy-clean; `#![no_std]` riscv32imc build is 2,084 B `.text` (was 2,080 B).

**Tests:**
- `PortableCodeTests` (5): Rust and TS output, balanced-paren rewriting, missing time variable, and a typo (`Light.ONN`) failing at generation time.
  The last one is a real gain over raw target code: StateSmith compiles portable code with Roslyn, so mistakes surface once, before any target compiler runs.
- `ManettaExampleGenTests.RustAndTypeScriptTracesMatch`.
- Full suite: same 92 failures as the untouched base commit (environmental, Java and TypeScript Spec2 tooling in this container), nothing new.
  No other language's committed output changed.

**Known limits of the spike:**
- **Only Rust and TypeScript** were taught unsigned wrapping and portable declarations.
  C99 / C++ / C# / Java / Python / JavaScript in portable mode are untested. C99 will likely need struct-field initialisers moved into its ctor.
- **`after()` is a guard function**, not yet a trigger. `elapsed()` takes any expression; `after()` measures from the behaviour's owning state.
- **Types are enums only.** The variable parser expects `type name = value;` per line.
- **Portable mode is implied** by the presence of the section; upstream it should be an explicit switch.
  In this mode every piece of diagram code must be valid portable code. That's a strictness win, but existing target-code diagrams can't be mixed in.
- **Cosmetics:** some redundant parentheses around wrapped time expressions, and `self.vars.X_entered_at` names follow the state name casing.

**Next concrete steps:**
1. P2: `after(ms)` trigger syntax and `next_deadline_ms()`.
2. P3: design the host trait / interface (`ManettaHost`), then typed payloads.
3. P1 hardening: C99 and C# in portable mode, plus a Spec2-style portable spec run in several languages.
