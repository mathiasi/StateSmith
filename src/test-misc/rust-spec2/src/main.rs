mod printer;
mod spec2_sm;

use spec2_sm::{EventId, Spec2Sm, EVENT_ID_COUNT};

fn parse_event(arg: &str) -> EventId {
    for i in 0..EVENT_ID_COUNT {
        // SAFETY: EventId is `#[repr(u8)]` with contiguous discriminants 0..EVENT_ID_COUNT.
        let ev: EventId = unsafe { core::mem::transmute(i as u8) };
        if Spec2Sm::event_id_to_string(ev).eq_ignore_ascii_case(arg) {
            return ev;
        }
    }
    panic!("bad event arg: `{}`", arg);
}

fn main() {
    let mut sm = Spec2Sm::new();

    println!("Start Statemachine");
    printer::print_divider();
    sm.start();
    println!();

    for arg in std::env::args().skip(1) {
        let ev = parse_event(&arg);
        println!("Dispatch event {}", arg);
        printer::print_divider();
        sm.dispatch_event(ev);
        println!();
    }
}
