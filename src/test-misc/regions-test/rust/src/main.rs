//! Regions semantics test runner. Args: event names. Prints every trace and the active states after each event.
mod regions_sm;

use regions_sm::{EventId, RegionsSm, RegionsSmHost};

struct PrintHost;
impl RegionsSmHost for PrintHost {
    fn trace(&mut self, msg: &str) {
        println!("  {}", msg);
    }
}

fn parse(name: &str) -> EventId {
    match name {
        "GO" => EventId::GO,
        "DEEP" => EventId::DEEP,
        "EV1" => EventId::EV1,
        "EV2" => EventId::EV2,
        "EV3" => EventId::EV3,
        "EV4" => EventId::EV4,
        "EV5" => EventId::EV5,
        "LEAVE" => EventId::LEAVE,
        "RESET" => EventId::RESET,
        "DO" => EventId::DO,
        "EV6" => EventId::EV6,
        "EV7" => EventId::EV7,
        "EV8" => EventId::EV8,
        "LEAVE2" => EventId::LEAVE2,
        _ => panic!("unknown event {}", name),
    }
}

fn print_states(sm: &RegionsSm<PrintHost>) {
    println!(
        "  = {} | {} | {} | {} {} | ticks={}",
        regions_sm::state_id_to_string(sm.state_id),
        regions_sm::state_id_to_string(sm.par_region_1_state_id),
        regions_sm::state_id_to_string(sm.par_region_2_state_id),
        regions_sm::state_id_to_string(sm.a3_region_1_state_id),
        regions_sm::state_id_to_string(sm.a3_region_2_state_id),
        sm.vars.ticks
    );
}

fn main() {
    let mut sm = RegionsSm::new(PrintHost);
    println!("start");
    sm.start();
    print_states(&sm);
    for arg in std::env::args().skip(1) {
        println!("{}", arg);
        sm.dispatch_event(parse(&arg));
        print_states(&sm);
    }
}
