import { useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/tauri";
import { confirm, open } from "@tauri-apps/api/dialog";
import type {
  FormatPreset,
  LicenseStatus,
  ParseDocumentRequest,
  ParseDocumentResponse,
  PlanInfo,
  RulesConfig,
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

  const [activeTab, setActiveTab] = useState<TabKey>("common");
  const [ruleValues, setRuleValues] = useState<Record<string, Record<string, string>>>({});
  const [titleRulePresets, setTitleRulePresets] = useState<string[]>([]);

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
    setRuleValues((prev) => {
      const next = { ...prev };
      const write = (section: string, key: string, value: string) => {
        next[section] = {
          ...(next[section] || {}),
          [key]: value,
        };
      };
      write("页面", "上边距", preset.pageTop);
      write("页面", "下边距", preset.pageBottom);
      write("页面", "左边距", preset.pageLeft);
      write("页面", "右边距", preset.pageRight);
      write("正文", "字体", preset.bodyFont);
      write("正文", "字号", preset.bodySize);
      write("正文", "对齐方式", preset.bodyAlign);
      write("表格", "字体", preset.tableFont);
      write("表格", "字号", preset.tableSize);
      write("表格", "表格水平对齐方式", preset.tableHAlign);
      write("表格", "纵向对齐方式", preset.tableVAlign);
      write("一级标题", "字体", preset.title1Font);
      write("一级标题", "字号", preset.title1Size);
      write("二级标题", "字体", preset.title2Font);
      write("二级标题", "字号", preset.title2Size);
      return next;
    });
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
        pageTop: readRule("页面", "上边距"),
        pageBottom: readRule("页面", "下边距"),
        pageLeft: readRule("页面", "左边距"),
        pageRight: readRule("页面", "右边距"),
        bodyFont: readRule("正文", "字体"),
        bodySize: readRule("正文", "字号"),
        bodyAlign: readRule("正文", "对齐方式"),
        tableFont: readRule("表格", "字体"),
        tableSize: readRule("表格", "字号"),
        tableHAlign: readRule("表格", "表格水平对齐方式"),
        tableVAlign: readRule("表格", "纵向对齐方式"),
        title1Font: readRule("一级标题", "字体"),
        title1Size: readRule("一级标题", "字号"),
        title2Font: readRule("二级标题", "字体"),
        title2Size: readRule("二级标题", "字号"),
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

  useEffect(() => {
    (async () => {
      try {
        await Promise.all([refreshLicense(), refreshPlans(), loadRuleConfig(), loadTitleRulePresets()]);
      } catch (e) {
        setError(String(e));
      }
    })();
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

  async function parseOne(path: string): Promise<ParseDocumentResponse> {
    const request: ParseDocumentRequest = {
      filePath: path,
      rulesPath: rulesPath.trim() || undefined,
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
        all.push(await parseOne(p));
      }
      setBatchResults(all);
      setResult(all[all.length - 1] ?? null);
      await refreshLicense();
      const total = all.reduce((sum, item) => sum + item.issues.length, 0);
      const doneMsg = `检查结束，共${all.length}个文档，总问题${total}条`;
      setProgressText(doneMsg);
      const shouldOpen = await confirm(`${doneMsg}，是否查看检查结果？`, {
        title: "消息提示",
      });
      if (shouldOpen) {
        await openSelectedResult();
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

  function openSingleResultByFile(filePath: string, silent = false) {
    const matched = batchResults.find((r) => r.filePath === filePath);
    if (!matched || (!matched.reportDocxPath && !matched.reportPath)) {
      if (!silent) {
        setError("还未生成结果");
      }
      return false;
    }
    void openGeneratedReport(matched);
    return true;
  }

  async function openSelectedResult() {
    setError("");
    if (selectedFiles.length === 0) {
      setError("请先选择文档");
      return;
    }
    try {
      await invoke("open_result_summary");
    } catch {
      // 兼容旧版：入口文件可能不存在，不阻塞逐文档打开结果。
    }
    let opened = 0;
    for (const filePath of selectedFiles) {
      if (openSingleResultByFile(filePath, true)) {
        opened += 1;
      }
    }
    if (opened === 0) {
      setError("还未生成结果");
    }
  }

  const totalIssues = useMemo(() => batchResults.reduce((sum, item) => sum + item.issues.length, 0), [batchResults]);

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
          <select className="mini" value={authMode} onChange={(e) => setAuthMode(e.target.value as "device" | "udisk")}>
            <option value="device">本机</option>
            <option value="udisk">U盾</option>
          </select>
          <input className="mini" value={udiskDrive} onChange={(e) => setUdiskDrive(e.target.value)} disabled={authMode !== "udisk"} />
          <button className="btn-coral" onClick={applyAuthMode}>应用模式</button>

          <div className="label-box">设备码：</div>
          <div className="value-box">{license?.machineCode ?? "-"}</div>
          <div className="label-box">序列号：</div>
          <div className="value-box">{license?.serialNum ?? "-"}</div>
          <div className="label-box">激活码：</div>
          <input className="wide" value={regCodeInput} onChange={(e) => setRegCodeInput(e.target.value)} />
          <button className="btn-coral" onClick={activate}>激活</button>
          <button className="btn-coral" onClick={saveRuleConfig}>保存</button>
          <button className="btn-coral" onClick={chooseRulesFile}>选择规则</button>
          <button className="btn-coral" onClick={() => openPath(rulesPath)} disabled={!rulesPath.trim()}>打开规则</button>
          <button className="btn-coral" onClick={loadFormatPreset}>读取预设</button>
          <button className="btn-coral" onClick={saveFormatPreset}>保存预设</button>
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

      <section className="legacy-actions mt8">
        <div className="label-box">套餐：</div>
        <select value={selectedPlan} onChange={(e) => setSelectedPlan(e.target.value)}>
          {plans.map((p) => (
            <option key={p.id} value={p.id}>{p.name} / {p.validDays === 0 ? "永久" : `${p.validDays}天`} / {p.pageLimit}页 / {p.priceYuan}元</option>
          ))}
        </select>
        <button className="btn-coral" onClick={purchaseAndGenerate}>套餐购买</button>
        <input value={generatedRegCode} readOnly placeholder="购买后生成激活码" />
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
                    onClick={() => setSelectedListFile(p)}
                    onDoubleClick={() => openSingleResultByFile(p, false)}
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

      {batchResults.length > 0 ? (
        <section className="panel-lite">
          <div>文档数：{batchResults.length}，总问题数：{totalIssues}</div>
          <ul>
            {batchResults.map((r) => (
              <li key={r.filePath}>
                {getBaseName(r.filePath)}：{r.issues.length} 条 {(r.reportDocxPath || r.reportPath) ? <button className="mini-btn" onClick={() => openGeneratedReport(r)}>打开报告</button> : null}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {result?.reportText ? (
        <details>
          <summary>文本报告</summary>
          <pre className="result">{result.reportText}</pre>
        </details>
      ) : null}

      <section className="legacy-footer">
        <div className="label-box">版权所有：陕西淼华智脑科技有限公司</div>
        <div className="label-box">微信号：17104694950</div>
        <div className="label-box">电话：15871681541</div>
      </section>
    </main>
  );
}
