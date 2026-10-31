//! Hand written. Output enums used by the generated `manetta_sm.rs`. Mirrors `ts/manetta_types.ts`.
//! Only uses `core`, so the no_std crate shares this file.
//! SCREAMING_CASE variants on purpose: the same names are used in the diagram for both Rust and TypeScript.
#![allow(non_camel_case_types, clippy::upper_case_acronyms)]

#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Reply { NONE, OK, BUSY, NOT_READY, FAULT, INVALID }

#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Phase { IDLE, BREWING, FAULT }

#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Power { OFF, HEATING, READY, COOLING, ON }

#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum SteamState { OFF, HEATING, READY }

#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum StopReason { NONE, APP, MACHINE, COMMAND_CAP, DEVICE_CAP, LINK_LOST, HEARTBEAT, FAULT }

#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum StartSource { NONE, APP, MACHINE }
