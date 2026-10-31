mod light;
mod light_sm;

use light_sm::{EventId, LightSm};

// `LightSm::new()` is a `const fn`, so a machine can live in a `static` without lazy init.
static INITIAL: LightSm = LightSm::new();

fn print(sm: &LightSm, what: &str) {
    println!(
        "{:<10} -> state: {:<8} light: {:<6} count: {} group_exits: {}",
        what,
        LightSm::state_id_to_string(sm.state_id),
        format!("{:?}", sm.vars.light.color),
        sm.vars.count,
        sm.vars.group_exits
    );
}

fn main() {
    let mut sm = LightSm::new();
    assert!(sm.state_id == INITIAL.state_id);

    sm.start();
    print(&sm, "start");

    let events = [
        EventId::INC, // OFF -> history -> default ON1 (blue)
        EventId::INC, // ON1 -> ON2 (yellow, count = 0)
        EventId::INC, // count 1
        EventId::INC, // count 2
        EventId::INC, // count 3 -> guard [count >= 3] -> OFF (exit ON_GROUP)
        EventId::INC, // OFF -> history remembers ON2
        EventId::DIM, // ON2 -> ON1
        EventId::OFF, // ON_GROUP handles OFF -> OFF (exit ON_GROUP)
        EventId::INC, // history remembers ON1
        EventId::DIM, // ON1 -> OFF
    ];

    for ev in events {
        sm.dispatch_event(ev);
        print(&sm, LightSm::event_id_to_string(ev));
    }
}
