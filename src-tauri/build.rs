fn main() {
    println!("cargo:rerun-if-changed=../dist");
    println!("cargo:rerun-if-changed=../rules/set.ini");
    println!("cargo:rerun-if-changed=../rules/title-presets.txt");
    tauri_build::build()
}
