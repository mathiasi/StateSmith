// Scenario runner (host side). Plays ../scenarios.txt and prints one trace line per step.
// rust/src/main.rs does exactly the same; the two outputs must be identical.

// The enums (Reply, Power, ...) are generated from the diagram's [RenderConfig.Portable] Types.
import { ManettaSm, EventId, Reply, Phase, Power, SteamState, StopReason, StartSource } from "./ManettaSm";

// Minimal node declarations so the example needs no npm install (no @types/node).
declare const process: { argv: string[] };
declare function require(name: string): any;
const fs = require("fs");

const TICK_MS = 100;

function parseEvent(name: string): EventId {
    const ev = (EventId as any)[name];
    if (ev === undefined || name === "DO") throw new Error(`unknown event \`${name}\``);
    return ev as EventId;
}

const SETTABLE = ["cmd_max_ms", "device_max_brew_ms", "device_max_steam_ms", "heartbeat_timeout_ms", "auto_off_min"];

function setVar(sm: ManettaSm, name: string, value: number) {
    if (!SETTABLE.includes(name)) throw new Error(`unknown var \`${name}\``);
    (sm.vars as any)[name] = value;
}

const pad = (s: string, n: number) => s.padEnd(n);
const b = (x: boolean) => (x ? "1" : "0");

/** Everything the trace shows except the reply. Used to detect changes during `wait`. */
function snapshot(sm: ManettaSm): string {
    const v = sm.vars;
    return `${pad(ManettaSm.stateIdToString(sm.stateId), 14)} phase=${pad(Phase[v.phase], 7)} power=${pad(Power[v.power], 7)} ` +
        `steam=${pad(SteamState[v.steam_state], 7)} stop=${pad(StopReason[v.stop_reason], 11)} src=${pad(StartSource[v.start_source], 7)} ` +
        `pump=${b(v.pump_on)} relay=${b(v.power_relay_on)} steam_on=${b(v.steam_on)}`;
}

function printLine(sm: ManettaSm, step: string, reply: string) {
    const t = sm.vars.now_ms;
    const time = `${Math.floor(t / 1000)}.${Math.floor((t % 1000) / 100)}s`;
    console.log(`t=${time.padStart(7)} | ${pad(step, 26)} | reply=${pad(reply, 9)} | ${snapshot(sm)}`);
}

function main() {
    const path = process.argv[2] ?? "../scenarios.txt";
    const text: string = fs.readFileSync(path, "utf8");

    let sm = new ManettaSm();

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
                sm = new ManettaSm();
                sm.start();
                printLine(sm, "start", "-");
                break;
            case "set": {
                const [name, value] = rest.split(" ");
                setVar(sm, name, Number(value));
                break;
            }
            case "cmd":
                sm.vars.reply = Reply.NONE;
                sm.vars.last_write_at = sm.vars.now_ms;
                sm.dispatchEvent(parseEvent(rest));
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
                    waited += TICK_MS;
                    sm.vars.now_ms = (sm.vars.now_ms + TICK_MS) >>> 0;
                    sm.dispatchEvent(EventId.DO);
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
}

main();
