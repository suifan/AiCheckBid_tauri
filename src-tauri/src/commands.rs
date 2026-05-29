use crate::license::{self, LicenseStatus, PlanInfo};
use crate::models::{ParseDocumentRequest, ParseDocumentResponse, RuleIssue};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::process::Command;

#[tauri::command]
pub fn parse_document(request: ParseDocumentRequest) -> Result<ParseDocumentResponse, String> {
    license::validate_before_check().map_err(|e| e.to_string())?;
    let page_limit = license::page_limit_for_check().map_err(|e| e.to_string())?;
    let mut req = request;
    if req.rules_path.as_deref().unwrap_or("").trim().is_empty() {
        req.rules_path = Some(get_default_rules_path()?);
    }
    let mut resp = crate::sidecar::parse_document(req).map_err(|e| e.to_string())?;

    if let Some(page_count) = resp.page_count {
        if page_count as i32 > page_limit {
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

    let _ = license::record_usage_once();
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
    pub page_top: String,
    pub page_bottom: String,
    pub page_left: String,
    pub page_right: String,
    pub body_font: String,
    pub body_size: String,
    pub body_align: String,
    pub table_font: String,
    pub table_size: String,
    pub table_h_align: String,
    pub table_v_align: String,
    pub title1_font: String,
    pub title1_size: String,
    pub title2_font: String,
    pub title2_size: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RulesConfig {
    pub rules_path: String,
    pub values: HashMap<String, HashMap<String, String>>,
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

fn ini_get<'a>(
    ini: &'a HashMap<String, HashMap<String, String>>,
    section: &str,
    key: &str,
) -> &'a str {
    ini.get(section)
        .and_then(|m| m.get(key))
        .map(|s| s.as_str())
        .unwrap_or("")
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
    let ini = parse_ini(&content);
    Ok(FormatPreset {
        rules_path: path,
        page_top: ini_get(&ini, "页面", "上边距").to_string(),
        page_bottom: ini_get(&ini, "页面", "下边距").to_string(),
        page_left: ini_get(&ini, "页面", "左边距").to_string(),
        page_right: ini_get(&ini, "页面", "右边距").to_string(),
        body_font: ini_get(&ini, "正文", "字体").to_string(),
        body_size: ini_get(&ini, "正文", "字号").to_string(),
        body_align: ini_get(&ini, "正文", "对齐方式").to_string(),
        table_font: ini_get(&ini, "表格", "字体").to_string(),
        table_size: ini_get(&ini, "表格", "字号").to_string(),
        table_h_align: ini_get(&ini, "表格", "表格水平对齐方式").to_string(),
        table_v_align: ini_get(&ini, "表格", "纵向对齐方式").to_string(),
        title1_font: ini_get(&ini, "一级标题", "字体").to_string(),
        title1_size: ini_get(&ini, "一级标题", "字号").to_string(),
        title2_font: ini_get(&ini, "二级标题", "字体").to_string(),
        title2_size: ini_get(&ini, "二级标题", "字号").to_string(),
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
    let updates = [
        ("页面", "上边距", preset.page_top.as_str()),
        ("页面", "下边距", preset.page_bottom.as_str()),
        ("页面", "左边距", preset.page_left.as_str()),
        ("页面", "右边距", preset.page_right.as_str()),
        ("正文", "字体", preset.body_font.as_str()),
        ("正文", "字号", preset.body_size.as_str()),
        ("正文", "对齐方式", preset.body_align.as_str()),
        ("表格", "字体", preset.table_font.as_str()),
        ("表格", "字号", preset.table_size.as_str()),
        ("表格", "表格水平对齐方式", preset.table_h_align.as_str()),
        ("表格", "纵向对齐方式", preset.table_v_align.as_str()),
        ("一级标题", "字体", preset.title1_font.as_str()),
        ("一级标题", "字号", preset.title1_size.as_str()),
        ("二级标题", "字体", preset.title2_font.as_str()),
        ("二级标题", "字号", preset.title2_size.as_str()),
    ];
    for (sec, key, val) in updates {
        content = upsert_ini_value(&content, sec, key, val);
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
            let mut items: Vec<String> = content
                .lines()
                .map(|l| l.trim().to_string())
                .filter(|l| !l.is_empty() && !l.starts_with('#') && !l.starts_with(';'))
                .collect();
            items.sort();
            items.dedup();
            if !items.is_empty() {
                return Ok(items);
            }
        }
    }

    Ok(vec![
        "一、".to_string(),
        "（一）".to_string(),
        "1.".to_string(),
        "（1）".to_string(),
        "①".to_string(),
        "A.".to_string(),
        "a)".to_string(),
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
pub fn open_result_folder() -> Result<(), String> {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let app_root = manifest_dir
        .parent()
        .map(|p| p.to_path_buf())
        .unwrap_or(manifest_dir);
    let result_dir = app_root.join("result");
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
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let app_root = manifest_dir
        .parent()
        .map(|p| p.to_path_buf())
        .unwrap_or(manifest_dir);

    let mut candidates: Vec<PathBuf> = vec![
        app_root.join("OpenResult.exe"),
        app_root.join("查看检查结果.xlsm"),
    ];
    if let Ok(exe) = std::env::current_exe() {
        if let Some(exe_dir) = exe.parent() {
            candidates.push(exe_dir.join("OpenResult.exe"));
            candidates.push(exe_dir.join("查看检查结果.xlsm"));
            candidates.push(exe_dir.join("resources").join("OpenResult.exe"));
            candidates.push(exe_dir.join("resources").join("查看检查结果.xlsm"));
        }
    }

    for p in candidates {
        if p.exists() {
            Command::new("explorer.exe")
                .arg(p.as_os_str())
                .spawn()
                .map_err(|e| e.to_string())?;
            return Ok(());
        }
    }

    Err("未找到查看检查结果入口文件（OpenResult.exe 或 查看检查结果.xlsm）".to_string())
}
