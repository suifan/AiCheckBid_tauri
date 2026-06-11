use crate::license::{self, LicenseStatus, PlanInfo};
use crate::models::{ParseDocumentRequest, ParseDocumentResponse, RuleIssue};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::process::Command;
use tauri::Manager;

#[tauri::command]
pub fn parse_document(window: tauri::Window, request: ParseDocumentRequest) -> Result<ParseDocumentResponse, String> {
    emit_progress(
        &window,
        "preparing",
        format!("开始检查文档：{}", file_name_of(&request.file_path)),
    );
    emit_progress(&window, "license_check", "正在校验授权信息".to_string());
    license::validate_before_check().map_err(|e| e.to_string())?;
    let page_limit = license::page_limit_for_check().map_err(|e| e.to_string())?;
    let mut req = request;
    if req.rules_path.as_deref().unwrap_or("").trim().is_empty() {
        req.rules_path = Some(get_default_rules_path()?);
    }
    emit_progress(&window, "rules_loading", "正在读取规则配置".to_string());
    emit_progress(
        &window,
        "loading_document",
        format!("正在加载文档：{}", file_name_of(&req.file_path)),
    );
    emit_progress(
        &window,
        "analyzing",
        analyze_message_for(&req.file_path),
    );
    let mut resp = crate::sidecar::parse_document_with_progress(req, |message| {
        emit_progress(&window, "running", message.to_string());
    })
    .map_err(|e| {
        emit_progress(&window, "error", format!("检查异常：{e}"));
        e.to_string()
    })?;

    if let Some(page_count) = resp.page_count {
        if page_count as i32 >= page_limit {
            let msg = format!("所购版本不能对其进行检查：当前页数{page_count}，上限{page_limit}");
            resp.warnings.push(msg.clone());
            resp.issues = vec![RuleIssue {
                category: "其他".to_string(),
                rule: "页数限制".to_string(),
                message: msg,
                location: "P1".to_string(),
                current_value: page_count.to_string(),
                expected_value: format!("<{page_limit}"),
                severity: "warning".to_string(),
                fixed: false,
                snippet: None,
            }];
        }
    }

    emit_progress(&window, "reporting", "正在生成检查报告".to_string());
    let _ = license::record_usage_once();
    emit_progress(
        &window,
        "finished",
        format!("完成检查：{}", file_name_of(&resp.file_path)),
    );
    Ok(resp)
}

