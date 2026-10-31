// Regions semantics test runner. Args: event names. Prints every trace and the active states after each event.
import { RegionsSm, RegionsSmHost, EventId } from "./RegionsSm";

declare const process: { argv: string[] };

class PrintHost implements RegionsSmHost {
    trace(msg: string): void { console.log(`  ${msg}`); }
}

function printStates(sm: RegionsSm) {
    console.log(`  = ${RegionsSm.stateIdToString(sm.stateId)} | ${RegionsSm.stateIdToString(sm.parRegion_1StateId)} | ${RegionsSm.stateIdToString(sm.parRegion_2StateId)} | ${RegionsSm.stateIdToString(sm.a3Region_1StateId)} ${RegionsSm.stateIdToString(sm.a3Region_2StateId)} | ticks=${sm.vars.ticks}`);
}

const sm = new RegionsSm(new PrintHost());
console.log("start");
sm.start();
printStates(sm);
for (const arg of process.argv.slice(2)) {
    console.log(arg);
    sm.dispatchEvent((EventId as any)[arg]);
    printStates(sm);
}
