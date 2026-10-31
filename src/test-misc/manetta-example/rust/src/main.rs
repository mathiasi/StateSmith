//! Scenario runner (host side, uses std). Plays `../scenarios.txt` and prints one trace line per step.
//! `ts/runner.ts` does exactly the same; the two outputs must be identical.

mod manetta_sm;

// The enums (Reply, Power, ...) are generated from the diagram's [RenderConfig.Portable] Types.
use manetta_sm::{EventId, ManettaSm, ManettaSmHost, Reply};

const TICK_MS: u32 = 100;

/// The runner's host: remembers what the machine switched, like the board's GPIO outputs would.
#[derive(Default)]
struct TraceHost {
    pump_on: bool,
    power_relay_on: bool,
    steam_on: bool,
}

impl ManettaSmHost for TraceHost {
    fn set_pump(&mut self, on: bool) {
        self.pump_on = on;
    }
    fn set_power_relay(&mut self, on: bool) {
        self.power_relay_on = on;
    }
    fn set_steam_heater(&mut self, on: bool) {
        self.steam_on = on;
    }
    fn power_relay_is_on(&mut self) -> bool {
        self.power_relay_on
    }
}

type Sm = ManettaSm<TraceHost>;

fn parse_event(name: &str) -> EventId {
    match name {
        "TURN_ON" => EventId::TURN_ON,
        "TURN_OFF" => EventId::TURN_OFF,
        "START_BREW" => EventId::START_BREW,
        "STOP_BREW" => EventId::STOP_BREW,
        "ENTER_STEAM" => EventId::ENTER_STEAM,
        "LEAVE_STEAM" => EventId::LEAVE_STEAM,
        "PING" => EventId::PING,
        "MACHINE_BREW_SWITCH_ON" => EventId::MACHINE_BREW_SWITCH_ON,
        "MACHINE_BREW_SWITCH_OFF" => EventId::MACHINE_BREW_SWITCH_OFF,
        "MACHINE_STEAM_BUTTON" => EventId::MACHINE_STEAM_BUTTON,
        "MACHINE_POWER_SWITCH" => EventId::MACHINE_POWER_SWITCH,
        "TEMP_HEATING" => EventId::TEMP_HEATING,
        "TEMP_READY" => EventId::TEMP_READY,
        "TEMP_TOO_HOT" => EventId::TEMP_TOO_HOT,
        "FAULT_DETECTED" => EventId::FAULT_DETECTED,
        "FAULT_CLEARED" => EventId::FAULT_CLEARED,
        "LINK_LOST" => EventId::LINK_LOST,
        _ => panic!("unknown event `{}`", name),
    }
}

fn set_var(sm: &mut Sm, name: &str, value: u32) {
    let v = &mut sm.vars;
    match name {
        "device_max_brew_ms" => v.device_max_brew_ms = value,
        "device_max_steam_ms" => v.device_max_steam_ms = value,
        "heartbeat_timeout_ms" => v.heartbeat_timeout_ms = value,
        "auto_off_min" => v.auto_off_min = value,
        _ => panic!("unknown var `{}`", name),
    }
}

/// Everything the trace shows except the reply. Used to detect changes during `wait`.
fn snapshot(sm: &Sm) -> String {
    let v = &sm.vars;
    format!(
        "{:<14} phase={:<7} power={:<7} steam={:<7} stop={:<11} src={:<7} pump={} relay={} steam_on={}",
        manetta_sm::state_id_to_string(sm.state_id),
        format!("{:?}", v.phase),
        format!("{:?}", v.power),
        format!("{:?}", v.steam_state),
        format!("{:?}", v.stop_reason),
        format!("{:?}", v.start_source),
        sm.host.pump_on as u8,
        sm.host.power_relay_on as u8,
        sm.host.steam_on as u8,
    )
}

fn print_line(sm: &Sm, step: &str, reply: &str) {
    let t = sm.vars.now_ms;
    let time = format!("{}.{}s", t / 1000, (t % 1000) / 100);
    println!("t={:>7} | {:<26} | reply={:<9} | {}", time, step, reply, snapshot(sm));
}

fn main() {
    // `--sleep`: behave like a low-power host. Instead of a `do` tick every 100 ms, sleep until
    // `next_deadline_ms()` and dispatch `do` only then. The trace must be identical to polling.
    let sleep_mode = std::env::args().any(|a| a == "--sleep");
    let path = std::env::args().skip(1).find(|a| a != "--sleep").unwrap_or_else(|| "../scenarios.txt".to_string());
    let mut do_dispatches: u64 = 0;
    let text = std::fs::read_to_string(&path).expect("can't read scenarios file");

    let mut sm = Sm::new(TraceHost::default());
    // the data the app sends with START_BREW / ENTER_STEAM (scenario: `set cmd_max_ms N`)
    let mut cmd_max_ms: u32 = 0;

    for raw in text.lines() {
        let line = raw.split('#').next().unwrap().trim();
        if line.is_empty() {
            continue;
        }
        let (cmd, rest) = line.split_once(' ').unwrap_or((line, ""));

        match cmd {
            "scenario" => {
                println!();
                println!("== {}", rest);
                sm = Sm::new(TraceHost::default());
                cmd_max_ms = 0;
                sm.start();
                print_line(&sm, "start", "-");
            }
            "set" => {
                let (name, value) = rest.split_once(' ').expect("set <var> <value>");
                let value = value.parse().expect("number");
                if name == "cmd_max_ms" {
                    cmd_max_ms = value;
                } else {
                    set_var(&mut sm, name, value);
                }
            }
            "cmd" => {
                sm.vars.reply = Reply::NONE;
                sm.vars.last_write_at = sm.vars.now_ms;
                // commands with data use the generated typed dispatchers
                match rest {
                    "START_BREW" => sm.dispatch_start_brew(cmd_max_ms),
                    "ENTER_STEAM" => sm.dispatch_enter_steam(cmd_max_ms),
                    _ => sm.dispatch_event(parse_event(rest)),
                }
                let reply = format!("{:?}", sm.vars.reply);
                print_line(&sm, &format!("cmd {}", rest), &reply);
            }
            "ev" => {
                sm.dispatch_event(parse_event(rest));
                print_line(&sm, &format!("ev {}", rest), "-");
            }
            "wait" => {
                let ms: u32 = rest.parse().expect("number");
                let mut last = snapshot(&sm);
                let mut waited = 0;
                while waited < ms {
                    let step = if sleep_mode { sm.next_deadline_ms().min(ms - waited) } else { TICK_MS };
                    waited += step;
                    sm.vars.now_ms = sm.vars.now_ms.wrapping_add(step);
                    sm.dispatch_event(EventId::DO);
                    do_dispatches += 1;
                    let now = snapshot(&sm);
                    if now != last {
                        print_line(&sm, "do", "-");
                        last = now;
                    }
                }
                print_line(&sm, &format!("waited {} ms", ms), "-");
            }
            _ => panic!("bad line `{}`", line),
        }
    }

    eprintln!("do dispatches: {} ({})", do_dispatches, if sleep_mode { "sleep until next_deadline_ms()" } else { "poll every 100 ms" });
}
