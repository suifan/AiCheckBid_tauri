import { useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/tauri";
import { confirm, open } from "@tauri-apps/api/dialog";
import { listen } from "@tauri-apps/api/event";
import type {
  AppDebugFlags,
  CheckProgressEvent,
  DevActivationResult,
  FormatPreset,
  LicenseStatus,
  ParseDocumentRequest,
  ParseDocumentResponse,
  PlanInfo,
  ResultArtifact,
  ResultOverview,
  RulesConfig,
  TextFileContent,
} from "./types";

const BOOL_TRUE = new Set(["true", "True", "TRUE", "1"]);

type TabKey = "common" | "page" | "body" | "table" | "title";

const TAB_LABELS: Record<TabKey, string> = {
  common: "通用设置",
  page: "页面设置",
  body: "正文设置",
  table: "表格设置",
  title: "标题设置",
};

const FONT_SIZES = ["小四", "四号", "小五", "五号", "三号", "小三", "二号", "小二", "一号", "小一"];
const ALIGNS = ["左对齐", "右对齐", "居中", "分散对齐", "两端对齐"];
const SPACING = ["标准", "紧缩", "增大"];
const LINE_HEIGHT = ["1.5倍行距", "1倍行距", "2倍行距", "单倍行距"];

function boolToText(v: boolean): string {
  return v ? "True" : "False";
}

function isTrue(v: string | undefined): boolean {
  return BOOL_TRUE.has((v || "").trim());
}

function getBaseName(filePath: string): string {
  const parts = filePath.replace(/\\/g, "/").split("/");
  return parts[parts.length - 1] || filePath;
}

function getDirName(filePath: string): string {
  const normalized = filePath.replace(/\\/g, "/");
  const idx = normalized.lastIndexOf("/");
  if (idx <= 0) return filePath;
  return `${normalized.slice(0, idx + 1).replace(/\//g, "\\")}`;
}

export default function App() {
  const [license, setLicense] = useState<LicenseStatus | null>(null);
  const [plans, setPlans] = useState<PlanInfo[]>([]);
  const [selectedPlan, setSelectedPlan] = useState("");
  const [generatedRegCode, setGeneratedRegCode] = useState("");
  const [regCodeInput, setRegCodeInput] = useState("");
  const [authMode, setAuthMode] = useState<"device" | "udisk">("device");
  const [udiskDrive, setUdiskDrive] = useState("E:");

  const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
  const [selectedListFile, setSelectedListFile] = useState("");
  const [rulesPath, setRulesPath] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [progressText, setProgressText] = useState("就绪");
  const [result, setResult] = useState<ParseDocumentResponse | null>(null);
  const [batchResults, setBatchResults] = useState<ParseDocumentResponse[]>([]);
  const [resultViewFile, setResultViewFile] = useState("");
  const [historyResults, setHistoryResults] = useState<ResultArtifact[]>([]);
  const [selectedHistoryId, setSelectedHistoryId] = useState("");
  const [resultOverview, setResultOverview] = useState<ResultOverview | null>(null);
  const [selectedOverviewId, setSelectedOverviewId] = useState("");
  const [selectedOverviewSectionPath, setSelectedOverviewSectionPath] = useState("");
  const [overviewSectionText, setOverviewSectionText] = useState("");

  const [activeTab, setActiveTab] = useState<TabKey>("common");
  const [ruleValues, setRuleValues] = useState<Record<string, Record<string, string>>>({});
  const [titleRulePresets, setTitleRulePresets] = useState<string[]>([]);
  const [devBuild, setDevBuild] = useState(false);

  const maxDocCount = license?.maxDocCount ?? 2;

  function readRule(section: string, key: string, fallback = ""): string {
    return ruleValues?.[section]?.[key] ?? fallback;
  }

  function writeRule(section: string, key: string, value: string) {
    setRuleValues((prev) => {
      const next: Record<string, Record<string, string>> = {
        ...prev,
        [section]: {
          ...(prev[section] || {}),
          [key]: value,
        },
      };

      // 旧版联动：开启“智能修正”时，批注模式强制为“不批注”。
      if (section === "检查项" && key === "智能修正" && isTrue(value)) {
        next["检查项"] = {
          ...(next["检查项"] || {}),
          批注标记: "不批注",
        };
      }

      // 旧版联动：批注模式不为“不批注”时，自动关闭“智能修正”。
      if (section === "检查项" && key === "批注标记" && value !== "不批注") {
        next["检查项"] = {
          ...(next["检查项"] || {}),
          智能修正: "False",
        };
      }

      return next;
    });
  }

  async function loadRuleConfig(explicitPath?: string) {
    const cfg = await invoke<RulesConfig>("get_rules_config", { rulesPath: explicitPath ?? null });
    setRulesPath(cfg.rulesPath);
    setRuleValues(cfg.values || {});
  }

  async function saveRuleConfig() {
    const config: RulesConfig = { rulesPath, values: ruleValues };
    const saved = await invoke<string>("save_rules_config", { config });
    setRulesPath(saved);
    await loadRuleConfig(saved);
  }

  async function loadTitleRulePresets() {
    try {
      const presets = await invoke<string[]>("list_title_rule_presets");
      setTitleRulePresets((presets || []).filter((x) => x.trim().length > 0));
    } catch {
      setTitleRulePresets([]);
    }
  }

  function applyFormatPreset(preset: FormatPreset) {
    setRuleValues(preset.values || {});
  }

  async function loadFormatPreset() {
    setError("");
    try {
      const preset = await invoke<FormatPreset>("get_format_preset", { rulesPath: rulesPath.trim() || null });
      setRulesPath(preset.rulesPath || rulesPath);
      applyFormatPreset(preset);
    } catch (e) {
      setError(String(e));
    }
  }

  async function saveFormatPreset() {
    setError("");
    try {
      const preset: FormatPreset = {
        rulesPath,
        values: ruleValues,
      };
      const saved = await invoke<string>("save_format_preset", { preset });
      setRulesPath(saved);
      await loadRuleConfig(saved);
    } catch (e) {
      setError(String(e));
    }
  }

  async function refreshLicense() {
    const status = await invoke<LicenseStatus>("get_license_status");
    setLicense(status);
    setRegCodeInput(status.regCode || "");
    setAuthMode((status.authMode === "udisk" ? "udisk" : "device") as "device" | "udisk");
    setUdiskDrive(status.udiskDrive || "E:");
  }

  async function refreshPlans() {
    const items = await invoke<PlanInfo[]>("list_plans");
    setPlans(items);
    if (!selectedPlan && items.length > 0) {
      setSelectedPlan(items[0].id);
    }
  }

  async function refreshDebugFlags() {
    try {
      const flags = await invoke<AppDebugFlags>("get_app_debug_flags");
      setDevBuild(Boolean(flags.devBuild));
    } catch {
      setDevBuild(false);
    }
  }

  async function refreshHistoryResults() {
    const items = await invoke<ResultArtifact[]>("list_result_artifacts");
    setHistoryResults(items || []);
    setSelectedHistoryId((prev) => {
      if (prev && (items || []).some((item) => item.id === prev)) return prev;
      return items?.[0]?.id || "";
    });
  }

  async function refreshResultOverview() {
    const overview = await invoke<ResultOverview>("get_result_overview");
    setResultOverview(overview);
    setSelectedOverviewId((prev) => {
      if (prev && (overview.items || []).some((item) => item.id === prev)) return prev;
      return overview.items?.[0]?.id || "";
    });
  }

  async function loadOverviewSection(path?: string) {
    if (!path) {
      setSelectedOverviewSectionPath("");
      setOverviewSectionText("");
      return;
    }
    try {
      const file = await invoke<TextFileContent>("get_text_file_content", { path });
      setSelectedOverviewSectionPath(file.path || path);
      setOverviewSectionText(file.text || "");
    } catch (e) {
      setError(String(e));
    }
  }

  useEffect(() => {
    (async () => {
      try {
        await Promise.all([
          refreshLicense(),
          refreshPlans(),
          refreshDebugFlags(),
          loadRuleConfig(),
          loadTitleRulePresets(),
          refreshHistoryResults(),
          refreshResultOverview(),
        ]);
      } catch (e) {
        setError(String(e));
      }
    })();
  }, []);

  useEffect(() => {
    let disposed = false;
    let unlisten: null | (() => void) = null;

    void (async () => {
      unlisten = await listen<CheckProgressEvent>("check-progress", (event) => {
        if (!disposed && event.payload?.message) {
          setProgressText(event.payload.message);
        }
      });
    })();

    return () => {
      disposed = true;
      if (unlisten) {
        unlisten();
      }
    };
  }, []);

  async function chooseFiles() {
    setError("");
    try {
      await invoke("precheck_choose_files", { regCode: regCodeInput.trim() || null });
      const picked = await open({
        multiple: true,
        filters: [{ name: "文档", extensions: ["doc", "docx", "pdf"] }],
      });
      if (!picked) return;
      const arr = Array.isArray(picked) ? picked : [picked];
      const merged = [...selectedFiles, ...arr.filter((p) => !selectedFiles.includes(p))];
      if (merged.length > maxDocCount) {
        setError(`所购版本最多只能选择${maxDocCount}个文档`);
        setSelectedFiles(merged.slice(0, maxDocCount));
      } else {
        setSelectedFiles(merged);
      }
    } catch (e) {
      setError(String(e));
    }
  }

  async function activate() {
    setError("");
    try {
      const status = await invoke<LicenseStatus>("activate_license", { regCode: regCodeInput.trim() });
      setLicense(status);
      setRegCodeInput(status.regCode);
    } catch (e) {
      setError(String(e));
    }
  }

  async function applyAuthMode() {
    setError("");
    try {
      const status = await invoke<LicenseStatus>("set_auth_mode", {
        mode: authMode,
        udiskDrive: authMode === "udisk" ? udiskDrive : null,
      });
      setLicense(status);
    } catch (e) {
      setError(String(e));
    }
  }

  async function saveAllSettings() {
    setError("");
    try {
      const status = await invoke<LicenseStatus>("set_auth_mode", {
        mode: authMode,
        udiskDrive: authMode === "udisk" ? udiskDrive : null,
      });
      setLicense(status);
      await saveRuleConfig();
      await refreshLicense();
      setProgressText("保存完成");
    } catch (e) {
      setError(String(e));
    }
  }

  async function purchaseAndGenerate() {
    setError("");
    try {
      const code = await invoke<string>("purchase_plan", { planId: selectedPlan });
      setGeneratedRegCode(code);
      setRegCodeInput(code);
    } catch (e) {
      setError(String(e));
    }
  }

  async function devActivateYearly() {
    setError("");
    try {
      const result = await invoke<DevActivationResult>("dev_activate_yearly");
      setGeneratedRegCode(result.regCode);
      setRegCodeInput(result.regCode);
      setLicense(result.status);
    } catch (e) {
      setError(String(e));
    }
  }

  async function parseOne(path: string, batchSerial?: number, batchTotal?: number): Promise<ParseDocumentResponse> {
    const request: ParseDocumentRequest = {
      filePath: path,
      rulesPath: rulesPath.trim() || undefined,
      batchSerial,
      batchTotal,
      overviewMode: batchSerial && batchSerial > 1 ? "append" : "replace",
    };
    return invoke<ParseDocumentResponse>("parse_document", { request });
  }

  async function runBatchParse() {
    setLoading(true);
    setError("");
    setProgressText("正在准备检查...");
    setResult(null);
    setBatchResults([]);

    try {
      await saveRuleConfig();
      if (selectedFiles.length === 0) throw new Error("请先选择文档");
      if (selectedFiles.length > maxDocCount) throw new Error(`所购版本最多只能选择${maxDocCount}个文档`);

      const all: ParseDocumentResponse[] = [];
      for (let i = 0; i < selectedFiles.length; i += 1) {
        const p = selectedFiles[i];
        setProgressText(`正在检查（${i + 1}/${selectedFiles.length}）：${getBaseName(p)}`);
        all.push(await parseOne(p, i + 1, selectedFiles.length));
      }
      setBatchResults(all);
      setResult(all[all.length - 1] ?? null);
      setResultViewFile(all[0]?.filePath ?? "");
      await refreshLicense();
      await refreshHistoryResults();
      await refreshResultOverview();
      const total = all.reduce((sum, item) => sum + item.issues.length, 0);
      const doneMsg = `检查结束，共${all.length}个文档，总问题${total}条`;
      setProgressText(doneMsg);
      const shouldOpen = await confirm(`${doneMsg}，是否查看检查结果？`, {
        title: "消息提示",
      });
      if (shouldOpen) {
        const opened = await openBatchResultsByFiles(all.map((item) => item.filePath));
        if (opened > 0) {
          setProgressText(opened > 1 ? `已打开${opened}个文档的检查结果` : `已打开检查结果：${getBaseName(all[0]?.filePath || "")}`);
        } else {
          await openSelectedResult();
        }
      }
    } catch (e) {
      setError(String(e));
      setProgressText("检查失败");
    } finally {
      setLoading(false);
    }
  }

  async function openResultFolder() {
    setError("");
    try {
      await invoke("open_result_folder");
    } catch (e) {
      setError(String(e));
    }
  }

  async function openPath(path?: string) {
    if (!path) return;
    setError("");
    try {
      await invoke("open_path", { path });
    } catch (e) {
      setError(String(e));
    }
  }

  async function openGeneratedReport(item?: ParseDocumentResponse): Promise<boolean> {
    if (!item) return false;
    const target = item.reportDocxPath || item.reportPath;
    if (!target) return false;
    await openPath(target);
    return true;
  }

  async function chooseRulesFile() {
    setError("");
    try {
      const picked = await open({
        multiple: false,
        filters: [{ name: "规则文件", extensions: ["ini"] }],
      });
      if (!picked || Array.isArray(picked)) return;
      await loadRuleConfig(picked);
    } catch (e) {
      setError(String(e));
    }
  }

  async function openSingleResultByFile(filePath: string, silent = false) {
    try {
      await invoke("open_result_for_file", { filePath });
      setSelectedListFile(filePath);
      setResultViewFile(filePath);
      return true;
    } catch (e) {
      if (!silent) {
        setError(String(e));
      }
      return false;
    }
  }

  async function openBatchResultsByFiles(filePaths: string[]) {
    let opened = 0;
    for (const filePath of filePaths) {
      if (!filePath) continue;
      if (await openSingleResultByFile(filePath, true)) {
        opened += 1;
      }
    }
    return opened;
  }

  function focusResultOverview(): boolean {
    if (!resultOverview?.exists) return false;
    const targetId = selectedOverviewId || resultOverview.items[0]?.id;
    if (!targetId) return false;
    setSelectedOverviewId(targetId);
    setProgressText(`已在下方显示结果总览：${resultOverview.items.find((x) => x.id === targetId)?.displayName || targetId}`);
    return true;
  }

  async function openSelectedResult() {
    setError("");
    const hasCurrentSelection = selectedFiles.length > 0;
    const preferOverview = (batchResults.length > 1 || selectedFiles.length > 1) && resultOverview?.exists;
    if (!hasCurrentSelection && !resultOverview?.exists && historyResults.length === 0) {
      setError("请先选择文档");
      return;
    }
    if (preferOverview && focusResultOverview()) {
      return;
    }
    if (batchResults.length > 0 && hasCurrentSelection) {
      const targetFile = selectedListFile || resultViewFile || batchResults[0]?.filePath || selectedFiles[0];
      if (targetFile) {
        if (await openSingleResultByFile(targetFile, true)) {
          setProgressText(`已打开检查结果：${getBaseName(targetFile)}`);
          return;
        }
        setSelectedListFile(targetFile);
        setResultViewFile(targetFile);
        setProgressText(`已在下方显示检查结果：${getBaseName(targetFile)}`);
        return;
      }
    }
    if (focusResultOverview()) {
      return;
    }
    if (historyResults.length > 0) {
      const historyId = selectedHistoryId || historyResults[0]?.id;
      if (historyId) {
        setSelectedHistoryId(historyId);
        setProgressText(`已在下方显示历史结果：${historyResults.find((x) => x.id === historyId)?.displayName || historyId}`);
        return;
      }
    }
    try {
      await invoke("open_result_summary");
    } catch (e) {
      setError(String(e));
    }
  }

  const totalIssues = useMemo(() => batchResults.reduce((sum, item) => sum + item.issues.length, 0), [batchResults]);
  const selectedResult = useMemo(() => {
    const target = resultViewFile || selectedListFile;
    if (target) {
      const matched = batchResults.find((item) => item.filePath === target);
      if (matched) return matched;
    }
    return result ?? batchResults[0] ?? null;
  }, [batchResults, result, resultViewFile, selectedListFile]);
  const selectedHistory = useMemo(
    () => historyResults.find((item) => item.id === selectedHistoryId) || historyResults[0] || null,
    [historyResults, selectedHistoryId],
  );
  const selectedOverview = useMemo(
    () => resultOverview?.items.find((item) => item.id === selectedOverviewId) || resultOverview?.items[0] || null,
    [resultOverview, selectedOverviewId],
  );
  useEffect(() => {
    const firstSection = selectedOverview?.sectionLinks?.[0]?.txtPath || "";
    void loadOverviewSection(firstSection);
  }, [selectedOverviewId, selectedOverview?.id]);
  const renderCheck = (section: string, key: string, label: string) => (
    <label className="check-item" key={`${section}-${key}`}>
      <input
        type="checkbox"
        checked={isTrue(readRule(section, key))}
        onChange={(e) => writeRule(section, key, boolToText(e.target.checked))}
      />
      <span>{label}</span>
    </label>
  );

  const renderInput = (section: string, key: string, label: string, options?: string[], size: "sm" | "md" | "lg" | "xl" = "md") => (
    <div className={`field size-${size}`} key={`${section}-${key}`}>
      <span>{label}</span>
      {options && options.length > 0 ? (
        <select value={readRule(section, key)} onChange={(e) => writeRule(section, key, e.target.value)}>
          <option value=""></option>
          {(options.includes(readRule(section, key))
            ? options
            : [readRule(section, key), ...options].filter((x) => x.trim().length > 0)
          ).map((op) => (
            <option key={op} value={op}>
              {op}
            </option>
          ))}
        </select>
      ) : (
        <input value={readRule(section, key)} onChange={(e) => writeRule(section, key, e.target.value)} />
      )}
    </div>
  );

  return (
    <main className="legacy-root">
      <div className="legacy-title-row">
        <h1 className="legacy-title">智脑审标</h1>
        <span className="legacy-version">V3.43</span>
      </div>

      <section className="legacy-top">
        <div className="auth-row">
          <div className="label-box">认证模式：</div>
          <select className="mini" value={authMode} onChange={(e) => setAuthMode((e.target.value as "device" | "udisk"))}>
            <option value="device">本机</option>
            <option value="udisk">U盾</option>
          </select>
          <div className="label-box">盘符：</div>
          <input
            className="mini"
            value={udiskDrive}
            onChange={(e) => setUdiskDrive(e.target.value)}
            disabled={authMode !== "udisk"}
          />
          <button className="btn-coral" onClick={applyAuthMode}>应用</button>

          <div className="label-box">设备码：</div>
          <div className="value-box">{license?.machineCode ?? "-"}</div>
          <div className="label-box">序列号：</div>
          <div className="value-box">{license?.serialNum ?? "-"}</div>
          <div className="label-box">激活码：</div>
          <input className="wide" value={regCodeInput} onChange={(e) => setRegCodeInput(e.target.value)} />
          <button className="btn-coral" onClick={activate}>激活</button>
          {devBuild ? <button className="btn-coral" onClick={devActivateYearly}>测试年度激活</button> : null}
          <button className="btn-coral" onClick={saveAllSettings}>保存</button>
          <button className="btn-coral" onClick={purchaseAndGenerate}>套餐购买</button>
        </div>

        <div className="auth-row mt8">
          <div className="label-box">设置敏感词：</div>
          <input
            className="fill"
            value={readRule("检查项", "敏感词")}
            onChange={(e) => writeRule("检查项", "敏感词", e.target.value)}
          />
          <span className="tip">（多个词用分号；隔开）</span>
        </div>
      </section>

      <section className="legacy-tabs">
        <div className="tab-head">
          {(Object.keys(TAB_LABELS) as TabKey[]).map((t) => (
            <button
              key={t}
              className={t === activeTab ? "tab-btn active" : "tab-btn"}
              onClick={() => setActiveTab(t)}
            >
              {TAB_LABELS[t]}
            </button>
          ))}
        </div>

        {activeTab === "common" ? (
          <div className="tab-body">
            <div className="check-grid">
              {renderCheck("检查项", "检查加粗下划线斜体颜色", "检查加粗下划线斜体颜色")}
              {renderCheck("检查项", "检查空白行", "检查空白行")}
              {renderCheck("检查项", "检查空格", "检查空格")}
              {renderCheck("检查项", "格式检查", "格式检查")}
              {renderCheck("检查项", "检查段前段后", "检查文本段落前后")}
              {renderCheck("检查项", "地名检查", "地名检查")}
              {renderCheck("检查项", "公司名检查", "公司名检查")}
              {renderCheck("检查项", "标点检查", "标点检查")}
              {renderCheck("检查项", "人名检查", "人名检查")}
              {renderCheck("检查项", "智能修正", "智能修正")}
              {renderCheck("检查项", "输出页码", "输出页码")}
              {renderCheck("检查项", "彩色图片检查", "彩色图片检查")}
            </div>
            <div className="field-grid cols3 mt8">
              {renderInput("检查项", "批注标记", "批注模式", ["", "不批注", "全标记", "可疑问题不批注"])}
              {renderInput("检查项", "非中文字体", "非中文字体")}
              {renderInput("检查项", "编辑软件", "编写软件", ["Office", "WPS"])}
            </div>
          </div>
        ) : null}

        {activeTab === "page" ? (
          <div className="tab-body legacy-compact">
            <div className="group-line">
              <div className="label-box">页边距要求（厘米）：</div>
              <div className="label-box">封面设置：</div>
            </div>
            <div className="field-grid cols6">
              {renderInput("页面", "上边距", "上边距")}
              {renderInput("页面", "下边距", "下边距")}
              {renderInput("页面", "左边距", "左边距")}
              {renderInput("页面", "右边距", "右边距")}
              {renderInput("页面", "页眉顶端边距", "页眉顶端边距")}
              {renderInput("页面", "页眉底端边距", "页眉底端边距")}
            </div>
            <div className="field-grid cols3 mt8">
              {renderInput("页面", "页面大小", "页面大小")}
              {renderInput("页面", "页面方向", "页面方向", ["纵向", "横向"])}
              {renderInput("检查项", "编辑软件", "编写软件", ["Office", "WPS"])}
            </div>
            <div className="field-grid cols5 mt8">
              {renderInput("页面", "封面标题", "封面标题")}
              {renderInput("页面", "封面字体", "封面字体")}
              {renderInput("页面", "封面字号", "封面字号", FONT_SIZES)}
              {renderInput("页面", "封面水平对齐方式", "封面水平对齐", ALIGNS)}
              {renderInput("页面", "封面垂直对齐方式", "封面垂直对齐", ["顶端对齐", "居中对齐", "基线对齐", "底端对齐", "自动"])}
            </div>
          </div>
        ) : null}

        {activeTab === "body" ? (
          <div className="tab-body legacy-compact">
            <div className="field-grid compact-inline">
              {renderInput("正文", "字体", "正文字体", undefined, "lg")}
              {renderInput("正文", "字号", "字号", FONT_SIZES, "sm")}
              {renderInput("正文", "行距", "行距", LINE_HEIGHT, "lg")}
              <div className="field with-suffix">
                <span>固定值</span>
                <input value={readRule("正文", "固定值")} onChange={(e) => writeRule("正文", "固定值", e.target.value)} />
                <em>磅</em>
              </div>
              {renderInput("正文", "首行缩进字符数", "首行缩进", undefined, "sm")}
              {renderInput("正文", "对齐方式", "对齐方式", ALIGNS, "xl")}
              {renderInput("正文", "字间距", "字间距", SPACING, "md")}
            </div>
            <div className="check-grid mt8">{renderCheck("正文", "断行检查", "断行检查")}</div>
          </div>
        ) : null}

        {activeTab === "table" ? (
          <div className="tab-body legacy-compact">
            <div className="field-grid compact-inline">
              {renderInput("表格", "字体", "表格字体", undefined, "lg")}
              {renderInput("表格", "字号", "字号", FONT_SIZES, "sm")}
              {renderInput("表格", "行距", "行距", LINE_HEIGHT, "lg")}
              <div className="field with-suffix">
                <span>固定值</span>
                <input value={readRule("表格", "固定值")} onChange={(e) => writeRule("表格", "固定值", e.target.value)} />
                <em>磅</em>
              </div>
              {renderInput("表格", "首行缩进字符数", "首行缩进", undefined, "sm")}
              {renderInput("表格", "对齐方式", "文字水平对齐", ALIGNS, "xl")}
              {renderInput("表格", "字间距", "字间距", SPACING, "md")}
            </div>
            <div className="field-grid compact-inline mt8">
              {renderInput("表格", "线条宽度", "线条宽度", undefined, "sm")}
              {renderInput("表格", "纵向对齐方式", "纵向对齐", ["顶端对齐", "居中", "底端对齐", "自动"], "lg")}
              {renderInput("表格", "表格水平对齐方式", "表格水平对齐", ["左对齐", "居中", "右对齐"], "lg")}
            </div>
          </div>
        ) : null}

        {activeTab === "title" ? (
          <div className="tab-body">
            <div className="field-grid cols2">
              {renderInput("标题", "缩进字符数", "缩进字符数")}
              {renderCheck("标题", "序号后空格", "标题编号后必须带空格")}
            </div>
            {[
              "一级标题",
              "二级标题",
              "三级标题",
              "四级标题",
              "五级标题",
              "六级标题",
              "七级标题",
            ].map((sec) => (
              <div className="title-row" key={sec}>
                <h4>{sec}</h4>
                <div className="field-grid cols4">
                  {renderInput(sec, "标题规则", "标题规则", titleRulePresets)}
                  {renderInput(sec, "字体", "字体")}
                  {renderInput(sec, "字号", "字号", FONT_SIZES)}
                  {renderCheck(sec, "加粗", "加粗")}
                </div>
              </div>
            ))}
          </div>
        ) : null}
      </section>

      <section className="legacy-actions">
        <button className="btn-coral" onClick={chooseFiles}>选择文档</button>
        <button className="btn-coral" onClick={runBatchParse} disabled={loading || selectedFiles.length === 0}>
          {loading ? "正在检查" : "开始检查"}
        </button>
        <div className="label-box">检查进度：</div>
        <div className="progress-box inline-progress">{error || progressText || license?.message || "就绪"}</div>
      </section>

      <section className="file-list">
        <div className="list-head">
          <div className="list-title">已选择的文档（双击文件名可看检查结果）</div>
          <button
            className="btn-coral"
            onClick={() => {
              setSelectedFiles([]);
              setSelectedListFile("");
            }}
            disabled={selectedFiles.length === 0}
          >
            清空
          </button>
          <button className="btn-coral" onClick={openResultFolder}>打开检查结果文件夹</button>
          <button className="btn-coral" onClick={openSelectedResult}>查看检查结果</button>
          <div className="usage-box">使用次数：{license?.useCount ?? 0}</div>
        </div>
        {selectedFiles.length === 0 ? <div className="empty">未选择文档</div> : null}
        <div className="list-table-wrap">
          <table className="legacy-table">
            <thead>
              <tr>
                <th>序号</th>
                <th>文件路径</th>
                <th>文件名</th>
              </tr>
            </thead>
            <tbody>
              {selectedFiles.map((p, idx) => {
                const selected = selectedListFile === p;
                return (
                  <tr
                    key={p}
                    className={selected ? "selected" : ""}
                    onClick={() => {
                      setSelectedListFile(p);
                      setResultViewFile(p);
                    }}
                    onDoubleClick={() => void openSingleResultByFile(p, false)}
                  >
                    <td>{idx + 1}</td>
                    <td>{getDirName(p)}</td>
                    <td>{getBaseName(p)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <section className="panel-lite compat-panel">
        <details>
          <summary>兼容工具</summary>
          <div className="compat-row">
            <div className="label-box">当前规则：</div>
            <div className="value-box path-box" title={rulesPath || "-"}>
              {rulesPath || "-"}
            </div>
            <button className="btn-coral" onClick={chooseRulesFile}>选择规则</button>
            <button className="btn-coral" onClick={() => openPath(rulesPath)} disabled={!rulesPath.trim()}>打开规则</button>
            <button className="btn-coral" onClick={loadFormatPreset}>读取预设</button>
            <button className="btn-coral" onClick={saveFormatPreset}>保存预设</button>
          </div>
          {generatedRegCode ? (
            <div className="compat-row mt8">
              <div className="label-box">最近生成激活码：</div>
              <div className="value-box path-box" title={generatedRegCode}>
                {generatedRegCode}
              </div>
            </div>
          ) : null}
          {plans.length > 0 ? (
            <div className="compat-row mt8">
              <div className="label-box">当前默认套餐：</div>
              <div className="value-box path-box" title={plans.find((p) => p.id === selectedPlan)?.name || plans[0]?.name || "-"}>
                {plans.find((p) => p.id === selectedPlan)?.name || plans[0]?.name || "-"}
              </div>
            </div>
          ) : null}
        </details>
      </section>

      {batchResults.length > 0 ? (
        <section className="panel-lite">
          <details>
            <summary>本次检查概要：文档 {batchResults.length} 个，问题 {totalIssues} 条</summary>
            <ul>
              {batchResults.map((r) => (
                <li key={r.filePath}>
                  <button className="mini-link" onClick={() => {
                    setSelectedListFile(r.filePath);
                    setResultViewFile(r.filePath);
                  }}>
                    {getBaseName(r.filePath)}
                  </button>
                  ：{r.issues.length} 条 {(r.reportDocxPath || r.reportPath) ? <button className="mini-btn" onClick={() => openGeneratedReport(r)}>打开报告</button> : null}
                </li>
              ))}
            </ul>
          </details>
        </section>
      ) : null}

      {resultOverview?.exists ? (
        <section className="panel-lite history-panel">
          <details open>
            <summary>内置结果总览（共 {resultOverview.items.length} 项）</summary>
            <div className="result-head mt8">
              <button className="btn-coral" onClick={refreshResultOverview}>刷新总览</button>
              {resultOverview.path ? (
                <button className="btn-coral" onClick={() => openPath(resultOverview.path)}>打开概要文件</button>
              ) : null}
              {selectedOverview?.reportDocxPath ? (
                <button className="btn-coral" onClick={() => openPath(selectedOverview.reportDocxPath)}>打开结果报告</button>
              ) : null}
              {selectedOverview?.sourceCopyPath ? (
                <button className="btn-coral" onClick={() => openPath(selectedOverview.sourceCopyPath)}>打开检查副本</button>
              ) : null}
            </div>
            <div className="history-layout">
              <div className="history-list">
                {resultOverview.items.map((item) => (
                  <button
                    key={item.id}
                    className={item.id === selectedOverview?.id ? "history-item active" : "history-item"}
                    onClick={() => setSelectedOverviewId(item.id)}
                  >
                    <span>{item.displayName}</span>
                    <em>{item.sourceName}</em>
                  </button>
                ))}
              </div>
              <div className="history-preview">
                {selectedOverview ? (
                  <>
                    <div className="history-meta">
                      <strong>{selectedOverview.displayName}</strong>
                      <span>{selectedOverview.sourceName}</span>
                    </div>
                    <div className="result-head">
                      {selectedOverview.sectionLinks.length > 0 ? selectedOverview.sectionLinks.map((link) => (
                        <button
                          key={link.txtPath}
                          className={selectedOverviewSectionPath === link.txtPath ? "mini-btn active" : "mini-btn"}
                          onClick={() => void loadOverviewSection(link.txtPath)}
                        >
                          {link.name}
                        </button>
                      )) : <div className="empty">该条结果没有可打开的分类结果</div>}
                    </div>
                    {selectedOverviewSectionPath ? (
                      <div className="result-head mt8">
                        <button className="btn-coral" onClick={() => openPath(selectedOverviewSectionPath)}>打开当前分类结果</button>
                      </div>
                    ) : null}
                    {overviewSectionText ? (
                      <pre className="result history-text mt8">{overviewSectionText}</pre>
                    ) : selectedOverview.sectionLinks.length > 0 ? (
                      <div className="empty mt8">当前分类结果暂无可预览文本</div>
                    ) : null}
                    {resultOverview.rawText ? (
                      <details className="mt8">
                        <summary>概要原文</summary>
                        <pre className="result history-text">{resultOverview.rawText}</pre>
                      </details>
                    ) : null}
                  </>
                ) : resultOverview.rawText ? (
                  <pre className="result history-text">{resultOverview.rawText}</pre>
                ) : (
                  <div className="empty">暂无可显示的结果概要</div>
                )}
              </div>
            </div>
          </details>
        </section>
      ) : null}

      {historyResults.length > 0 ? (
        <section className="panel-lite history-panel">
          <details>
            <summary>历史检查结果（共 {historyResults.length} 项）</summary>
            <div className="result-head mt8">
              <button className="btn-coral" onClick={refreshHistoryResults}>刷新历史结果</button>
              {selectedHistory?.txtPath ? (
                <button className="btn-coral" onClick={() => openPath(selectedHistory.txtPath)}>打开文本报告</button>
              ) : null}
              {selectedHistory?.docxPath ? (
                <button className="btn-coral" onClick={() => openPath(selectedHistory.docxPath)}>打开 DOCX 报告</button>
              ) : null}
            </div>
            <div className="history-layout">
              <div className="history-list">
                {historyResults.map((item) => (
                  <button
                    key={item.id}
                    className={item.id === selectedHistory?.id ? "history-item active" : "history-item"}
                    onClick={() => setSelectedHistoryId(item.id)}
                  >
                    <span>{item.displayName}</span>
                    <em>{item.updatedAt}</em>
                  </button>
                ))}
              </div>
              <div className="history-preview">
                {selectedHistory ? (
                  <>
                    <div className="history-meta">
                      <strong>{selectedHistory.displayName}</strong>
                      <span>{selectedHistory.updatedAt}</span>
                    </div>
                    {selectedHistory.reportText ? (
                      <pre className="result history-text">{selectedHistory.reportText}</pre>
                    ) : (
                      <div className="empty">该历史结果没有可直接预览的文本报告</div>
                    )}
                  </>
                ) : (
                  <div className="empty">暂无历史结果</div>
                )}
              </div>
            </div>
          </details>
        </section>
      ) : null}

      {selectedResult ? (
        <section className="panel-lite result-panel">
          <details>
            <summary>内置结果查看：{getBaseName(selectedResult.filePath)} / 问题 {selectedResult.issues.length} 条</summary>
            <div className="result-head mt8">
              {(selectedResult.reportDocxPath || selectedResult.reportPath) ? (
                <button className="btn-coral" onClick={() => openGeneratedReport(selectedResult)}>打开报告文件</button>
              ) : null}
            </div>
            {selectedResult.warnings.length > 0 ? (
              <div className="warning-box">
                {selectedResult.warnings.map((warning, idx) => (
                  <div key={`${selectedResult.filePath}-warning-${idx}`}>{warning}</div>
                ))}
              </div>
            ) : null}
            <div className="issue-table-wrap">
              <table className="legacy-table issue-table">
                <thead>
                  <tr>
                    <th>分类</th>
                    <th>规则</th>
                    <th>位置</th>
                    <th>说明</th>
                    <th>当前值</th>
                    <th>期望值</th>
                  </tr>
                </thead>
                <tbody>
                  {selectedResult.issues.length === 0 ? (
                    <tr>
                      <td colSpan={6}>未发现问题</td>
                    </tr>
                  ) : selectedResult.issues.map((issue, idx) => (
                    <tr key={`${selectedResult.filePath}-issue-${idx}`}>
                      <td>{issue.category}</td>
                      <td>{issue.rule}</td>
                      <td>{issue.location}</td>
                      <td title={issue.message}>{issue.message}</td>
                      <td title={issue.currentValue}>{issue.currentValue}</td>
                      <td title={issue.expectedValue}>{issue.expectedValue}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {selectedResult.reportText ? (
              <details open>
                <summary>文本报告</summary>
                <pre className="result">{selectedResult.reportText}</pre>
              </details>
            ) : null}
          </details>
        </section>
      ) : null}

      <section className="legacy-footer">
        <div className="label-box">版权所有：陕西淼华智脑科技有限公司</div>
        <div className="label-box">微信号：17104694950</div>
        <div className="label-box">电话：15871681541</div>
      </section>
    </main>
  );
}