#[tauri::command]
pub fn precheck_choose_files(reg_code: Option<String>) -> Result<(), String> {
    license::validate_before_choose_files(reg_code.as_deref()).map_err(|e| e.to_string())
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FormatPreset {
    pub rules_path: String,
    pub values: HashMap<String, HashMap<String, String>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RulesConfig {
    pub rules_path: String,
    pub values: HashMap<String, HashMap<String, String>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResultArtifact {
    pub id: String,
    pub display_name: String,
    pub txt_path: Option<String>,
    pub docx_path: Option<String>,
    pub report_text: Option<String>,
    pub updated_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResultOverviewSectionLink {
    pub name: String,
    pub txt_path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResultOverviewItem {
    pub id: String,
    pub source_name: String,
    pub display_name: String,
    pub section_links: Vec<ResultOverviewSectionLink>,
    pub report_docx_path: Option<String>,
    pub source_copy_path: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResultOverview {
    pub exists: bool,
    pub path: Option<String>,
    pub raw_text: Option<String>,
    pub items: Vec<ResultOverviewItem>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TextFileContent {
    pub exists: bool,
    pub path: String,
    pub text: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DevActivationResult {
    pub reg_code: String,
    pub status: LicenseStatus,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppDebugFlags {
    pub dev_build: bool,
}

fn candidate_rule_paths() -> Vec<PathBuf> {
    let mut list = Vec::new();
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    if let Some(app_root) = manifest_dir.parent() {
        list.push(app_root.join("rules").join("set.ini"));
    }

    if let Ok(exe) = std::env::current_exe() {
        if let Some(exe_dir) = exe.parent() {
            list.push(exe_dir.join("rules").join("set.ini"));
            list.push(exe_dir.join("resources").join("rules").join("set.ini"));
        }
    }
    list
}

fn development_app_root() -> PathBuf {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    manifest_dir
        .parent()
        .map(|p| p.to_path_buf())
        .unwrap_or(manifest_dir)
}

fn runtime_app_root() -> PathBuf {
    let dev_root = development_app_root();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(exe_dir) = exe.parent() {
            let dir_name = exe_dir
                .file_name()
                .and_then(|x| x.to_str())
                .unwrap_or("")
                .to_ascii_lowercase();
            let parent_name = exe_dir
                .parent()
                .and_then(|x| x.file_name())
                .and_then(|x| x.to_str())
                .unwrap_or("")
                .to_ascii_lowercase();
            if parent_name == "target" && (dir_name == "debug" || dir_name == "release") {
                return dev_root;
            }
            return exe_dir.to_path_buf();
        }
    }
    dev_root
}

fn get_default_rules_path() -> Result<String, String> {
    for p in candidate_rule_paths() {
        if p.exists() {
            return Ok(p.to_string_lossy().to_string());
        }
    }
    let fallback = candidate_rule_paths()
        .into_iter()
        .next()
        .ok_or_else(|| "无法解析规则路径".to_string())?;
    Ok(fallback.to_string_lossy().to_string())
}

fn parse_ini(text: &str) -> HashMap<String, HashMap<String, String>> {
    let mut map: HashMap<String, HashMap<String, String>> = HashMap::new();
    let mut section = String::new();
    for raw in text.lines() {
        let line = raw.trim();
        if line.is_empty() || line.starts_with(';') || line.starts_with('#') {
            continue;
        }
        if line.starts_with('[') && line.ends_with(']') {
            section = line[1..line.len() - 1].to_string();
            map.entry(section.clone()).or_default();
            continue;
        }
        if let Some(idx) = line.find('=') {
            let key = line[..idx].trim().to_string();
            let value = line[idx + 1..].trim().to_string();
            map.entry(section.clone()).or_default().insert(key, value);
        }
    }
    map
}

fn upsert_ini_value(text: &str, section: &str, key: &str, value: &str) -> String {
    let mut lines: Vec<String> = text.lines().map(|s| s.to_string()).collect();
    let mut sec_start: Option<usize> = None;
    let mut sec_end = lines.len();

    for (i, line) in lines.iter().enumerate() {
        let t = line.trim();
        if t.starts_with('[') && t.ends_with(']') {
            let current = &t[1..t.len() - 1];
            if sec_start.is_none() && current == section {
                sec_start = Some(i);
            } else if sec_start.is_some() {
                sec_end = i;
                break;
            }
        }
    }

    if let Some(start) = sec_start {
        for i in (start + 1)..sec_end {
            let t = lines[i].trim();
            if let Some(eq) = t.find('=') {
                if t[..eq].trim() == key {
                    lines[i] = format!("{key} = {value}");
                    return lines.join("\n");
                }
            }
        }
        lines.insert(sec_end, format!("{key} = {value}"));
        return lines.join("\n");
    }

    if !lines.is_empty() {
        lines.push(String::new());
    }
    lines.push(format!("[{section}]"));
    lines.push(format!("{key} = {value}"));
    lines.join("\n")
}

#[tauri::command]
pub fn get_default_rules_path_command() -> Result<String, String> {
    get_default_rules_path()
}

#[tauri::command]
pub fn get_format_preset(rules_path: Option<String>) -> Result<FormatPreset, String> {
    let path = rules_path
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(get_default_rules_path()?);
    let content = fs::read_to_string(&path).map_err(|e| format!("读取规则失败: {e}"))?;
    Ok(FormatPreset {
        rules_path: path,
        values: parse_ini(&content),
    })
}

#[tauri::command]
pub fn save_format_preset(preset: FormatPreset) -> Result<String, String> {
    let path = if preset.rules_path.trim().is_empty() {
        get_default_rules_path()?
    } else {
        preset.rules_path.trim().to_string()
    };
    let mut content = fs::read_to_string(&path).map_err(|e| format!("读取规则失败: {e}"))?;
    for (section, kv) in preset.values {
        for (key, val) in kv {
            content = upsert_ini_value(&content, &section, &key, &val);
        }
    }
    fs::write(&path, content).map_err(|e| format!("保存规则失败: {e}"))?;
    Ok(path)
}

#[tauri::command]
pub fn get_rules_config(rules_path: Option<String>) -> Result<RulesConfig, String> {
    let path = rules_path
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(get_default_rules_path()?);
    let content = fs::read_to_string(&path).map_err(|e| format!("读取规则失败: {e}"))?;
    Ok(RulesConfig {
        rules_path: path,
        values: parse_ini(&content),
    })
}

#[tauri::command]
pub fn save_rules_config(config: RulesConfig) -> Result<String, String> {
    let path = if config.rules_path.trim().is_empty() {
        get_default_rules_path()?
    } else {
        config.rules_path.trim().to_string()
    };
    let mut content = fs::read_to_string(&path).map_err(|e| format!("读取规则失败: {e}"))?;
    for (section, kv) in config.values {
        for (key, val) in kv {
            content = upsert_ini_value(&content, &section, &key, &val);
        }
    }
    fs::write(&path, content).map_err(|e| format!("保存规则失败: {e}"))?;
    Ok(path)
}

#[tauri::command]
pub fn export_rules_copy(source_path: Option<String>, target_path: String) -> Result<String, String> {
    let src = source_path
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(get_default_rules_path()?);
    let target = target_path.trim();
    if target.is_empty() {
        return Err("导出路径为空".to_string());
    }
    let content = fs::read_to_string(&src).map_err(|e| format!("读取规则失败: {e}"))?;
    fs::write(target, content).map_err(|e| format!("导出规则失败: {e}"))?;
    Ok(target.to_string())
}

#[tauri::command]
pub fn list_title_rule_presets() -> Result<Vec<String>, String> {
    let mut candidates: Vec<PathBuf> = Vec::new();
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    if let Some(app_root) = manifest_dir.parent() {
        candidates.push(app_root.join("rules").join("title-presets.txt"));
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(exe_dir) = exe.parent() {
            candidates.push(exe_dir.join("rules").join("title-presets.txt"));
            candidates.push(exe_dir.join("resources").join("rules").join("title-presets.txt"));
        }
    }

    for path in candidates {
        if path.exists() {
            let content = fs::read_to_string(&path).map_err(|e| format!("读取标题预设失败: {e}"))?;
            let items = parse_title_preset_lines(&content);
            if !items.is_empty() {
                return Ok(items);
            }
        }
    }

    Ok(vec![
        "一、".to_string(),
        "（一）".to_string(),
        "(一)".to_string(),
        "1.".to_string(),
        "1、".to_string(),
        "1.1".to_string(),
        "1.1.1".to_string(),
        "（1）".to_string(),
        "(1)".to_string(),
        "1）".to_string(),
        "①".to_string(),
        "第X章".to_string(),
        "第X节".to_string(),
        "附件X".to_string(),
    ])
}

#[tauri::command]
pub fn get_license_status() -> Result<LicenseStatus, String> {
    license::get_license_status().map_err(|e| e.to_string())
}

#[tauri::command]
pub fn list_plans() -> Result<Vec<PlanInfo>, String> {
    Ok(license::list_plans())
}

#[tauri::command]
pub fn purchase_plan(plan_id: String) -> Result<String, String> {
    license::purchase_plan(&plan_id).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn activate_license(reg_code: String) -> Result<LicenseStatus, String> {
    license::activate_license(&reg_code).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn set_auth_mode(mode: String, udisk_drive: Option<String>) -> Result<LicenseStatus, String> {
    license::set_auth_mode(&mode, udisk_drive.as_deref()).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn dev_activate_yearly() -> Result<DevActivationResult, String> {
    if !cfg!(debug_assertions) {
        return Err("该功能仅开发态可用".to_string());
    }
    let reg_code = license::purchase_plan("yearly").map_err(|e| e.to_string())?;
    let status = license::activate_license(&reg_code).map_err(|e| e.to_string())?;
    Ok(DevActivationResult { reg_code, status })
}

#[tauri::command]
pub fn get_app_debug_flags() -> AppDebugFlags {
    AppDebugFlags {
        dev_build: cfg!(debug_assertions),
    }
}

#[tauri::command]
pub fn open_result_folder() -> Result<(), String> {
    let result_dir = result_dir_path();
    std::fs::create_dir_all(&result_dir).map_err(|e| e.to_string())?;
    Command::new("explorer.exe")
        .arg(result_dir.as_os_str())
        .spawn()
        .map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
pub fn open_path(path: String) -> Result<(), String> {
    if path.trim().is_empty() {
        return Err("路径为空".to_string());
    }
    let p = PathBuf::from(path.trim());
    if !p.exists() {
        return Err("目标不存在".to_string());
    }
    Command::new("explorer.exe")
        .arg(p.as_os_str())
        .spawn()
        .map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
pub fn open_result_summary() -> Result<(), String> {
    let result_dir = result_dir_path();
    std::fs::create_dir_all(&result_dir).map_err(|e| e.to_string())?;
    let overview = result_dir.join("检查结果概要.txt");
    if overview.exists() {
        Command::new("explorer.exe")
            .arg(overview.as_os_str())
            .spawn()
            .map_err(|e| e.to_string())?;
    }
    Command::new("explorer.exe")
        .arg(result_dir.as_os_str())
        .spawn()
        .map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
pub fn get_result_overview() -> Result<ResultOverview, String> {
    let result_dir = result_dir_path();
    fs::create_dir_all(&result_dir).map_err(|e| e.to_string())?;

    let overview_path = result_dir.join("检查结果概要.txt");
    if !overview_path.exists() {
        return Ok(ResultOverview {
            exists: false,
            path: Some(overview_path.to_string_lossy().to_string()),
            raw_text: None,
            items: Vec::new(),
        });
    }

    let raw_text = read_text_file_lossy(&overview_path)?;
    let items = parse_result_overview_items(&result_dir, &raw_text);
    Ok(ResultOverview {
        exists: true,
        path: Some(overview_path.to_string_lossy().to_string()),
        raw_text: Some(raw_text),
        items,
    })
}

#[tauri::command]
pub fn get_text_file_content(path: String) -> Result<TextFileContent, String> {
    let path = path.trim();
    if path.is_empty() {
        return Err("路径为空".to_string());
    }

    let file_path = PathBuf::from(path);
    if !file_path.exists() {
        return Ok(TextFileContent {
            exists: false,
            path: file_path.to_string_lossy().to_string(),
            text: None,
        });
    }

    Ok(TextFileContent {
        exists: true,
        path: file_path.to_string_lossy().to_string(),
        text: Some(read_text_file_lossy(&file_path)?),
    })
}

#[tauri::command]
pub fn open_result_for_file(file_path: String) -> Result<(), String> {
    let source = PathBuf::from(file_path.trim());
    if !source.exists() {
        return Err("目标不存在".to_string());
    }

    let result_dir = result_dir_path();
    fs::create_dir_all(&result_dir).map_err(|e| e.to_string())?;

    let source_name = source
        .file_stem()
        .and_then(|x| x.to_str())
        .ok_or_else(|| "文件名无效".to_string())?;
    let report = result_dir.join(format!("检查结果-{}m.docx", source_name));
    if !report.exists() {
        return Err("还未生成结果".to_string());
    }

    Command::new("explorer.exe")
        .arg(report.as_os_str())
        .spawn()
        .map_err(|e| e.to_string())?;

    let original_copy = result_dir.join(format!(
        "{}m{}",
        source_name,
        source.extension().and_then(|x| x.to_str()).map(|ext| format!(".{ext}")).unwrap_or_default()
    ));
    if original_copy.exists() {
        let _ = Command::new("explorer.exe").arg(original_copy.as_os_str()).spawn();
    }
    Ok(())
}

#[tauri::command]
pub fn list_result_artifacts() -> Result<Vec<ResultArtifact>, String> {
    let result_dir = result_dir_path();
    fs::create_dir_all(&result_dir).map_err(|e| e.to_string())?;

    let mut groups: HashMap<String, ResultArtifact> = HashMap::new();
    let entries = fs::read_dir(&result_dir).map_err(|e| e.to_string())?;
    for entry in entries {
        let entry = entry.map_err(|e| e.to_string())?;
        let path = entry.path();
        if !path.is_file() {
            continue;
        }
        let ext = path
            .extension()
            .and_then(|x| x.to_str())
            .unwrap_or("")
            .to_ascii_lowercase();
        if ext != "txt" && ext != "docx" {
            continue;
        }

        let stem = path.file_stem().and_then(|x| x.to_str()).unwrap_or("").to_string();
        if stem.is_empty() {
            continue;
        }
        if is_result_auxiliary_stem(&stem) {
            continue;
        }
        let group_id = normalize_result_group_id(&stem);
        let modified = fs::metadata(&path)
            .and_then(|m| m.modified())
            .ok()
            .and_then(format_system_time);

        let item = groups.entry(group_id.clone()).or_insert_with(|| ResultArtifact {
            id: group_id.clone(),
            display_name: humanize_result_name(&group_id),
            txt_path: None,
            docx_path: None,
            report_text: None,
            updated_at: modified.clone().unwrap_or_else(|| "-".to_string()),
        });

        if let Some(time) = modified {
            item.updated_at = time;
        }

        let path_text = path.to_string_lossy().to_string();
        match ext.as_str() {
            "txt" => {
                item.txt_path = Some(path_text.clone());
                item.report_text = fs::read_to_string(&path).ok();
            }
            "docx" => item.docx_path = Some(path_text),
            _ => {}
        }
    }

    let mut list: Vec<ResultArtifact> = groups.into_values().collect();
    list.sort_by(|a, b| b.updated_at.cmp(&a.updated_at).then_with(|| a.display_name.cmp(&b.display_name)));
    Ok(list)
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct CheckProgressEvent {
    stage: String,
    message: String,
}

fn emit_progress(window: &tauri::Window, stage: &str, message: String) {
    let payload = CheckProgressEvent {
        stage: stage.to_string(),
        message,
    };
    let _ = window.app_handle().emit_all("check-progress", payload);
}

fn file_name_of(path: &str) -> String {
    PathBuf::from(path)
        .file_name()
        .and_then(|x| x.to_str())
        .unwrap_or(path)
        .to_string()
}

fn result_dir_path() -> PathBuf {
    runtime_app_root().join("result")
}

fn is_result_auxiliary_stem(stem: &str) -> bool {
    let legacy_suffixes = [
        "格式检查结果",
        "公司名检查结果",
        "地名检查结果",
        "人名检查结果",
        "敏感词检查结果",
        "标点符号检查结果",
        "其他检查结果",
    ];
    let is_legacy_numbered = legacy_suffixes.iter().any(|suffix| {
        if let Some(prefix) = stem.strip_suffix(suffix).and_then(|v| v.strip_suffix("的")) {
            !prefix.is_empty() && prefix.chars().all(|ch| ch.is_ascii_digit())
        } else {
            false
        }
    });
    stem == "检查结果概要"
        || is_legacy_numbered
        || legacy_suffixes
            .iter()
            .any(|suffix| stem.ends_with(&format!("-{suffix}")))
}

fn normalize_result_group_id(stem: &str) -> String {
    stem.trim()
        .trim_start_matches("检查结果-")
        .trim_end_matches("_检查报告")
        .trim_end_matches("-检查结果")
        .trim_end_matches("_检查结果")
        .trim_end_matches("-检查报告")
        .to_string()
}

fn humanize_result_name(group_id: &str) -> String {
    PathBuf::from(group_id)
        .file_name()
        .and_then(|x| x.to_str())
        .unwrap_or(group_id)
        .to_string()
}

fn read_text_file_lossy(path: &PathBuf) -> Result<String, String> {
    let bytes = fs::read(path).map_err(|e| e.to_string())?;
    Ok(String::from_utf8_lossy(&bytes).to_string())
}

fn parse_result_overview_items(result_dir: &PathBuf, raw_text: &str) -> Vec<ResultOverviewItem> {
    raw_text
        .lines()
        .enumerate()
        .filter_map(|(line_no, line)| parse_result_overview_line(result_dir, line_no, line))
        .collect()
}

fn parse_result_overview_line(result_dir: &PathBuf, line_no: usize, line: &str) -> Option<ResultOverviewItem> {
    let parts: Vec<String> = line
        .split('\t')
        .map(|x| x.trim().to_string())
        .collect();
    if parts.len() < 2 {
        return None;
    }

    let source_name = parts.get(1)?.trim().to_string();
    if source_name.is_empty() {
        return None;
    }

    let mut section_links = Vec::new();
    for section_name in parts.iter().skip(2) {
        if section_name.trim().is_empty() {
            continue;
        }
        let txt_path = result_dir.join(format!("{section_name}.txt"));
        if txt_path.exists() {
            section_links.push(ResultOverviewSectionLink {
                name: section_name.clone(),
                txt_path: txt_path.to_string_lossy().to_string(),
            });
        }
    }

    let report_docx = result_dir.join(format!("检查结果-{source_name}.docx"));
    Some(ResultOverviewItem {
        id: format!("{}-{source_name}", line_no + 1),
        source_name: source_name.clone(),
        display_name: humanize_result_name(&source_name),
        section_links,
        report_docx_path: if report_docx.exists() {
            Some(report_docx.to_string_lossy().to_string())
        } else {
            None
        },
        source_copy_path: find_result_source_copy(result_dir, &source_name),
    })
}

fn find_result_source_copy(result_dir: &PathBuf, source_name: &str) -> Option<String> {
    let entries = fs::read_dir(result_dir).ok()?;
    for entry in entries {
        let path = entry.ok()?.path();
        if !path.is_file() {
            continue;
        }
        let stem = path.file_stem().and_then(|x| x.to_str()).unwrap_or("");
        if stem != source_name {
            continue;
        }
        let ext = path
            .extension()
            .and_then(|x| x.to_str())
            .unwrap_or("")
            .to_ascii_lowercase();
        if ext == "doc" || ext == "docx" || ext == "pdf" {
            return Some(path.to_string_lossy().to_string());
        }
    }
    None
}

fn format_system_time(time: std::time::SystemTime) -> Option<String> {
    let secs = time.duration_since(std::time::UNIX_EPOCH).ok()?.as_secs() as i64;
    let days = secs / 86_400;
    let sec_of_day = secs % 86_400;
    let z = days + 719468;
    let era = if z >= 0 { z } else { z - 146096 } / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = mp + if mp < 10 { 3 } else { -9 };
    let year = y + if m <= 2 { 1 } else { 0 };
    let hour = sec_of_day / 3600;
    let minute = (sec_of_day % 3600) / 60;
    Some(format!("{year:04}-{m:02}-{d:02} {hour:02}:{minute:02}"))
}

fn analyze_message_for(path: &str) -> String {
    let lower = path.to_ascii_lowercase();
    if lower.ends_with(".pdf") {
        return "正在分析 PDF 页面与文本块".to_string();
    }
    "正在分析正文、标题与表格".to_string()
}

fn parse_title_preset_lines(text: &str) -> Vec<String> {
    let mut items: Vec<String> = text
        .lines()
        .map(|l| l.trim().to_string())
        .filter(|l| !l.is_empty() && !l.starts_with('#') && !l.starts_with(';'))
        .map(|line| {
            line.split_once("*****")
                .map(|(label, _)| label.trim().to_string())
                .unwrap_or(line)
        })
        .filter(|l| !l.is_empty())
        .collect();
    items.sort();
    items.dedup();
    items
}
