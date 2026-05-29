use std::path::PathBuf;
use std::process::Command;

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
    let file_type = detect_file_type(&request.file_path);
    let mut last_error: Option<SidecarError> = None;
    let roots = candidate_roots();

    // 路由策略：
    // - doc/docx：优先 net48（规则口径更贴近原版），失败再回退 net8
    // - pdf：优先 net8（PDF规则链路集中在 net8）
    // - 其他：先 net8，再 net48
    let route: &[&str] = match file_type.as_str() {
        "doc" | "docx" => &["net48", "net8"],
        "pdf" => &["net8", "net48"],
        _ => &["net8", "net48"],
    };

    for engine in route {
        for root in &roots {
            let ret = if *engine == "net48" {
                try_net48_sidecar(root, &request)
            } else {
                try_net8_sidecar(root, &request)
            };
            match ret {
                Ok(resp) => return Ok(resp),
                Err(err) => last_error = Some(err),
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

fn candidate_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    if let Some(app_root) = manifest_dir.parent() {
        roots.push(app_root.to_path_buf());
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            roots.push(dir.to_path_buf());
            roots.push(dir.join("resources"));
        }
    }
    roots
}

fn try_net48_sidecar(
    app_root: &PathBuf,
    request: &ParseDocumentRequest,
) -> Result<ParseDocumentResponse, SidecarError> {
    let exe = app_root
        .join("sidecar")
        .join("DocParserSidecarNet48")
        .join("bin")
        .join("Debug")
        .join("DocParserSidecarNet48.exe");
    if !exe.exists() {
        return Err(SidecarError::Io(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            "net48 sidecar not found",
        )));
    }

    let mut cmd = Command::new(exe);
    cmd.arg(request.file_path.clone());
    if let Some(rules_path) = request.rules_path.clone() {
        if !rules_path.trim().is_empty() {
            cmd.arg(rules_path);
        }
    }
    let output = cmd.output()?;
    if !output.status.success() {
        let code = output.status.code().unwrap_or(-1);
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        if !stderr.is_empty() {
            return Err(SidecarError::Stderr(stderr));
        }
        return Err(SidecarError::Exit(code));
    }

    let stdout = String::from_utf8_lossy(&output.stdout);
    let response: ParseDocumentResponse = serde_json::from_str(stdout.trim())?;
    Ok(response)
}

fn try_net8_sidecar(
    app_root: &PathBuf,
    request: &ParseDocumentRequest,
) -> Result<ParseDocumentResponse, SidecarError> {
    let req_json = serde_json::to_string(request)?;
    for exe in [
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
    ] {
        if exe.exists() {
            let output = Command::new(exe).arg(&req_json).output()?;
            if output.status.success() {
                let stdout = String::from_utf8_lossy(&output.stdout);
                let response: ParseDocumentResponse = serde_json::from_str(stdout.trim())?;
                return Ok(response);
            }
            let code = output.status.code().unwrap_or(-1);
            let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
            if !stderr.is_empty() {
                return Err(SidecarError::Stderr(stderr));
            }
            return Err(SidecarError::Exit(code));
        }
    }

    let csproj = app_root
        .join("sidecar")
        .join("DocParserSidecar")
        .join("DocParserSidecar.csproj");
    if !csproj.exists() {
        return Err(SidecarError::Io(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            "net8 sidecar project not found",
        )));
    }

    let output = Command::new("dotnet")
        .arg("run")
        .arg("--project")
        .arg(csproj)
        .arg("--")
        .arg(req_json)
        .output()?;

    if !output.status.success() {
        let code = output.status.code().unwrap_or(-1);
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        if !stderr.is_empty() {
            return Err(SidecarError::Stderr(stderr));
        }
        return Err(SidecarError::Exit(code));
    }

    let stdout = String::from_utf8_lossy(&output.stdout);
    let response: ParseDocumentResponse = serde_json::from_str(stdout.trim())?;
    Ok(response)
}
