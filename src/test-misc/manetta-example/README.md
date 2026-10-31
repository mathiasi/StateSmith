# Manetta example: one diagram, Rust + TypeScript

A cut-down Manetta device state machine, drawn once in PlantUML and generated twice:
- **Rust** (`rust/src/manetta_sm.rs`), using the experimental Rust target from the `rust-spike` branch (see `/RUST_SPIKE.md`);
- **TypeScript** (`ts/ManettaSm.ts`), using StateSmith's existing TypeScript target.

> **Branch `north-star`:** this version uses the experimental **portable code** mode (see `/NORTH_STAR.md`).
> The diagram declares its own types and variables, and all diagram code is translated per language, so there is **no per-language glue at all**.
> The expansion-based first version is on branch `manetta-example`. Its trace is byte-identical to this one.

Both runners play the same `scenarios.txt` and print the same trace. **The two traces are byte-identical** (205 lines, 18 scenarios).
The Rust module also builds as `#![no_std]` for `riscv32imc-unknown-none-elf`.

| | Result |
|---|---|
| Same diagram text for both languages | yes, and nothing else: types, variables and code are all in `ManettaSm.plantuml`. The generator only picks the target |
| Rust trace == TypeScript trace | **identical** (`diff trace-rust.txt trace-ts.txt` is empty; checked by `ManettaExampleGenTests.RustAndTypeScriptTracesMatch`) |
| Low-power host (`--sleep`: sleep until `next_deadline_ms()` instead of a 100 ms `do` tick) | same trace in both languages, **29 `do` dispatches instead of 2,850** |
| Rust warnings | 0 rustc and 0 clippy warnings for the runner crate and the no_std crate |
| TypeScript | `tsc` 6.0 with `strict`, target ES2020: no errors |
| no_std, `riscv32imc-unknown-none-elf`, `opt-level = "s"` | builds. Whole crate object: **2,372 B `.text`** (including an exported `next_deadline_ms()` call and a GPIO-style host), 0 data, 0 bss. The only external symbol is `memset` (from `compiler_builtins`) |

## The diagram

Source: [`ManettaSm.plantuml`](ManettaSm.plantuml). Rendered with PlantUML 1.2024.7 + Graphviz:

![ManettaSm state diagram](ManettaSm.svg)

