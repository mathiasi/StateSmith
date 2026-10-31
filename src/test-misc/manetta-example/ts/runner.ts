// Scenario runner (host side). Plays ../scenarios.txt and prints one trace line per step.
// rust/src/main.rs does exactly the same; the two outputs must be identical.

// The enums (Reply, Power, ...) are generated from the diagram's [RenderConfig.Portable] Types.
import { ManettaSm, ManettaSmHost, EventId, Reply, Phase, Power, SteamState, StopReason, StartSource } from "./ManettaSm";

// Minimal node declarations so the example needs no npm install (no @types/node).
declare const process: { argv: string[] };
declare function require(name: string): any;
const fs = require("fs");

const TICK_MS = 100;

/** The runner's host: remembers what the machine switched, like the board's GPIO outputs would. */
class TraceHost implements ManettaSmHost {
    pump_on = false;
    power_relay_on = false;
    steam_on = false;
    set_pump(on: boolean): void { this.pump_on = on; }
    set_power_relay(on: boolean): void { this.power_relay_on = on; }
    set_steam_heater(on: boolean): void { this.steam_on = on; }
}

type Sm = ManettaSm & { host: TraceHost };
const newSm = (): Sm => new ManettaSm(new TraceHost()) as Sm;

function parseEvent(name: string): EventId {
    const ev = (EventId as any)[name];
    if (ev === undefined || name === "DO") throw new Error(`unknown event \`${name}\``);
    return ev as EventId;
}

const SETTABLE = ["device_max_brew_ms", "device_max_steam_ms", "heartbeat_timeout_ms", "auto_off_min"];

function setVar(sm: ManettaSm, name: string, value: number) {
    if (!SETTABLE.includes(name)) throw new Error(`unknown var \`${name}\``);
    (sm.vars as any)[name] = value;
}

const pad = (s: string, n: number) => s.padEnd(n);
const b = (x: boolean) => (x ? "1" : "0");

/** Everything the trace shows except the reply. Used to detect changes during `wait`. */
function snapshot(sm: Sm): string {
    const v = sm.vars;
    return `${pad(ManettaSm.stateIdToString(sm.stateId), 14)} phase=${pad(Phase[v.phase], 7)} power=${pad(Power[v.power], 7)} ` +
        `steam=${pad(SteamState[v.steam_state], 7)} stop=${pad(StopReason[v.stop_reason], 11)} src=${pad(StartSource[v.start_source], 7)} ` +
        `pump=${b(sm.host.pump_on)} relay=${b(sm.host.power_relay_on)} steam_on=${b(sm.host.steam_on)}`;
}

function printLine(sm: Sm, step: string, reply: string) {
    const t = sm.vars.now_ms;
    const time = `${Math.floor(t / 1000)}.${Math.floor((t % 1000) / 100)}s`;
    console.log(`t=${time.padStart(7)} | ${pad(step, 26)} | reply=${pad(reply, 9)} | ${snapshot(sm)}`);
}

function main() {
    // `--sleep`: behave like a low-power host. Instead of a `do` tick every 100 ms, sleep until
    // `nextDeadlineMs()` and dispatch `do` only then. The trace must be identical to polling.
    const args = process.argv.slice(2);
    const sleepMode = args.includes("--sleep");
    const path = args.find(a => a !== "--sleep") ?? "../scenarios.txt";
    let doDispatches = 0;
    const text: string = fs.readFileSync(path, "utf8");

    let sm = newSm();
    // the data the app sends with START_BREW / ENTER_STEAM (scenario: `set cmd_max_ms N`)
    let cmdMaxMs = 0;

    for (const raw of text.split(/\r?\n/)) {
        const line = raw.split("#")[0].trim();
        if (line.length === 0) continue;
        const space = line.indexOf(" ");
        const cmd = space < 0 ? line : line.slice(0, space);
        const rest = space < 0 ? "" : line.slice(space + 1);

        switch (cmd) {
            case "scenario":
                console.log();
                console.log(`== ${rest}`);
                sm = newSm();
                cmdMaxMs = 0;
                sm.start();
                printLine(sm, "start", "-");
                break;
            case "set": {
                const [name, value] = rest.split(" ");
                if (name === "cmd_max_ms") cmdMaxMs = Number(value);
                else setVar(sm, name, Number(value));
                break;
            }
            case "cmd":
                sm.vars.reply = Reply.NONE;
                sm.vars.last_write_at = sm.vars.now_ms;
                // commands with data use the generated typed dispatchers
                if (rest === "START_BREW") sm.dispatchStartBrew(cmdMaxMs);
                else if (rest === "ENTER_STEAM") sm.dispatchEnterSteam(cmdMaxMs);
                else sm.dispatchEvent(parseEvent(rest));
                printLine(sm, `cmd ${rest}`, Reply[sm.vars.reply]);
                break;
            case "ev":
                sm.dispatchEvent(parseEvent(rest));
                printLine(sm, `ev ${rest}`, "-");
                break;
            case "wait": {
                const ms = Number(rest);
                let last = snapshot(sm);
                for (let waited = 0; waited < ms; ) {
                    const step = sleepMode ? Math.min(sm.nextDeadlineMs(), ms - waited) : TICK_MS;
                    waited += step;
                    sm.vars.now_ms = (sm.vars.now_ms + step) >>> 0;
                    sm.dispatchEvent(EventId.DO);
                    doDispatches++;
                    const now = snapshot(sm);
                    if (now !== last) {
                        printLine(sm, "do", "-");
                        last = now;
                    }
                }
                printLine(sm, `waited ${ms} ms`, "-");
                break;
            }
            default:
                throw new Error(`bad line \`${line}\``);
        }
    }

    console.error(`do dispatches: ${doDispatches} (${sleepMode ? "sleep until nextDeadlineMs()" : "poll every 100 ms"})`);
}

main();
