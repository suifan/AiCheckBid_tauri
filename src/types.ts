export interface ParseDocumentRequest {
  filePath: string;
  rulesPath?: string;
}

export interface PageMarginsCm {
  top: number;
  bottom: number;
  left: number;
  right: number;
  headerDistance: number;
  footerDistance: number;
}

export interface DocumentMetrics {
  sectionCount: number;
  paragraphCount: number;
  tableCount: number;
  marginsCm?: PageMarginsCm;
}

export interface RuleIssue {
  category: string;
  rule: string;
  message: string;
  location: string;
  currentValue: string;
  expectedValue: string;
  severity: string;
  fixed: boolean;
  snippet?: string;
}

export interface ParseDocumentResponse {
  filePath: string;
  fileType: string;
  parser: string;
  pageCount?: number;
  warnings: string[];
  metrics?: DocumentMetrics;
  issues: RuleIssue[];
  reportText?: string;
  reportPath?: string;
  reportDocxPath?: string;
}

export interface LicenseStatus {
  authMode: string;
  udiskDrive: string;
  machineCode: string;
  serialNum: string;
  activated: boolean;
  regCode: string;
  planName: string;
  maxDocCount: number;
  pageLimit: number;
  validDays: number;
  expiresOn: string;
  useCount: number;
  overUseLimit: boolean;
  message: string;
}

export interface PlanInfo {
  id: string;
  name: string;
  validDays: number;
  pageLimit: number;
  priceYuan: number;
}

export interface FormatPreset {
  rulesPath: string;
  pageTop: string;
  pageBottom: string;
  pageLeft: string;
  pageRight: string;
  bodyFont: string;
  bodySize: string;
  bodyAlign: string;
  tableFont: string;
  tableSize: string;
  tableHAlign: string;
  tableVAlign: string;
  title1Font: string;
  title1Size: string;
  title2Font: string;
  title2Size: string;
}

export interface RulesConfig {
  rulesPath: string;
  values: Record<string, Record<string, string>>;
}