(`ManettaSm.png` is the same picture. `ManettaSm.sim.html` is StateSmith's interactive simulator. It loads mermaid and svg-pan-zoom from
`cdn.jsdelivr.net`, so it works in a normal browser. I couldn't render it in the sandbox it was built in, because that host is blocked there.)

### Structure

```
ManettaSm (the machine)      PING / STOP_BREW / LEAVE_STEAM answer OK
├── OFF                      relay off. START_BREW, ENTER_STEAM -> NOT_READY; a fault while off is `phase == FAULT` (no state change)
└── ON                       relay on. TEMP_* update `power`; TURN_OFF, power switch, auto-off (not while brewing); FAULT_DETECTED -> FAULT
    ├── IDLE                 starts brews and steam
    ├── BREWING              pump on; command cap, device cap, heartbeat, STOP_BREW, machine switch, link loss, fault
    ├── STEAM                steam heater on, power reports ON; caps, LEAVE_STEAM, button
    │   ├── STEAM_HEATING
    │   └── STEAM_READY
    └── FAULT                START_BREW / ENTER_STEAM / TURN_ON -> FAULT; FAULT_CLEARED -> IDLE
```

Changes from the suggested hierarchy, and why:
- **Behaviours on the machine itself.** The "true everywhere" answers (PING, STOP_BREW and LEAVE_STEAM are always OK) are written on the root: `ManettaSm : PING / reply = Reply.OK;`. The picture draws them as a small `ManettaSm` box.
  Stock StateSmith can't do this from PlantUML; this branch maps the machine's name to the root.
  The earlier versions needed an outer `MANETTA` wrapper state instead.
- **A fault sits beside the power state instead of replacing it.**
  - **While on,** FAULT is a sub state of ON, next to IDLE, BREWING and STEAM. Everything ON does keeps working during a fault: auto-off, temperature (`power`), TURN_OFF and the machine's power switch.
  - **While off,** the machine stays in OFF and `phase` reports FAULT. `phase == Phase.FAULT` is the one fault flag; only FAULT_CLEARED resets it, so a fault survives turning off.
  - An earlier version made FAULT a sibling of OFF and ON. Entering it left ON, which silently stopped auto-off, froze `power` and ignored the power switch.
    It also needed a choice point plus a `power_relay_is_on()` host query to work out where to go when the fault cleared. See "Fault restructure" below.
- **Temperature is not a state.** HEATING, READY and COOLING run in parallel with everything else, so they are the `power`
  variable, set by `TEMP_*` handlers on ON. STEAM overrides those handlers, because in steam mode power reports ON.
  Auto-off is a timestamp plus a `do` guard.

### How one diagram text serves both languages

The diagram's `$CONFIG` block (a kept PlantUML comment, invisible in the picture) declares the types and variables once:

```toml
[RenderConfig.Portable]
Types = """
enum Reply { NONE, OK, BUSY, NOT_READY, FAULT, INVALID }
enum Power { OFF, HEATING, READY, COOLING, ON }
...
"""
Functions = """
void set_pump(bool on);              // brew pump
void set_power_relay(bool on);
...
"""
Events = """
START_BREW(uint max_ms)              // command cap for this brew
ENTER_STEAM(uint max_ms)
"""
Variables = """
uint now_ms = 0;                     // monotonic ms, set before every dispatch
Reply reply = Reply.NONE;            // answer to the last command
...
"""
```

All action and guard code is portable code, which StateSmith translates for each language:

| In the diagram | Rust | TypeScript |
|---|---|---|
| `reply = Reply.OK;` | `self.vars.reply = Reply::OK;` | `this.vars.reply = Reply.OK;` |
| `power != Power.HEATING` | `self.vars.power != Power::HEATING` | `this.vars.power != Power.HEATING` |
| `after(brew_cmd_max_ms)` trigger (time since BREWING was entered; sugar for `do [after(...)]`) | `self.vars.now_ms.wrapping_sub(self.vars.BREWING_entered_at) >= self.vars.brew_cmd_max_ms` | `((this.vars.now_ms - this.vars.BREWING_entered_at) >>> 0) >= this.vars.brew_cmd_max_ms` |
| `elapsed(last_write_at)` | `self.vars.now_ms.wrapping_sub(self.vars.last_write_at)` | `((this.vars.now_ms - this.vars.last_write_at) >>> 0)` |
| `auto_off_min * 60000` | `self.vars.auto_off_min.wrapping_mul(60000)` | `(Math.imul(this.vars.auto_off_min, 60000) >>> 0)` |

How the pieces fit:
- **Enums** are generated into both outputs from the `Types` declaration: in Rust with `#[repr(u8)]`, derives and `*_COUNT`; in TypeScript as `export enum`.
  The runners import them from the generated module.
- **`after()`** makes StateSmith add a `BREWING_entered_at` variable and an entry action that stamps it.
  `BREWING --> IDLE : 1. after(brew_cmd_max_ms) [start_source == StartSource.APP] / ...` is a trigger; it's checked on each `do`.
- **Host functions** (`[RenderConfig.Portable] Functions`) are how the machine touches hardware: `set_pump(true);`, `set_power_relay(false);`.
  Rust gets a `ManettaSmHost` trait, and the machine is generic over it (`ManettaSm<H: ManettaSmHost>`, `const fn new(host)`). TypeScript gets an interface passed to the constructor.
  The runners use a `TraceHost` that records the outputs; the no_std crate uses a GPIO-style `BoardHost`.
- **Event data** (`[RenderConfig.Portable] Events`): `START_BREW(uint max_ms)` generates `dispatch_start_brew(max_ms)` (TS `dispatchStartBrew`), and diagram code reads `max_ms`.
- **`next_deadline_ms()`** is generated from those timers. A host can sleep that long instead of ticking `do` every 100 ms.
  Both runners do this with `--sleep`; the traces stay identical.
- **Unsigned arithmetic** wraps like the device's u32 counter in both languages.
- **The generator test** ([`ManettaExampleGenTests.cs`](../../StateSmithTest/Output/Rust/ManettaExampleGenTests.cs)) has no render config, no expansions
  and no variable table. It only chooses the target language and output folder.

Generated code for the same behaviour, IDLE handling START_BREW (step comments trimmed):

```rust
fn idle_start_brew(&mut self) {
    let mut consume_event: bool = false;

    // uml: 1. START_BREW [max_ms == 0] / { reply = Reply.INVALID; }
    if self.vars.max_ms == 0 {
        consume_event = true;
        self.vars.reply = Reply::INVALID;
    }

    // uml: 2. START_BREW [max_ms != 0 && power != Power.READY] / { reply = Reply.NOT_READY; }
    if self.vars.max_ms != 0 && self.vars.power != Power::READY {
        consume_event = true;
        self.vars.reply = Reply::NOT_READY;
    }

    // uml: 3. START_BREW [max_ms != 0 && power == Power.READY] / { reply = Reply.OK; start_source = StartSource.APP; brew_cmd_max_ms = max_ms; } TransitionTo(BREWING)
    if self.vars.max_ms != 0 && self.vars.power == Power::READY {
        self.idle_exit();
        self.vars.reply = Reply::OK; self.vars.start_source = StartSource::APP; self.vars.brew_cmd_max_ms = self.vars.max_ms;
        self.brewing_enter();
        return;
    }

    // Check if event has been consumed before calling ancestor handler.
    if !consume_event {
        self.on_start_brew(); // ON answers BUSY
    }
}
```

```ts
private IDLE_start_brew(): void
{
    let consume_event: boolean = false;
    // uml: 1. START_BREW [max_ms == 0] / { reply = Reply.INVALID; }
    if (this.vars.max_ms == 0)
    {
        consume_event = true;
        this.vars.reply = Reply.INVALID;
    }
    // uml: 2. START_BREW [max_ms != 0 && power != Power.READY] / { reply = Reply.NOT_READY; }
    if (this.vars.max_ms != 0 && this.vars.power != Power.READY)
    {
        consume_event = true;
        this.vars.reply = Reply.NOT_READY;
    }
    // uml: 3. START_BREW [max_ms != 0 && power == Power.READY] / { ... } TransitionTo(BREWING)
    if (this.vars.max_ms != 0 && this.vars.power == Power.READY)
    {
        this.IDLE_exit();
        this.vars.reply = Reply.OK; this.vars.start_source = StartSource.APP; this.vars.brew_cmd_max_ms = this.vars.max_ms;
        this.BREWING_enter();
        return;
    }
    if (!consume_event)
    {
        this.ON_start_brew();
    }
}
```

Generated sizes: `manetta_sm.rs` is 1,541 lines and `ManettaSm.ts` 1,633, mostly comments that echo the diagram.

## What the diagram covers

All of the requested scope:
- **Commands:** TURN_ON, TURN_OFF, START_BREW, STOP_BREW, ENTER_STEAM, LEAVE_STEAM, PING.
- **Machine events:** the brew switch, steam button, power switch, TEMP_*, FAULT_DETECTED, FAULT_CLEARED, LINK_LOST.
- **`do` every 100 ms tick.**
- **Every output:** `reply`, `phase`, `power`, `steam_state`, `stop_reason`, `start_source`, `pump_on`, `power_relay_on`, `steam_on`.

Choices made where the scope left room:
- **START_BREW while heating:** NOT_READY. A brew needs `power == READY`, so COOLING after steam is NOT_READY too.
- **START_BREW / ENTER_STEAM with `max_ms == 0`:** INVALID. A value above the device cap is accepted, and the device cap wins.
- **Steam HEATING → READY:** on `TEMP_READY`, not a device-chosen time.
- **ENTER_STEAM while heating:** NOT_READY. While READY or COOLING it's OK.
- **Two brew caps due on the same tick:** the command cap wins and is reported, by ordering (`1. do` before `2. do`). Steam works the same way.
- **The machine's brew switch:** `MACHINE_BREW_SWITCH_ON` in IDLE starts a brew whatever the temperature (it's a physical switch),
  with `start_source = MACHINE`, which ignores link loss and heartbeat. `MACHINE_BREW_SWITCH_OFF` stops any brew with `MACHINE`.
