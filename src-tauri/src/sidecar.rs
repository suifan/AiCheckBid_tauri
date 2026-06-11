use std::io::{Read, Write};
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::thread;

use thiserror::Error;

use crate::models::{ParseDocumentRequest, ParseDocumentResponse};

#[derive(Debug, Error)]
pub enum SidecarError {
    #[error("serialize request failed: {0}")]
    Serialize(#[from] serde_json::Error),
    #[error("sidecar execution failed: {0}")]
    Io(#[from] std::io::Error),
    #[error("sidecar returned non-zero code: {0}")]
    Exit(i32),
    #[error("sidecar stderr: {0}")]
    Stderr(String),
}

pub fn parse_document(request: ParseDocumentRequest) -> Result<ParseDocumentResponse, SidecarError> {
    parse_document_with_progress(request, |_| {})
}

pub fn parse_document_with_progress<F>(
    request: ParseDocumentRequest,
    mut on_progress: F,
) -> Result<ParseDocumentResponse, SidecarError>
where
    F: FnMut(&str),
{
    let file_type = detect_file_type(&request.file_path);
    let mut last_error: Option<SidecarError> = None;
    let roots = candidate_roots();
    let result_dir = runtime_result_dir();
    let file_name = file_name_of(&request.file_path);

    on_progress(&format!("开始检查文档：{file_name}"));
    on_progress(&format!("正在加载文档：{file_name}"));
    on_progress(match file_type.as_str() {
        "pdf" => "正在分析 PDF 页面与文本块",
        "doc" | "docx" => "正在分析正文、标题与表格",
        _ => "正在分析文档内容",
    });

    // 路由策略：
    // - doc/docx：优先 net48（规则口径更贴近原版），失败再回退 net8
    // - pdf：优先 net8（PDF规则链路集中在 net8）
    // - 其他：先 net8，再 net48
    let route: &[&str] = match file_type.as_str() {
        "doc" | "docx" => &["net48", "net8"],
        "pdf" => &["net8", "net48"],
        _ => &["net8", "net48"],
    };
    debug_report(
        "D",
        "sidecar.rs:parse_document_with_progress",
        "starting parse route",
        serde_json::json!({
            "filePath": request.file_path,
            "fileType": file_type,
            "resultDir": result_dir.to_string_lossy().to_string(),
            "route": route,
            "roots": roots.iter().map(|p| p.to_string_lossy().to_string()).collect::<Vec<_>>()
        }),
    );

    for engine in route {
        for root in &roots {
            let engine_name = if *engine == "net48" { "net48" } else { "net8" };
            on_progress(&format!("正在尝试 {engine_name} 解析器：{file_name}"));
            let ret = if *engine == "net48" {
                try_net48_sidecar(root, &request, &result_dir, &mut on_progress)
            } else {
                try_net8_sidecar(root, &request, &result_dir, &mut on_progress)
            };
            match ret {
                Ok(resp) => {
                    on_progress("正在生成检查报告");
                    on_progress(&format!("完成检查：{file_name}"));
                    return Ok(resp);
                }
                Err(err) => {
                    let should_replace = match last_error.as_ref() {
                        Some(existing) => !(!is_not_found_error(existing) && is_not_found_error(&err)),
                        None => true,
                    };
                    debug_report(
                        "C",
                        "sidecar.rs:parse_document_with_progress",
                        "sidecar attempt failed",
                        serde_json::json!({
                            "engine": engine_name,
                            "root": root.to_string_lossy().to_string(),
                            "error": err.to_string(),
                            "replaceLastError": should_replace
                        }),
                    );
                    if should_replace {
                        last_error = Some(err);
                    }
                }
            }
        }
    }
    Err(last_error.unwrap_or_else(|| {
        SidecarError::Io(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            "sidecar not found",
        ))
    }))
}

fn file_name_of(path: &str) -> String {
    PathBuf::from(path)
        .file_name()
        .and_then(|x| x.to_str())
        .unwrap_or(path)
        .to_string()
}

fn detect_file_type(path: &str) -> String {
    let lower = path.trim().to_ascii_lowercase();
    if lower.ends_with(".docx") {
        return "docx".to_string();
    }
    if lower.ends_with(".doc") {
        return "doc".to_string();
    }
    if lower.ends_with(".pdf") {
        return "pdf".to_string();
    }
    "unknown".to_string()
}

fn debug_report(hypothesis_id: &str, location: &str, msg: &str, data: serde_json::Value) {
    // #region debug-point A:report-sidecar-runtime
    let payload = serde_json::json!({
        "sessionId": "sidecar-path-bug",
        "runId": "pre-fix",
        "hypothesisId": hypothesis_id,
        "location": location,
        "msg": format!("[DEBUG] {msg}"),
        "data": data,
        "ts": 0
    });
    if let Ok(mut stream) = std::net::TcpStream::connect("127.0.0.1:7777") {
        let body = payload.to_string();
        let request = format!(
            "POST /event HTTP/1.1\r\nHost: 127.0.0.1:7777\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
            body.as_bytes().len(),
            body
        );
        let _ = stream.write_all(request.as_bytes());
        let _ = stream.flush();
    }
    // #endregion
}

fn candidate_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    if let Some(app_root) = manifest_dir.parent() {
        if app_root.exists() {
            roots.push(app_root.to_path_buf());
        }
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            roots.push(dir.to_path_buf());
            roots.push(dir.join("_up_"));
            roots.push(dir.join("resources"));
        }
    }
    roots.retain(|p| p.exists());
    roots.dedup();
    debug_report(
        "A",
        "sidecar.rs:candidate_roots",
        "collected sidecar candidate roots",
        serde_json::json!({
            "manifestDir": manifest_dir.to_string_lossy().to_string(),
            "currentExe": std::env::current_exe().ok().map(|p| p.to_string_lossy().to_string()),
            "roots": roots
                .iter()
                .map(|p| p.to_string_lossy().to_string())
                .collect::<Vec<_>>()
        }),
    );
    roots
}

