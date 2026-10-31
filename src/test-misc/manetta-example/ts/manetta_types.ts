// Hand written. Output enums used by the generated ManettaSm.ts. Mirrors rust/src/manetta_types.rs.
export enum Reply { NONE, OK, BUSY, NOT_READY, FAULT, INVALID }
export enum Phase { IDLE, BREWING, FAULT }
export enum Power { OFF, HEATING, READY, COOLING, ON }
export enum SteamState { OFF, HEATING, READY }
export enum StopReason { NONE, APP, MACHINE, COMMAND_CAP, DEVICE_CAP, LINK_LOST, HEARTBEAT, FAULT }
export enum StartSource { NONE, APP, MACHINE }