- **The steam button:** toggles steam (entering it needs `power != Power.HEATING`). A button-started steam's command cap is the device cap.
- **The power switch:** toggles power. During a brew it stops the brew with `MACHINE`.
- **TURN_ON while already on:** OK, and it restarts the auto-off clock ("the later of the last TURN_ON and the end of the last brew").
- **Auto-off:** one check on ON, `do [phase != Phase.BREWING && elapsed(auto_off_from) >= auto_off_min * 60000]`.
  It fires from IDLE, STEAM (steam doesn't hold it off) and FAULT (SPEC 5.9 doesn't exempt faults), never during a brew.
- **Faults:**
  - a fault leaves the power relay as it was;
  - pump and steam heater go off through BREWING and STEAM's exit actions;
  - during a fault, START_BREW, ENTER_STEAM and TURN_ON get FAULT; TURN_OFF, STOP_BREW, LEAVE_STEAM and PING get OK;
  - TURN_OFF and the power switch turn the machine off with the fault still active; clearing the fault then leaves it off;
  - clearing a fault while on goes to IDLE without re-entering ON, so `power` keeps reporting the real temperature.
- **TURN_ON during a fault while off: refused** (reply FAULT, stays off). The machine's own power switch is ignored too.
  Energising the heater of a machine that reports a fault is the unsafe choice, and it matches the answer while on (TURN_ON gets FAULT there too).
  Once the fault clears, both work again.
- **`stop_reason`** is only set for brews. How steam ended is not reported; the scope only lists brew reasons.
- **`Reply::NONE`:** an extra value. The host resets `reply` before each command, so "no answer" would be visible (it never happens in the traces).
  `StopReason::NONE` and `StartSource::NONE` are the initial values.

Left out, as asked: BLE itself, the per-connection seq replay cache, notification pacing and multiple connections.
Also left out:
- real temperature control (the `TEMP_*` events stand in for it);
- range checks on the device config (heartbeat 5 to 60 s, brew cap of at most 30 min), which belong where the config is set;
- persistence.

## Traces