fn is_dev_runtime() -> bool {
    let Ok(exe) = std::env::current_exe() else {
        return false;
    };
    let Some(exe_dir) = exe.parent() else {
        return false;
    };
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
    parent_name == "target" && (dir_name == "debug" || dir_name == "release")
}

fn is_not_found_error(err: &SidecarError) -> bool {
    match err {
        SidecarError::Io(io_err) => io_err.kind() == std::io::ErrorKind::NotFound,
        _ => false,
    }
}

fn runtime_result_dir() -> PathBuf {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let dev_root = manifest_dir
        .parent()
        .map(|p| p.to_path_buf())
        .unwrap_or(manifest_dir);
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
                return dev_root.join("result");
            }
            return exe_dir.join("result");
        }
    }
    dev_root.join("result")
}

fn try_net48_sidecar(
    app_root: &PathBuf,
    request: &ParseDocumentRequest,
    result_dir: &PathBuf,
    on_progress: &mut dyn FnMut(&str),
) -> Result<ParseDocumentResponse, SidecarError> {
    let mut found: Option<PathBuf> = None;
    let candidates = [
        app_root
            .join("sidecar")
            .join("DocParserSidecarNet48")
            .join("bin")
            .join("Debug")
            .join("DocParserSidecarNet48.exe"),
        app_root
            .join("sidecar")
            .join("DocParserSidecarNet48")
            .join("bin")
            .join("Release")
            .join("DocParserSidecarNet48.exe"),
    ];
    debug_report(
        "B",
        "sidecar.rs:try_net48_sidecar",
        "checking net48 candidates",
        serde_json::json!({
            "appRoot": app_root.to_string_lossy().to_string(),
            "candidates": candidates.iter().map(|p| serde_json::json!({"path": p.to_string_lossy().to_string(), "exists": p.exists()})).collect::<Vec<_>>()
        }),
    );
    for exe in candidates {
        if exe.exists() {
            found = Some(exe);
            break;
        }
    }
    let Some(exe) = found else {
        return Err(SidecarError::Io(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            "net48 sidecar not found in Debug/Release",
        )));
    };

    let mut cmd = Command::new(exe);
    cmd.env("AICHECKBID_RESULT_DIR", result_dir);
    if let Some(batch_serial) = request.batch_serial {
        cmd.env("AICHECKBID_BATCH_SERIAL", batch_serial.to_string());
    }
    if let Some(batch_total) = request.batch_total {
        cmd.env("AICHECKBID_BATCH_TOTAL", batch_total.to_string());
    }
    if let Some(overview_mode) = request.overview_mode.clone() {
        if !overview_mode.trim().is_empty() {
            cmd.env("AICHECKBID_OVERVIEW_MODE", overview_mode);
        }
    }
    cmd.arg(request.file_path.clone());
    if let Some(rules_path) = request.rules_path.clone() {
        if !rules_path.trim().is_empty() {
            cmd.arg(rules_path);
        }
    }
    run_sidecar_command(cmd, on_progress)
}

