//! Hand written user code for the LightSm example. Only uses `core`, so the no_std crate shares it.

use crate::light_sm::LightSm;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Color {
    Off,
    Blue,
    Yellow,
}

/// Stand-in for a hardware driver. It is owned by the state machine (`LightSm.vars.light`).
pub struct Light {
    pub color: Color,
    pub changes: u16,
}

impl Light {
    pub const fn new() -> Self {
        Self { color: Color::Off, changes: 0 }
    }

    fn set(&mut self, color: Color) {
        self.color = color;
        self.changes = self.changes.wrapping_add(1);
    }

    pub fn off(&mut self) {
        self.set(Color::Off)
    }

    pub fn blue(&mut self) {
        self.set(Color::Blue)
    }

    pub fn yellow(&mut self) {
        self.set(Color::Yellow)
    }
}

/// A user `impl` block on the generated type. Rust allows this from any module of the same crate,
/// so diagram code can call `self.record_group_exit()` without the generator knowing about it.
impl LightSm {
    pub(crate) fn record_group_exit(&mut self) {
        self.vars.group_exits = self.vars.group_exits.wrapping_add(1);
    }
}