Format: time, step, reply (for commands), then leaf state and status fields.
- `wait` prints a line for every change during the wait, then one line when the wait ends.
- `cmd` lines are BLE writes: before dispatching, the runner resets `reply` and sets `last_write_at = now_ms`.

<details>
<summary>Full trace (identical from <code>rust/</code> and <code>ts/</code>)</summary>

```
== 1. normal brew, stopped by the command cap
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=NOT_READY | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   4.0s | waited 4000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   4.0s | cmd PING                   | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   8.0s | waited 4000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   8.0s | cmd PING                   | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  12.0s | waited 4000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  12.0s | cmd PING                   | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  16.0s | waited 4000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  16.0s | cmd PING                   | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  20.0s | waited 4000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  20.0s | cmd PING                   | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  24.0s | waited 4000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  24.0s | cmd PING                   | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  25.0s | do                         | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=COMMAND_CAP src=APP     pump=0 relay=1 steam_on=0
t=  26.0s | waited 2000 ms             | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=COMMAND_CAP src=APP     pump=0 relay=1 steam_on=0

== 2. heartbeat timeout (app goes quiet)
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  10.0s | do                         | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=HEARTBEAT   src=APP     pump=0 relay=1 steam_on=0
t=  12.0s | waited 12000 ms            | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=HEARTBEAT   src=APP     pump=0 relay=1 steam_on=0

== 3. STOP_BREW, and STOP_BREW is OK even when nothing is brewing
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   3.0s | waited 3000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   3.0s | cmd STOP_BREW              | reply=OK        | IDLE           phase=IDLE    power=READY   steam=OFF     stop=APP         src=APP     pump=0 relay=1 steam_on=0
t=   3.0s | cmd STOP_BREW              | reply=OK        | IDLE           phase=IDLE    power=READY   steam=OFF     stop=APP         src=APP     pump=0 relay=1 steam_on=0

== 4. TURN_OFF during a brew is BUSY
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   1.0s | waited 1000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   1.0s | cmd TURN_OFF               | reply=BUSY      | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   1.0s | cmd STOP_BREW              | reply=OK        | IDLE           phase=IDLE    power=READY   steam=OFF     stop=APP         src=APP     pump=0 relay=1 steam_on=0
t=   1.0s | cmd TURN_OFF               | reply=OK        | OFF            phase=IDLE    power=OFF     steam=OFF     stop=APP         src=APP     pump=0 relay=0 steam_on=0

== 5. steam, then cooling, then a brew
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=NOT_READY | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=OK        | STEAM_HEATING  phase=IDLE    power=ON      steam=HEATING stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   0.0s | ev TEMP_HEATING            | reply=-         | STEAM_HEATING  phase=IDLE    power=ON      steam=HEATING stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   0.0s | ev TEMP_READY              | reply=-         | STEAM_READY    phase=IDLE    power=ON      steam=READY   stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   5.0s | waited 5000 ms             | reply=-         | STEAM_READY    phase=IDLE    power=ON      steam=READY   stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   5.0s | cmd LEAVE_STEAM            | reply=OK        | IDLE           phase=IDLE    power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   5.0s | cmd START_BREW             | reply=NOT_READY | IDLE           phase=IDLE    power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   5.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   5.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  10.0s | waited 5000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  10.0s | cmd STOP_BREW              | reply=OK        | IDLE           phase=IDLE    power=READY   steam=OFF     stop=APP         src=APP     pump=0 relay=1 steam_on=0

== 6. ENTER_STEAM during a brew is BUSY
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=BUSY      | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   0.0s | cmd STOP_BREW              | reply=OK        | IDLE           phase=IDLE    power=READY   steam=OFF     stop=APP         src=APP     pump=0 relay=1 steam_on=0

== 7. auto-off counts from the end of the last brew
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=  50.0s | waited 50000 ms            | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=  50.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  53.0s | waited 3000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  53.0s | cmd PING                   | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  56.0s | waited 3000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=  56.0s | cmd STOP_BREW              | reply=OK        | IDLE           phase=IDLE    power=READY   steam=OFF     stop=APP         src=APP     pump=0 relay=1 steam_on=0
t= 116.0s | do                         | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=APP         src=APP     pump=0 relay=0 steam_on=0
t= 117.0s | waited 61000 ms            | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=APP         src=APP     pump=0 relay=0 steam_on=0

== 8. a fault during a brew
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   2.0s | waited 2000 ms             | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   2.0s | ev FAULT_DETECTED          | reply=-         | FAULT          phase=FAULT   power=READY   steam=OFF     stop=FAULT       src=APP     pump=0 relay=1 steam_on=0
t=   2.0s | cmd START_BREW             | reply=FAULT     | FAULT          phase=FAULT   power=READY   steam=OFF     stop=FAULT       src=APP     pump=0 relay=1 steam_on=0
t=   2.0s | cmd TURN_ON                | reply=FAULT     | FAULT          phase=FAULT   power=READY   steam=OFF     stop=FAULT       src=APP     pump=0 relay=1 steam_on=0
t=   2.0s | ev FAULT_CLEARED           | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=FAULT       src=APP     pump=0 relay=1 steam_on=0
t=   2.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=FAULT       src=APP     pump=0 relay=1 steam_on=0
t=   2.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   2.0s | cmd STOP_BREW              | reply=OK        | IDLE           phase=IDLE    power=READY   steam=OFF     stop=APP         src=APP     pump=0 relay=1 steam_on=0

== 9. steam: a repeat ENTER_STEAM replaces the command cap, the device cap counts from when steam began, link loss doesn't stop it
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=OK        | STEAM_HEATING  phase=IDLE    power=ON      steam=HEATING stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   8.0s | waited 8000 ms             | reply=-         | STEAM_HEATING  phase=IDLE    power=ON      steam=HEATING stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   8.0s | cmd ENTER_STEAM            | reply=OK        | STEAM_HEATING  phase=IDLE    power=ON      steam=HEATING stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   8.0s | ev LINK_LOST               | reply=-         | STEAM_HEATING  phase=IDLE    power=ON      steam=HEATING stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=  20.0s | do                         | reply=-         | IDLE           phase=IDLE    power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=  33.0s | waited 25000 ms            | reply=-         | IDLE           phase=IDLE    power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0

== 10. a brew started with the machine's switch ignores link loss and heartbeat
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev MACHINE_BREW_SWITCH_ON  | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=MACHINE pump=1 relay=1 steam_on=0
t=   0.0s | ev LINK_LOST               | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=MACHINE pump=1 relay=1 steam_on=0
t=  15.0s | waited 15000 ms            | reply=-         | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=MACHINE pump=1 relay=1 steam_on=0
t=  15.0s | ev MACHINE_BREW_SWITCH_OFF | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=MACHINE     src=MACHINE pump=0 relay=1 steam_on=0

== 11. link loss stops an app brew; invalid arguments; off answers NOT_READY
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd START_BREW             | reply=NOT_READY | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=NOT_READY | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=INVALID   | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=INVALID   | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd START_BREW             | reply=OK        | BREWING        phase=BREWING power=READY   steam=OFF     stop=NONE        src=APP     pump=1 relay=1 steam_on=0
t=   0.0s | ev LINK_LOST               | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=LINK_LOST   src=APP     pump=0 relay=1 steam_on=0

== 12. a fault while off, TURN_OFF in fault, clearing back to off
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev FAULT_DETECTED          | reply=-         | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_OFF               | reply=OK        | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev FAULT_CLEARED           | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev FAULT_DETECTED          | reply=-         | FAULT          phase=FAULT   power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd TURN_OFF               | reply=OK        | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev FAULT_CLEARED           | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0

== 13. fault while on and idle, then auto-off fires during the fault
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=  10.0s | waited 10000 ms            | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=  10.0s | ev FAULT_DETECTED          | reply=-         | FAULT          phase=FAULT   power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=  60.0s | do                         | reply=-         | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=  65.0s | waited 55000 ms            | reply=-         | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=  65.0s | cmd TURN_ON                | reply=FAULT     | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=  65.0s | ev FAULT_CLEARED           | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=  65.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0

== 14. the machine's power switch during a fault
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev FAULT_DETECTED          | reply=-         | FAULT          phase=FAULT   power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev MACHINE_POWER_SWITCH    | reply=-         | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev MACHINE_POWER_SWITCH    | reply=-         | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev FAULT_CLEARED           | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev MACHINE_POWER_SWITCH    | reply=-         | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0

== 15. temperature events during a fault update power
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev FAULT_DETECTED          | reply=-         | FAULT          phase=FAULT   power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | FAULT          phase=FAULT   power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_TOO_HOT            | reply=-         | FAULT          phase=FAULT   power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev FAULT_CLEARED           | reply=-         | IDLE           phase=IDLE    power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0

== 16. fault while off, then cleared
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev FAULT_DETECTED          | reply=-         | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd START_BREW             | reply=FAULT     | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=FAULT     | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=FAULT     | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd STOP_BREW              | reply=OK        | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd LEAVE_STEAM            | reply=OK        | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev FAULT_CLEARED           | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd START_BREW             | reply=NOT_READY | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0

== 17. fault during steam
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=OK        | STEAM_HEATING  phase=IDLE    power=ON      steam=HEATING stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   0.0s | ev TEMP_READY              | reply=-         | STEAM_READY    phase=IDLE    power=ON      steam=READY   stop=NONE        src=NONE    pump=0 relay=1 steam_on=1
t=   0.0s | ev FAULT_DETECTED          | reply=-         | FAULT          phase=FAULT   power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd LEAVE_STEAM            | reply=OK        | FAULT          phase=FAULT   power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=FAULT     | FAULT          phase=FAULT   power=COOLING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | FAULT          phase=FAULT   power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev FAULT_CLEARED           | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd ENTER_STEAM            | reply=OK        | STEAM_HEATING  phase=IDLE    power=ON      steam=HEATING stop=NONE        src=NONE    pump=0 relay=1 steam_on=1

== 18. TURN_OFF during a fault, then the fault clears (stays off)
t=   0.0s | start                      | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | cmd TURN_ON                | reply=OK        | IDLE           phase=IDLE    power=HEATING steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev TEMP_READY              | reply=-         | IDLE           phase=IDLE    power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | ev FAULT_DETECTED          | reply=-         | FAULT          phase=FAULT   power=READY   steam=OFF     stop=NONE        src=NONE    pump=0 relay=1 steam_on=0
t=   0.0s | cmd TURN_OFF               | reply=OK        | OFF            phase=FAULT   power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   0.0s | ev FAULT_CLEARED           | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
t=   1.0s | waited 1000 ms             | reply=-         | OFF            phase=IDLE    power=OFF     steam=OFF     stop=NONE        src=NONE    pump=0 relay=0 steam_on=0
```

