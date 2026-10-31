//! Scenario runner (host side, uses std). Plays `../scenarios.txt` and prints one trace line per step.
//! `ts/runner.ts` does exactly the same; the two outputs must be identical.

mod manetta_sm;
mod manetta_types;

use manetta_sm::{EventId, ManettaSm};

const TICK_MS: u32 = 100;

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

fn set_var(sm: &mut ManettaSm, name: &str, value: u32) {
    let v = &mut sm.vars;
    match name {
        "cmd_max_ms" => v.cmd_max_ms = value,
        "device_max_brew_ms" => v.device_max_brew_ms = value,
        "device_max_steam_ms" => v.device_max_steam_ms = value,
        "heartbeat_timeout_ms" => v.heartbeat_timeout_ms = value,
        "auto_off_min" => v.auto_off_min = value,
        _ => panic!("unknown var `{}`", name),
    }
}

/// Everything the trace shows except the reply. Used to detect changes during `wait`.
fn snapshot(sm: &ManettaSm) -> String {
    let v = &sm.vars;
    format!(
        "{:<14} phase={:<7} power={:<7} steam={:<7} stop={:<11} src={:<7} pump={} relay={} steam_on={}",
        ManettaSm::state_id_to_string(sm.state_id),
        format!("{:?}", v.phase),
        format!("{:?}", v.power),
        format!("{:?}", v.steam_state),
        format!("{:?}", v.stop_reason),
        format!("{:?}", v.start_source),
        v.pump_on as u8,
        v.power_relay_on as u8,
        v.steam_on as u8,
    )
}

fn print_line(sm: &ManettaSm, step: &str, reply: &str) {
    let t = sm.vars.now_ms;
    let time = format!("{}.{}s", t / 1000, (t % 1000) / 100);
    println!("t={:>7} | {:<26} | reply={:<9} | {}", time, step, reply, snapshot(sm));
}

fn main() {
    let path = std::env::args().nth(1).unwrap_or_else(|| "../scenarios.txt".to_string());
    let text = std::fs::read_to_string(&path).expect("can't read scenarios file");

    let mut sm = ManettaSm::new();

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
                sm = ManettaSm::new();
                sm.start();
                print_line(&sm, "start", "-");
            }
            "set" => {
                let (name, value) = rest.split_once(' ').expect("set <var> <value>");
                set_var(&mut sm, name, value.parse().expect("number"));
            }
            "cmd" => {
                sm.vars.reply = manetta_types::Reply::NONE;
                sm.vars.last_write_at = sm.vars.now_ms;
                sm.dispatch_event(parse_event(rest));
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
                    waited += TICK_MS;
                    sm.vars.now_ms = sm.vars.now_ms.wrapping_add(TICK_MS);
                    sm.dispatch_event(EventId::DO);
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
}
