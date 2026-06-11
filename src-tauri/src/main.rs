#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod commands;
mod license;
mod models;
mod sidecar;

fn main() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            commands::parse_document,
            commands::precheck_choose_files,
            commands::get_license_status,
            commands::list_plans,
            commands::purchase_plan,
            commands::activate_license,
            commands::dev_activate_yearly,
            commands::get_app_debug_flags,
            commands::set_auth_mode,
            commands::get_default_rules_path_command,
            commands::get_format_preset,
            commands::save_format_preset,
            commands::get_rules_config,
            commands::save_rules_config,
            commands::export_rules_copy,
            commands::open_result_folder,
            commands::open_path,
            commands::open_result_for_file,
            commands::open_result_summary,
            commands::get_result_overview,
            commands::get_text_file_content,
            commands::list_title_rule_presets,
            commands::list_result_artifacts
        ])
        .run(tauri::generate_context!("./tauri.conf.json"))
        .expect("failed to run tauri app");
}