</details>

Highlights:
- **Command cap (scenario 1):** `cmd_max_ms` = 25000 with a PING every 4 s, and the brew stops at exactly 25.0 s with `COMMAND_CAP`.
- **Heartbeat (scenario 2):** no writes after START_BREW, and the brew stops at 10.0 s with `HEARTBEAT`.
- **TURN_OFF during a brew (scenario 4):** BUSY. After STOP_BREW it's OK.
- **Steam (scenario 5):**
  - ENTER_STEAM is NOT_READY while heating, then OK;
  - in steam mode `TEMP_HEATING` is ignored, and `TEMP_READY` moves to STEAM_READY;
  - after LEAVE_STEAM power is COOLING and START_BREW is NOT_READY, until `TEMP_READY` makes a brew OK.
- **Auto-off (scenario 7):** auto-off of 1 min, brew ends at 56 s, and the machine turns off at 116.0 s.
- **Fault (scenario 8):** a fault mid-brew stops it with `FAULT`, and the pump goes off.
- **Steam caps and link loss (scenario 9):**
  - a repeat ENTER_STEAM at 8 s replaces the 10 s command cap with 30 s;
  - the device cap (20 s, counted from steam start) ends steam at 20.0 s;
  - LINK_LOST at 8 s doesn't end it.