fn try_net8_sidecar(
    app_root: &PathBuf,
    request: &ParseDocumentRequest,
    result_dir: &PathBuf,
    on_progress: &mut dyn FnMut(&str),
) -> Result<ParseDocumentResponse, SidecarError> {
    let req_json = serde_json::to_string(request)?;
    let candidates = [
        app_root
            .join("sidecar")
            .join("DocParserSidecar")
            .join("bin")
            .join("Debug")
            .join("net8.0")
            .join("DocParserSidecar.exe"),
        app_root
            .join("sidecar")
            .join("DocParserSidecar")
            .join("bin")
            .join("Release")
            .join("net8.0")
            .join("DocParserSidecar.exe"),
    ];
    debug_report(
        "B",
        "sidecar.rs:try_net8_sidecar",
        "checking net8 candidates",
        serde_json::json!({
            "appRoot": app_root.to_string_lossy().to_string(),
            "candidates": candidates.iter().map(|p| serde_json::json!({"path": p.to_string_lossy().to_string(), "exists": p.exists()})).collect::<Vec<_>>()
        }),
    );
    for exe in candidates {
        if exe.exists() {
            let mut cmd = Command::new(exe);
            cmd.env("AICHECKBID_RESULT_DIR", result_dir);
            cmd.arg(&req_json);
            return run_sidecar_command(cmd, on_progress);
        }
    }

    let csproj = app_root
        .join("sidecar")
        .join("DocParserSidecar")
        .join("DocParserSidecar.csproj");
    debug_report(
        "B",
        "sidecar.rs:try_net8_sidecar",
        "checking net8 project fallback",
        serde_json::json!({
            "appRoot": app_root.to_string_lossy().to_string(),
            "isDevRuntime": is_dev_runtime(),
            "csproj": csproj.to_string_lossy().to_string(),
            "csprojExists": csproj.exists()
        }),
    );
    if !is_dev_runtime() || !csproj.exists() {
        return Err(SidecarError::Io(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            "net8 sidecar not found in packaged resources",
        )));
    }

    let mut cmd = Command::new("dotnet");
    cmd.env("AICHECKBID_RESULT_DIR", result_dir);
    cmd.arg("run")
        .arg("--project")
        .arg(csproj)
        .arg("--")
        .arg(req_json);
    run_sidecar_command(cmd, on_progress)
}

fn run_sidecar_command(
    mut cmd: Command,
    on_progress: &mut dyn FnMut(&str),
) -> Result<ParseDocumentResponse, SidecarError> {
    cmd.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = cmd.spawn()?;

    let stdout = child.stdout.take().ok_or_else(|| {
        SidecarError::Io(std::io::Error::new(
            std::io::ErrorKind::BrokenPipe,
            "sidecar stdout not captured",
        ))
    })?;
    let stderr = child.stderr.take().ok_or_else(|| {
        SidecarError::Io(std::io::Error::new(
            std::io::ErrorKind::BrokenPipe,
            "sidecar stderr not captured",
        ))
    })?;

    let stdout_thread = thread::spawn(move || -> Result<Vec<u8>, std::io::Error> {
        let mut buf = Vec::new();
        let mut reader = std::io::BufReader::new(stdout);
        reader.read_to_end(&mut buf)?;
        Ok(buf)
    });

    let stderr_thread = thread::spawn(move || -> Result<Vec<u8>, std::io::Error> {
        let mut buf = Vec::new();
        let mut reader = std::io::BufReader::new(stderr);
        reader.read_to_end(&mut buf)?;
        Ok(buf)
    });

    let status = child.wait()?;
    let stdout_bytes = stdout_thread
        .join()
        .map_err(|_| {
            SidecarError::Io(std::io::Error::new(
                std::io::ErrorKind::Other,
                "join sidecar stdout thread failed",
            ))
        })??;
    let stderr_bytes = stderr_thread
        .join()
        .map_err(|_| {
            SidecarError::Io(std::io::Error::new(
                std::io::ErrorKind::Other,
                "join sidecar stderr thread failed",
            ))
        })??;

    let stderr_text = decode_sidecar_text(&stderr_bytes);
    let mut stderr_lines = Vec::new();
    for raw_line in stderr_text.lines() {
        let text = raw_line.trim().to_string();
        if text.is_empty() {
            continue;
        }
        if let Some(progress) = text.strip_prefix("PROGRESS|") {
            on_progress(progress.trim());
        } else {
            stderr_lines.push(text);
        }
    }

    let stdout_text = decode_sidecar_text(&stdout_bytes);

    if !status.success() {
        let code = status.code().unwrap_or(-1);
        let stderr = stderr_lines.join("\n");
        if !stderr.trim().is_empty() {
            return Err(SidecarError::Stderr(stderr));
        }
        return Err(SidecarError::Exit(code));
    }

    let response: ParseDocumentResponse = serde_json::from_str(stdout_text.trim())?;
    Ok(response)
}

fn decode_sidecar_text(bytes: &[u8]) -> String {
    if bytes.starts_with(&[0xFF, 0xFE]) {
        let body = &bytes[2..];
        let units = body
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .collect::<Vec<_>>();
        return String::from_utf16_lossy(&units);
    }
    if bytes.starts_with(&[0xFE, 0xFF]) {
        let body = &bytes[2..];
        let units = body
            .chunks_exact(2)
            .map(|chunk| u16::from_be_bytes([chunk[0], chunk[1]]))
            .collect::<Vec<_>>();
        return String::from_utf16_lossy(&units);
    }
    match String::from_utf8(bytes.to_vec()) {
        Ok(text) => text,
        Err(_) => String::from_utf8_lossy(bytes).to_string(),
    }
}
