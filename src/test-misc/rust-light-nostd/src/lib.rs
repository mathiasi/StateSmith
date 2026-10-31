#![no_std]

#[path = "../../rust-light/src/light.rs"]
mod light;
#[path = "../../rust-light/src/light_sm.rs"]
mod light_sm;

// The much larger Spec2 test machine (174 states, history, choice points, do events) also builds no_std.
// Its diagram code calls `trace()`/`trace_guard()`, which are no-ops here.
#[path = "../../rust-spec2/src/spec2_sm.rs"]
mod spec2_sm;
mod printer {
    pub fn trace(_msg: &str) {}
    pub fn trace_guard(_msg: &str, guard: bool) -> bool {
        guard
    }
}

use light_sm::{EventId, LightSm, StateId};

/// `const fn new()` lets the machine be built at compile time.
static INITIAL: LightSm = LightSm::new();

/// Exported so the state machine code is actually code generated and linked into the static library.
///
/// # Safety
/// `events` must point to `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn light_sm_demo(events: *const u8, len: usize) -> u8 {
    let mut sm = LightSm::new();
    sm.start();

    let events = unsafe { core::slice::from_raw_parts(events, len) };
    for &e in events {
        let ev = match e {
            0 => EventId::DIM,
            1 => EventId::INC,
            _ => EventId::OFF,
        };
        sm.dispatch_event(ev);
    }

    if sm.state_id == INITIAL.state_id {
        return 0xFF;
    }
    sm.state_id as u8
}

#[no_mangle]
pub extern "C" fn light_sm_state_name_len(id: u8) -> usize {
    let id = match id {
        0 => StateId::ROOT,
        1 => StateId::OFF,
        2 => StateId::ON_GROUP,
        3 => StateId::ON1,
        _ => StateId::ON2,
    };
    LightSm::state_id_to_string(id).len()
}

/// # Safety
/// `events` must point to `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn spec2_sm_demo(events: *const u8, len: usize) -> u8 {
    let mut sm = spec2_sm::Spec2Sm::new();
    sm.start();
    let events = unsafe { core::slice::from_raw_parts(events, len) };
    for &e in events {
        if (e as usize) < spec2_sm::EVENT_ID_COUNT {
            // SAFETY: EventId is `#[repr(u8)]` with contiguous discriminants 0..EVENT_ID_COUNT.
            sm.dispatch_event(unsafe { core::mem::transmute::<u8, spec2_sm::EventId>(e) });
        }
    }
    sm.state_id as u8
}

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    loop {}
}