- **Machine-started brew (scenario 10):** survives LINK_LOST and 15 s without writes.
- **Auto-off during a fault (13):** fault at 10 s while idle, and auto-off still turns the machine off at 60.0 s (`phase` stays FAULT).
  TURN_ON is refused with FAULT until the fault clears.
- **Power switch during a fault (14):** it turns the machine off; pressed again while still faulted it's ignored; after FAULT_CLEARED it turns on.
- **Temperature during a fault (15):** `TEMP_READY` and `TEMP_TOO_HOT` move `power` to READY and COOLING while in FAULT.
- **Fault while off (16):** the state stays OFF with `phase=FAULT`. START_BREW, ENTER_STEAM and TURN_ON get FAULT, while STOP_BREW and LEAVE_STEAM get OK. After clearing, OFF behaves normally.
- **Fault during steam (17):** steam is left (heater off, `power=COOLING`). LEAVE_STEAM is OK and ENTER_STEAM is FAULT; after clearing, ENTER_STEAM works again.
- **TURN_OFF during a fault (18):** off with `phase=FAULT`; clearing the fault leaves it off.

**What changed in the first 12 scenarios after the fault restructure:** 4 lines, all intended.
- **Scenario 8, `ev FAULT_CLEARED`:** `power` is READY, was HEATING. Clearing no longer re-enters ON (which reset `power`), so it keeps reporting the last temperature event.
- **Scenario 12, first `ev FAULT_DETECTED` and `cmd TURN_OFF`:** the state is `OFF` with `phase=FAULT`, was a separate `FAULT` state.
- **Scenario 12, second `cmd TURN_OFF`:** the state is `OFF` with `phase=FAULT`, was `FAULT`. Relay, power and reply are unchanged.

### Fault restructure: before and after

Before (FAULT replaced the power state: 13 lines incl. a choice point and a host query):

```
state FAULT
state AFTER_FAULT <<choice>>
ManettaSm --> FAULT : FAULT_DETECTED
IDLE --> OFF : do [elapsed(auto_off_from) >= auto_off_min * 60000]
STEAM --> OFF : 3. do [elapsed(auto_off_from) >= auto_off_min * 60000]
FAULT : enter / phase = Phase.FAULT;
FAULT : exit / phase = Phase.IDLE;
FAULT : (TURN_ON, START_BREW, ENTER_STEAM) / reply = Reply.FAULT;
FAULT : TURN_OFF / reply = Reply.OK; set_power_relay(false); power = Power.OFF;
FAULT : FAULT_DETECTED
FAULT --> AFTER_FAULT : FAULT_CLEARED
AFTER_FAULT --> ON : [power_relay_is_on()]
AFTER_FAULT --> OFF : else
```

