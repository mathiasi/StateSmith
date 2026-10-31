# North Star: one diagram, every target

**Goal.** A Manetta-style controller is drawn once. The diagram file holds everything: types, variables, behaviour and timing.
StateSmith generates idiomatic, allocation-free Rust for the board and TypeScript for the app from it, with **no per-language glue**.
Nobody hand-writes enums twice or keeps a C# expansion file next to a Rust and TypeScript codebase. Nothing new has to be learned beyond the diagram.

This document has five parts:
1. what "best" looks like;
2. the principles behind it;
3. a phased roadmap with effort;
4. what is already working on branch `north-star`;
5. the design of the host and event data (P3).

Implemented there so far: portable code (P1), time with `after()` and `next_deadline_ms()` (P2), host calls and typed event data (P3),
and root behaviours in PlantUML (part of P4). The Manetta example regenerates from the diagram alone, with zero glue.
Its trace is still byte-identical to the expansion-based version (Rust == TypeScript == before, polling == sleeping).

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
"""
Events = """                                                 ' ✅ P3 typed event payloads
START_BREW(uint max_ms)
ENTER_STEAM(uint max_ms)
"""
Functions = """                                              ' ✅ P3 host calls -> Rust trait / TS interface
void set_pump(bool on);
void set_power_relay(bool on);
"""
'/

ManettaSm : PING / reply = Reply.OK;                         ' ✅ root behaviours in PlantUML (no wrapper state)

IDLE --> BREWING : START_BREW [max_ms != 0 && power == Power.READY] / reply = Reply.OK; brew_cmd_max_ms = max_ms;
                                                             ' ✅ portable code, `max_ms` from `dispatch_start_brew(max_ms)`
BREWING : enter / set_pump(true);                            ' ✅ host call
BREWING --> IDLE : after(brew_cmd_max_ms) [start_source == StartSource.APP] / stop_reason = StopReason.COMMAND_CAP;
                                                             ' ✅ `after()` trigger (sugar for `do [after(ms) && (guard)]`)
BREWING --> IDLE : do [elapsed(last_write_at) >= heartbeat_timeout_ms] / stop_reason = StopReason.HEARTBEAT;   ' ✅
IDLE --> OFF : do [elapsed(auto_off_from) >= auto_off_min * 60000]                                              ' ✅ wrapping u32 math
```

Generated Rust (✅ today, from the real example):

```rust
pub enum Reply { #[default] NONE, OK, BUSY, NOT_READY, FAULT, INVALID }   // + #[repr(u8)], derives, REPLY_COUNT

// uml: 1. do [after(brew_cmd_max_ms) && (start_source == StartSource.APP)] / { stop_reason = StopReason.COMMAND_CAP; } TransitionTo(IDLE)
if ((self.vars.now_ms.wrapping_sub(self.vars.BREWING_entered_at)) >= self.vars.brew_cmd_max_ms) && (self.vars.start_source == StartSource::APP) {
    self.brewing_exit();
    self.vars.stop_reason = StopReason::COMMAND_CAP;
    self.idle_enter();
    return;
}

// generated query for low-power hosts: how long until a timer of an active state can expire
pub fn next_deadline_ms(&self) -> u32 {
    let mut best: u32 = 0xFFFFFFFF;
    ...
    // BREWING
    if self.state_id == StateId::BREWING {
        elapsed = self.vars.now_ms.wrapping_sub(self.vars.BREWING_entered_at);
        duration = self.vars.brew_cmd_max_ms;
        if elapsed < duration && duration.wrapping_sub(elapsed) < best {
            best = duration.wrapping_sub(elapsed);
        }
        ... // device cap, heartbeat
    }
    ...
    return best;
}
```

TypeScript from the same line: `((((this.vars.now_ms - this.vars.BREWING_entered_at) >>> 0)) >= this.vars.brew_cmd_max_ms) && (this.vars.start_source == StartSource.APP)`, plus `public nextDeadlineMs(): number`.

What stays out on purpose:
- **Orthogonal regions, for now.** The accurate claim: one machine plus variables covers *this cut-down scope*.
  Brew and steam are mutually exclusive on a single heater; temperature, auto-off and faults are variables, guards and one sub state.
  It holds up only if every parallel concern sits *beside* the power state rather than replacing it. The first fault design got that wrong, and three rules silently stopped working during a fault (see section 4, "Fault restructure").
  Regions become worth it when parallel parts get many rules *between* them. A dual-boiler machine is the clear case: its steam boiler really does run in parallel with brewing, with its own life cycle and shared resources.
  See P5.
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
| **P2** | **Time**: `after(ms)`, `elapsed(t)` | transform adds `{STATE}_entered_at` plus an entry action; text rewrite at expansion; transpilers map unsigned `+ - *` to wrapping ops | spike done: `after(ms)` trigger (grammar), `elapsed()`, generated `next_deadline_ms()`. Upstream: all transpilers, plus a decision on one-shot vs level-triggered `after()`: about 1 week | **✅ Rust + TS** |
| P3 | Host calls and event payloads | `Functions` → a Rust trait as a generic parameter (`ManettaSm<H: ManettaHost>`) plus a TS interface; `START_BREW(uint max_ms)` → typed `dispatch_start_brew(max_ms)` | spike done (design in section 5). Upstream: 1–2 weeks to cover the other targets (C: extern functions or a function-pointer struct) and agree the design with the maintainer | **✅ Rust + TS** |
| P4 | Diagram ergonomics | root behaviours in PlantUML; accept `left to right direction`; `in(STATE)` guard (subtree-id range check); event groups `(COMMANDS)` | about 1 week, mostly grammar plus small transforms | ◐ root behaviours done |
| P5 | Orthogonal regions | per-region state ids, dispatch to each region, cross-region exit, `in()` across regions (see the earlier analysis) | 4–8 weeks, core algorithm; worth it once parallel parts have many rules between them (e.g. a dual-boiler steam boiler beside brewing) | ◻ (not needed for the cut-down single-heater scope) |

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
- **`after(ms)` as a trigger:**
  - the label grammar has a new `trigger_call` rule (`name(args)`); only `after` is accepted, and anything else is a clear error;
  - `after(ms) [guard] / action` is sugar for `do [after(ms) && (guard)] / action`;
  - the ANTLR 4.9.2 parser was regenerated; regenerating the unchanged grammar reproduced the committed parser byte for byte.
- **`next_deadline_ms()`** (TypeScript: `nextDeadlineMs()`):
  - generated from the timers found in guards (`after(X)`, `elapsed(T) >= X`, `elapsed(T) > X`), grouped by the state that owns them;
  - checked only while that state or its sub states are active, via a state-id range check;
  - returns the time until the nearest timer expires, or `0xFFFFFFFF` if none is pending;
  - other guard conditions are ignored, so a host may wake early, never late.

  Rust gets `&self` for such pure queries, and the generated enums now also derive `PartialOrd, Ord` for the range check.
- **Root behaviours in PlantUML (P4):** the machine's own name refers to the root, so `ManettaSm : PING / ...` and `ManettaSm --> FAULT : FAULT_DETECTED` work.
  The Manetta example drops its `MANETTA` wrapper state (tested in `PlantUmlRootBehaviorTests`; the trace is unchanged).
- **Transpilers:** Rust and TypeScript map unsigned `+ - *` to `wrapping_*` and `>>> 0` / `Math.imul`. TypeScript maps `uint` and similar types to `number`.
  The Rust target declares `Vars` as a class in GIL, so fields can have initialisers; the Rust output is unchanged.

**Manetta example converted** (`src/test-misc/manetta-example/`):
- **Removed:** the generator's variable table, both expansion classes, both render configs and both hand-written enum files. The generator test is now just "pick a target".
- **Diagram text:** `set_reply(OK)` → `reply = Reply.OK`; `power_is(READY)` → `power == Power.READY`; `brew_started_at` bookkeeping → `after(brew_cmd_max_ms)`.
- **Trace:** byte-identical to the expansion-based version from `manetta-example`, and Rust == TypeScript (140 lines, 12 scenarios).
- **Diagram text:** the three state-entry timers are now real triggers, e.g. `BREWING --> IDLE : 1. after(brew_cmd_max_ms) [start_source == StartSource.APP] / ...`.
- **Sleeping host:** both runners have a `--sleep` mode that, like a low-power board, sleeps until `next_deadline_ms()` instead of ticking `do` every 100 ms.
  The traces are identical to polling, and identical across Rust and TypeScript, with **25 `do` dispatches instead of 2,190** over the 12 scenarios of the time (29 vs 2,850 over today's 18).
- **Builds:** clippy-clean; `#![no_std]` riscv32imc build was 2,394 B `.text` at this step (2,430 B after P3). That includes an exported `manetta_next_deadline_ms()` (312 B) to prove the deadline query compiles for the board.

**Tests:**
- `PortableCodeTests` (6): Rust and TS output (including the `after()` trigger and `next_deadline_ms()`), balanced-paren rewriting, missing time variable,
  unknown trigger call (`every(ms)`), and a typo (`Light.ONN`) failing at generation time.
  The last one is a real gain over raw target code: StateSmith compiles portable code with Roslyn, so mistakes surface once, before any target compiler runs.
- `ManettaExampleGenTests.RustAndTypeScriptTracesMatch`: Rust == TypeScript, and polling == sleeping for both, with the dispatch counts asserted (now 29 vs 2,850).
- Full suite: 758 pass, and the 92 failures are the same set as on the untouched base commit (environmental, Java and TypeScript Spec2 tooling in this container), nothing new.
  No other language's committed output changed.

**Known limits of the spike:**
- **Only Rust and TypeScript** were taught unsigned wrapping and portable declarations.
  C99 / C++ / C# / Java / Python / JavaScript in portable mode are untested. C99 will likely need struct-field initialisers moved into its ctor.
- **`after()` is level-triggered.** Because it's polled on `do`, `after(ms) [guard]` fires as soon as the guard is true once the time has passed. UML time events are one-shot.
  This suits caps and timeouts; upstream should document it or offer both.
- **Sleeping hosts must still dispatch `do` after an event** if a timer can already be expired while its guard waits on something an event changes.
  `next_deadline_ms()` only reports future expiries. Manetta never hits this (its timer guards don't change while the state is active), and the trace check confirms it.
- **`next_deadline_ms()` only knows `after()` and `elapsed() >=` / `>` forms.** Other time logic still works when polled, but isn't in the deadline.
- **Types are enums only.** The variable parser expects `type name = value;` per line.
- **Portable mode is implied** by the presence of the section; upstream it should be an explicit switch.
  In this mode every piece of diagram code must be valid portable code. That's a strictness win, but existing target-code diagrams can't be mixed in.
- **Cosmetics:** some redundant parentheses around wrapped time expressions, and `self.vars.X_entered_at` names follow the state name casing.
  The `// uml:` comment shows the desugared `do [after(...) && (...)]` form.

**P3 on this branch (see section 5 for the design):**
- **`[RenderConfig.Portable] Functions`:** GIL interface syntax (`void set_pump(bool on);`).
  - StateSmith emits a host interface `{Sm}Host` (`ManettaSmHost`), a `host` field, and a constructor that takes it.
  - Diagram calls to declared functions are rewritten to `this.host.f(...)` at expansion.
  - **Rust:** `pub trait ManettaSmHost` (all methods `&mut self`); `pub struct ManettaSm<H: ManettaSmHost> { ..., pub host: H }`; `impl<H: ManettaSmHost>`; `pub const fn new(host: H)`;
    `impl<H: ManettaSmHost + Default> Default`. Static helpers (`state_id_to_string`, ...) become module functions, so callers don't need a type argument.
  - **TypeScript:** `export interface ManettaSmHost`, and a real `constructor(host: ManettaSmHost)`. Only constructors with parameters change; existing TypeScript output is untouched.
- **`[RenderConfig.Portable] Events`:** `START_BREW(uint max_ms)` generates a `max_ms` variable and `dispatch_start_brew(max_ms)` (TS `dispatchStartBrew`).
  Arguments with the same name share a variable and must have the same type. A declared event that no behaviour uses is an error.
- **Manetta:**
  - pump, power relay and steam heater are host calls (`set_pump(true);`), and the fault-clear choice asked the host (`[power_relay_is_on()]`; the fault restructure later removed both);
  - `cmd_max_ms` became the `max_ms` payload of `START_BREW` / `ENTER_STEAM`;
  - the runners implement a recording `TraceHost` and call the typed dispatchers;
  - the board crate implements a GPIO-style `BoardHost`.
- **Results:** traces are still byte-identical to v1 for Rust and TypeScript, polling and sleeping. The no_std board build was 2,430 B `.text` (2,372 B after the fault restructure), clippy-clean, static dispatch.
- **Tests:** `PortableHostAndPayloadTests` (4). The full suite has 762 passing, and the same 92 environmental failures as the base commit.

**Fault restructure (after P3):** the first design made FAULT a sibling of OFF and ON, so a fault replaced the power state.
- **What broke:** entering FAULT left ON, so auto-off stopped (SPEC 5.9 doesn't exempt faults), `TEMP_*` stopped updating `power`, and the machine's power switch was ignored.
  It also needed a choice point and a `power_relay_is_on()` host query to recover on/off when the fault cleared.
- **Now:**
  - while on, FAULT is a sub state of ON (beside IDLE, BREWING and STEAM), so ON's rules keep working;
  - while off, OFF stays the state, and `phase == Phase.FAULT` is the flag;
  - only FAULT_CLEARED resets the flag, so a fault survives turning off;
  - auto-off is one check on ON, guarded `phase != Phase.BREWING`;
  - TURN_ON and the machine's power switch are refused while off and faulted.
- **Scenarios:** six new ones (13–18) cover auto-off, the power switch, temperature, fault while off, fault during steam, and TURN_OFF during a fault.
  The 18-scenario trace is identical across Rust and TypeScript and across polling and sleeping.
  The first 12 scenarios changed in exactly 4 lines, all intended (listed in the example README).
- **Diagram size:** 188 → 191 lines.

**Next concrete steps:**
1. Hardening for upstream: C99 and C# in portable mode (C99 needs initialisers in its ctor, and host calls as extern functions or a function-pointer struct), plus a Spec2-style portable spec run in several languages.
2. An explicit portable-mode switch instead of "implied by the section".
3. P4 remainder: accept `left to right direction`, an `in(STATE)` guard, event groups.
4. A design discussion upstream (StateSmith/StateSmith#475 for Rust; portable code and time helpers as their own proposal), when you decide to.

## 5. P3 design: how the host gets in, and how events carry data

The question: diagram code like `set_pump(true)` has to reach the board's hardware (or a test mock) in Rust, and the app's code in TypeScript.
The generated machine must stay `no_std`, allocation-free and cheap.

**Rust options considered:**

| Option | Generated shape | For | Against |
|---|---|---|---|
| **A. Owned generic host** (chosen) | `ManettaSm<H: ManettaSmHost> { ..., pub host: H }`, `const fn new(host: H)` | static dispatch, no lifetimes, `const` construction, trivial mocks (`ManettaSm::new(MockHost::default())`), the host can own the peripherals | the machine owns the host, so other code reaches the peripherals through `sm.host`; static helpers can't be associated functions any more (made module functions) |
| B. Host passed per call | `dispatch_event(&mut self, host: &mut impl ManettaSmHost, ev)`, threaded through every handler | the firmware keeps ownership of its peripherals; no type parameter on the struct | every generated function gets an extra parameter; easy to call with the wrong host; more generator work |
| C. Stored reference | `ManettaSm<'a, H> { host: &'a mut H }` | the firmware keeps ownership | lifetime on the struct makes it hard to put in a `static`; awkward in RTIC / embassy tasks |
| D. Trait implemented on the machine by the user | `impl ManettaSmHost for ManettaSm { ... }` in user code | no generics at all | the machine can't own peripherals except as `Vars` fields; mocks need `cfg` tricks |
| E. Trait object | `Box<dyn ManettaSmHost>` | familiar | needs an allocator: ruled out for the board |

**Decision: A.** It's what the board wants (zero-cost, `static`-friendly), and test code gets the most out of it.
B is the natural follow-up if a firmware design really needs to keep the peripherals outside the machine.
It could be a per-diagram setting later (`HostPassing = "owned" | "per_call"`), with the same diagram text.

Details that follow from A:
- **Every trait method takes `&mut self`,** even queries like `door_closed()`. On embedded, reading a pin or sensor usually needs `&mut` (embedded-hal), and one rule is simpler than inferring.
- **No reentrancy.** Host methods only get `&mut` to the host, never to the machine, so a callback can't dispatch an event into a machine that is mid-transition.
  A host that wants to raise an event queues it and dispatches after the current dispatch returns.
- **The host stays reachable** as `sm.host`, a public field, so the firmware can service peripherals between dispatches.
- **Statics move out of the `impl`.** `ManettaSm::state_id_to_string(..)` would need `ManettaSm::<SomeHost>::...`, so static helpers become module functions (`manetta_sm::state_id_to_string`).
- **TypeScript is structural,** so the host is just an interface passed to the constructor. Mocks are plain objects.
- **Other targets:**
  - C# and Java: an interface field;
  - Python: duck typing;
  - C: either `extern` functions the user links in (`void ManettaSm_host_set_pump(ManettaSm* sm, bool on);`) or a struct of function pointers set at init.

  The GIL shape (an interface-typed field plus a constructor parameter) works for all of them.

**Event data:** `START_BREW(uint max_ms)` generates a `max_ms` variable plus a typed `dispatch_start_brew(max_ms)` that stores it and dispatches.
- **Why variables and not real function parameters:** event handlers in GIL take no arguments, and threading arguments through every ancestor handler would touch the core algorithm.
  A stored argument keeps the algorithm unchanged and also lets guards in ancestor states read it.
- **Shared by name:** arguments with the same name share one variable (both commands' `max_ms`), so they must agree on type.
- **Plain dispatch still works:** `dispatch_event(START_BREW)` uses the last stored value. That's handy for replay, but the typed function is the API to use.
