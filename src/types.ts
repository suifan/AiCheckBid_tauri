export interface ParseDocumentRequest {
  filePath: string;
  rulesPath?: string;
  batchSerial?: number;
  batchTotal?: number;
  overviewMode?: "replace" | "append";
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

export interface DevActivationResult {
  regCode: string;
  status: LicenseStatus;
}

export interface AppDebugFlags {
  devBuild: boolean;
}

export interface CheckProgressEvent {
  stage: string;
  message: string;
}

export interface ResultArtifact {
  id: string;
  displayName: string;
  txtPath?: string;
  docxPath?: string;
  reportText?: string;
  updatedAt: string;
}

export interface ResultOverviewSectionLink {
  name: string;
  txtPath: string;
}

export interface ResultOverviewItem {
  id: string;
  sourceName: string;
  displayName: string;
  sectionLinks: ResultOverviewSectionLink[];
  reportDocxPath?: string;
  sourceCopyPath?: string;
}

export interface ResultOverview {
  exists: boolean;
  path?: string;
  rawText?: string;
  items: ResultOverviewItem[];
}

export interface TextFileContent {
  exists: boolean;
  path: string;
  text?: string;
}

export interface FormatPreset {
  rulesPath: string;
  values: Record<string, Record<string, string>>;
}

export interface RulesConfig {
  rulesPath: string;
  values: Record<string, Record<string, string>>;
}
