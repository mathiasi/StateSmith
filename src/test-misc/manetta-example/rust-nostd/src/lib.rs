#![no_std]

#[path = "../../rust/src/manetta_sm.rs"]
mod manetta_sm;

use manetta_sm::{EventId, ManettaSm, EVENT_ID_COUNT};

/// `const fn new()`: the machine can be built at compile time (e.g. inside a `static` behind a critical-section Mutex).
static INITIAL: ManettaSm = ManettaSm::new();

/// Board-side shape of the integration: the firmware owns one machine, writes inputs into `vars`,
/// dispatches an event, then reads the outputs (reply, relays, status fields).
///
/// Exported only so the state machine is actually compiled and linked into the static library.
///
/// # Safety
/// `events` must point to `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn manetta_demo(events: *const u8, len: usize, tick_ms: u32) -> u32 {
    let mut sm = ManettaSm::new();
    sm.vars.cmd_max_ms = 25_000;
    sm.start();

    let events = unsafe { core::slice::from_raw_parts(events, len) };
    for &e in events {
        sm.vars.now_ms = sm.vars.now_ms.wrapping_add(tick_ms);
        sm.vars.last_write_at = sm.vars.now_ms;
        if (e as usize) < EVENT_ID_COUNT {
            // SAFETY: EventId is `#[repr(u8)]` with contiguous discriminants 0..EVENT_ID_COUNT.
            sm.dispatch_event(unsafe { core::mem::transmute::<u8, EventId>(e) });
        }
    }

    if sm.state_id == INITIAL.state_id {
        return u32::MAX;
    }

    // pack a few outputs so none of them is optimised away
    (sm.state_id as u32)
        | (sm.vars.reply as u32) << 8
        | (sm.vars.stop_reason as u32) << 16
        | (sm.vars.pump_on as u32) << 24
        | (sm.vars.power_relay_on as u32) << 25
        | (sm.vars.steam_on as u32) << 26
}

/// What a low-power board would call after every dispatch: how long it may sleep before the next `do`.
/// Exported only so it is compiled and linked (measures the cost of `next_deadline_ms()`).
#[no_mangle]
pub extern "C" fn manetta_next_deadline_ms(now_ms: u32, cmd_max_ms: u32) -> u32 {
    let mut sm = ManettaSm::new();
    sm.start();
    sm.dispatch_event(EventId::TURN_ON);
    sm.dispatch_event(EventId::TEMP_READY);
    sm.vars.cmd_max_ms = cmd_max_ms;
    sm.dispatch_event(EventId::START_BREW);
    sm.vars.now_ms = now_ms;
    sm.next_deadline_ms()
}

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    loop {}
}