After (FAULT inside ON; a fault while off is the `phase` flag; one auto-off check):

```
state ON { ... state FAULT }
ON --> FAULT : FAULT_DETECTED
ON --> OFF : do [phase != Phase.BREWING && elapsed(auto_off_from) >= auto_off_min * 60000]
FAULT : enter / phase = Phase.FAULT;
FAULT : (TURN_ON, START_BREW, ENTER_STEAM) / reply = Reply.FAULT;
FAULT : FAULT_DETECTED
FAULT --> IDLE : FAULT_CLEARED / phase = Phase.IDLE;

OFF : 1. (TURN_ON, START_BREW, ENTER_STEAM) [phase == Phase.FAULT] / reply = Reply.FAULT;
OFF : 2. (START_BREW, ENTER_STEAM) [phase != Phase.FAULT] / reply = Reply.NOT_READY;
OFF --> ON : 3. TURN_ON [phase != Phase.FAULT] / reply = Reply.OK;
OFF --> ON : MACHINE_POWER_SWITCH [phase != Phase.FAULT]
OFF : FAULT_DETECTED / phase = Phase.FAULT;
OFF : FAULT_CLEARED / phase = Phase.IDLE;
```

The diagram file went from 188 to 191 lines. The fault rules are about the same size: OFF gained the fault-flag lines that the separate FAULT state used to cover,
and a few comments explain the structure. The gains are in behaviour (the three bugs above).
The choice point, the duplicated auto-off check, the duplicated OFF entry actions and the `power_relay_is_on()` host query are all gone.

## What felt awkward

Status on this branch: ✅ fixed by portable code, ◐ partly, ◻ still open (see `/NORTH_STAR.md` for the plan).

1. ✅ **Time is polling, not timers.** Now `after(brew_cmd_max_ms)` is a trigger that records the entry time itself.
   `next_deadline_ms()` lets the host sleep between timers (29 `do` dispatches instead of 2,850 in these scenarios).
   - Was: StateSmith had no `after(25 s)` trigger. Every cap is a guard on the `do` event, re-evaluated each tick, so a cap's resolution is the tick (100 ms here).
   - Was: each one needs its own "started at" timestamp variable, set in an entry action. (`after()` now does this; re-armable timers such as the steam command cap still use a timestamp and `elapsed()`.)
   - Was: the 30-minute device cap meant 18,000 `do` dispatches, each running every guard of every active state. A polling host still does that; a sleeping host wakes once.
   - Still true: which reason is reported when two caps expire on the same tick depends on the `1.` / `2.` ordering numbers. That's a subtle thing to encode in edge labels.
2. ✅ **Wrapping time needs an expansion.** Now built in: `elapsed()` / `after()`, and unsigned math wraps in every language.
   Was: `now_ms - t` would panic on wraparound in debug Rust and is a float in TypeScript, so a hand-written `elapsed()` expansion was needed per language.
3. ◻ **"Any write counts as a heartbeat" can't be said once in the diagram.** Kept as a host concern on purpose.
   - A child's handler for an event consumes it, so an ancestor can't watch "every command".
   - StateSmith has no "on any event" or orthogonal-region hook.
   - The host sets `last_write_at` on every BLE write instead. That's arguably the right place, but it's behaviour outside the diagram.
4. ◻ **No `else` for event handlers.**
   - When guarded behaviours in one state handle the same event, *every* passing guard runs; StateSmith keeps evaluating after an action-only behaviour.
   - So IDLE's three START_BREW behaviours need mutually exclusive guards, which repeats `max_ms != 0 &&`.
   - A choice point would give `else`, but its outgoing transitions would exit and re-enter IDLE just to answer a command.
5. ◻ **Parallel concerns become "do nothing" overrides.** Steam must hide the brew temperature events from ON, which takes a bare
   `STEAM : (TEMP_HEATING, TEMP_READY, TEMP_TOO_HOT)` behaviour. It reads oddly unless you know that a behaviour with no action still consumes the event.
6. ◐ **No root behaviours in PlantUML.** Now `ManettaSm : PING / ...` attaches to the machine itself, and the outer `MANETTA` wrapper state is gone.
   Still open: the grammar rejects `left to right direction`, which this picture needs to stay readable, so it sits in a
   `' <StateSmith:ignore>` block. (`skinparam` lines are accepted.)
7. ✅ **Enums can't be written portably.** Now `reply = Reply.OK;`, with the enum declared once in the diagram and generated for both languages.
   - Was: `Reply::OK` vs `Reply.OK` forced `set_reply(OK)` / `power_is(READY)` expansions.
   - Was: expansion arguments were untyped text. Now a typo like `Reply.OKK` fails at generation time, because StateSmith compiles the portable code with Roslyn.
   - Was: the enum definitions were written twice by hand.
