pub fn trace(msg: &str) {
    println!("{}", msg);
}

pub fn trace_guard(msg: &str, guard: bool) -> bool {
    print!("{} ", msg);
    if guard {
        println!("Behavior running.");
    } else {
        println!("Behavior skipped.");
    }
    guard
}

pub fn print_divider() {
    println!("===================================================");
}
