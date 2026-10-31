#![no_std]

#[path = "../../rust/src/manetta_dual_sm.rs"]
mod manetta_dual_sm;

use manetta_dual_sm::{EventId, ManettaDualSm, ManettaDualSmHost, EVENT_ID_COUNT};

/// The board's host: what the diagram's `set_pump(true)` etc. do on the hardware.
/// Stand-in for a GPIO output register; a real board would write the register (or an embedded-hal pin) here.
pub struct BoardHost {
    pub gpio_out: u32,
}

const PUMP: u32 = 1 << 0;
const POWER_RELAY: u32 = 1 << 1;
const STEAM_HEATER: u32 = 1 << 2;

impl BoardHost {
    pub const fn new() -> Self {
        Self { gpio_out: 0 }
    }

    fn set(&mut self, bit: u32, on: bool) {
        if on {
            self.gpio_out |= bit;
        } else {
            self.gpio_out &= !bit;
        }
    }
}

impl Default for BoardHost {
    fn default() -> Self {
        Self::new()
    }
}

impl ManettaDualSmHost for BoardHost {
    fn set_pump(&mut self, on: bool) {
        self.set(PUMP, on)
    }
    fn set_power_relay(&mut self, on: bool) {
        self.set(POWER_RELAY, on)
    }
    fn set_steam_heater(&mut self, on: bool) {
        self.set(STEAM_HEATER, on)
    }
}

/// `const fn new(host)`: the machine, host included, can be built at compile time
/// (e.g. inside a `static` behind a critical-section Mutex).
static INITIAL: ManettaDualSm<BoardHost> = ManettaDualSm::new(BoardHost::new());

/// Board-side shape of the integration: the firmware owns one machine (which owns the host), writes inputs into `vars`,
/// dispatches an event, then reads the outputs (reply and status fields in `vars`, hardware through the host).
///
/// Exported only so the state machine is actually compiled and linked into the static library.
///
/// # Safety
/// `events` must point to `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn manetta_dual_demo(events: *const u8, len: usize, tick_ms: u32) -> u32 {
    let mut sm = ManettaDualSm::new(BoardHost::new());
    // raw `dispatch_event(START_BREW)` uses the last stored event data; typed dispatchers set it
    sm.vars.max_ms = 25_000;
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

    // pack a few outputs (both regions included) so none of them is optimised away
    (sm.state_id as u32)
        | (sm.on_region_1_state_id as u32) << 4
        | (sm.on_region_2_state_id as u32) << 8
        | (sm.vars.reply as u32) << 12
        | (sm.vars.stop_reason as u32) << 16
        | sm.host.gpio_out << 24
}

/// What a low-power board would call after every dispatch: how long it may sleep before the next `do`.
/// Exported only so it is compiled and linked (measures the cost of `next_deadline_ms()`).
#[no_mangle]
pub extern "C" fn manetta_dual_next_deadline_ms(now_ms: u32, cmd_max_ms: u32) -> u32 {
    let mut sm = ManettaDualSm::new(BoardHost::new());
    sm.start();
    sm.dispatch_event(EventId::TURN_ON);
    sm.dispatch_event(EventId::TEMP_READY);
    sm.dispatch_enter_steam(cmd_max_ms); // steam boiler region
    sm.dispatch_start_brew(cmd_max_ms); // brew region (generated typed dispatcher for `START_BREW(uint max_ms)`)
    sm.vars.now_ms = now_ms;
    sm.next_deadline_ms()
}

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    loop {}
}