8. ✅ **Variable declarations aren't language neutral.** Now declared once in the diagram; the C# glue is gone.
   - Was: the generator kept one C# table and rendered it per language, so the glue (expansions, var table) was a third language in a Rust and TypeScript build.
9. ◐ **Commands answer through a `reply` variable.** Event data is now typed (`dispatch_start_brew(max_ms)`); the reply is still a variable. It works, but every state path must remember to set `reply`:
   the root, ON and OFF hold defaults so nothing slips through. Was: event arguments (`cmd_max_ms`) were written to a variable before the dispatch.
10. ◐ **Naming compromise in Rust.** SCREAMING_CASE enum variants keep the diagram text identical for both languages, at the cost of
    `#![allow(...)]` lints. These now sit in the generated file, where the enums live.

Nothing in the requested scope was impossible to express. The hard parts were ergonomics rather than missing semantics. Portable code and the time helpers remove 1, 2, 7 and 8.

## Does the lack of orthogonal regions hurt?

**For this cut-down scope: not much, once the hierarchy is right.** One machine plus variables covers it.
The first fault design shows the risk, though: a parallel concern (fault) was modelled as a state that *replaces* another (power).
That silently dropped three ON rules (auto-off, temperature, power switch) and nothing in the tool flagged it. The trace tests only caught it once scenarios for it existed.

What works without regions here:
- **Power and phase overlap only at a few points**, so "FAULT inside ON, plus a `phase` flag while OFF" captures it with no duplicated rules.
- **Temperature is a variable with three handlers.**
- **Auto-off is one guarded check.**

What still shows the seams:
- **`phase` is a hand-maintained flag** that encodes "a fault is active" across OFF and ON. A region would make that a state.
- **STEAM blocks the temperature events** with a do-nothing behaviour.
- **The auto-off guard has to know about BREWING** (`phase != Phase.BREWING`).

**When regions would be worth it:** when the parallel parts get many rules *between* them.
- The obvious next case is a dual-boiler machine, where the steam boiler really does run in parallel with brewing. Steam would have its own heating, ready and fault life cycle, interacting with brew rules (power budget, shared pump).
- As variables and guards, every cross rule becomes a guard on both sides, and it gets easy to miss one, as the first fault design did.
- With regions, each part keeps its own states, and the cross rules become explicit `in(STATE)` guards or internal events.

That's roadmap P5 in `/NORTH_STAR.md`. For now, scenario tests per parallel concern are the safety net.

## Regenerate and run

Setup: .NET SDK (the tests target net9.0; with SDK 10 set `DOTNET_ROLL_FORWARD=Major`), Rust stable, `tsc` and node, and
`rustup target add riscv32imc-unknown-none-elf`.

```bash
cd src/StateSmithTest
dotnet test --filter "FullyQualifiedName~ManettaExampleGenTests"   # regenerates Rust + TS + simulator, runs both, compares traces
```

```bash
cd src/test-misc/manetta-example/rust && cargo run > ../trace-rust.txt
```

```bash
cd src/test-misc/manetta-example/ts && tsc -p . && node dist/runner.js > ../trace-ts.txt
```

```bash
cd src/test-misc/manetta-example && diff trace-rust.txt trace-ts.txt && echo identical
```

```bash
cd src/test-misc/manetta-example/rust && cargo run -- --sleep | diff - ../trace-rust.txt && echo "sleeping host == polling host"
```

```bash
cd src/test-misc/manetta-example/rust-nostd && cargo build --release --target riscv32imc-unknown-none-elf
```

```bash
cd src/test-misc/manetta-example && java -jar plantuml.jar -tsvg ManettaSm.plantuml   # needs Graphviz
```

Files:
- `ManettaSm.plantuml`: the source diagram. `ManettaSm.svg` / `.png` are the picture; `ManettaSm.sim.html` is the simulator.
- `scenarios.txt`: the scenarios both runners play. `trace-rust.txt` and `trace-ts.txt` are their outputs.
- `rust/`:
  - `src/manetta_sm.rs`: generated;
  - `src/main.rs`: the scenario runner.
- `rust-nostd/`: `#![no_std]` staticlib for riscv32imc. It uses the generated `manetta_sm.rs` via `#[path]`, with a GPIO-style `BoardHost`.
- `ts/`:
  - `ManettaSm.ts`: generated;
  - `runner.ts`: the scenario runner;
  - `tsconfig.json`: ES2020, strict, no npm dependencies.
- [`../../StateSmithTest/Output/Rust/ManettaExampleGenTests.cs`](../../StateSmithTest/Output/Rust/ManettaExampleGenTests.cs): the generator
  (target selection only) and the trace comparison test.
