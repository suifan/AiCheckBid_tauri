use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ParseDocumentRequest {
    pub file_path: String,
    pub rules_path: Option<String>,
    pub batch_serial: Option<i32>,
    pub batch_total: Option<i32>,
    pub overview_mode: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PageMarginsCm {
    pub top: f64,
    pub bottom: f64,
    pub left: f64,
    pub right: f64,
    pub header_distance: f64,
    pub footer_distance: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DocumentMetrics {
    pub section_count: i32,
    pub paragraph_count: i32,
    pub table_count: i32,
    pub margins_cm: Option<PageMarginsCm>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuleIssue {
    pub category: String,
    pub rule: String,
    pub message: String,
    pub location: String,
    pub current_value: String,
    pub expected_value: String,
    pub severity: String,
    pub fixed: bool,
    pub snippet: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ParseDocumentResponse {
    pub file_path: String,
    pub file_type: String,
    pub parser: String,
    pub page_count: Option<u32>,
    pub warnings: Vec<String>,
    pub metrics: Option<DocumentMetrics>,
    pub issues: Vec<RuleIssue>,
    pub report_text: Option<String>,
    pub report_path: Option<String>,
    pub report_docx_path: Option<String>,
}
