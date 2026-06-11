using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Spire.Doc;
using UglyToad.PdfPig;

var request = ParseRequest(args);
ReportProgress("正在初始化检查任务");

var response = new ParseDocumentResponse
{
    FilePath = request.FilePath ?? string.Empty,
    FileType = DetectFileType(request.FilePath),
    Parser = "dotnet-sidecar-v3-rules",
    PageCount = null,
    Warnings = new List<string>(),
    Metrics = null,
    Issues = new List<RuleIssue>()
};

if (string.IsNullOrWhiteSpace(request.FilePath) || !File.Exists(request.FilePath))
{
    response.Warnings.Add("文件不存在或路径为空");
    Console.WriteLine(JsonSerializer.Serialize(response));
    return;
}

ReportProgress("正在读取规则配置");
var ruleBook = LoadRuleBook(request, response);
response.OutputPageNumber = ruleBook.GetBool("检查项", "输出页码", true);
response.CommentMarker = ruleBook.Get("检查项", "批注标记");

if (response.FileType is "doc" or "docx")
{
    ReportProgress("正在分析 Word 文档");
    TryParseWord(request.FilePath!, response, ruleBook);
}
else if (response.FileType is "pdf")
{
    ReportProgress("正在分析 PDF 文档");
    TryParsePdf(request.FilePath!, response, ruleBook);
}
else
{
    response.Warnings.Add("当前版本仅支持 Word/PDF 基础规则还原。");
}

response.Issues = NormalizeIssues(DeduplicateIssues(response.Issues));
ReportProgress("正在整理检查结果");
FinalizeReportArtifacts(response, request.BatchSerial ?? 1, request.OverviewMode);
Console.WriteLine(JsonSerializer.Serialize(response));

static void ReportProgress(string message)
{
    Console.Error.WriteLine($"PROGRESS|{message}");
}

static RuleBook LoadRuleBook(ParseDocumentRequest request, ParseDocumentResponse response)
{
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(request.RulesPath))
    {
        candidates.Add(request.RulesPath!);
    }

    var appBase = AppContext.BaseDirectory;
    candidates.Add(Path.Combine(appBase, "set", "set.ini"));
    var cwd = Directory.GetCurrentDirectory();
    candidates.Add(Path.Combine(cwd, "rules", "set.ini"));
    candidates.Add(Path.Combine(cwd, "..", "rules", "set.ini"));

    var repoDefault = Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "..", "rules", "set.ini"));
    candidates.Add(repoDefault);

    foreach (var c in candidates.Distinct())
    {
        if (!File.Exists(c))
        {
            continue;
        }

        try
        {
            return RuleBook.Parse(c);
        }
        catch (Exception ex)
        {
            response.Warnings.Add($"规则文件读取失败({c}): {ex.Message}");
        }
    }

    response.Warnings.Add("未找到 set.ini，当前按空规则执行。");
    return new RuleBook();
}

static ParseDocumentRequest ParseRequest(string[] args)
{
    if (args.Length == 0)
    {
        return new ParseDocumentRequest();
    }

    var raw = string.Join(" ", args).Trim();
    if (string.IsNullOrWhiteSpace(raw))
    {
        return new ParseDocumentRequest();
    }

    if (raw.TrimStart().StartsWith("{"))
    {
        try
        {
            return JsonSerializer.Deserialize<ParseDocumentRequest>(raw) ?? new ParseDocumentRequest();
        }
        catch
        {
            return new ParseDocumentRequest { FilePath = raw.Trim().Trim('"') };
        }
    }

    return new ParseDocumentRequest { FilePath = raw.Trim().Trim('"') };
}

static void TryParseWord(string path, ParseDocumentResponse response, RuleBook rules)
{
    try
    {
        ReportProgress("正在读取 Word 页数与基础统计");
        using var doc = new Spire.Doc.Document();
        doc.LoadFromFile(path);

        var sectionCount = doc.Sections.Count;
        var paragraphCount = 0;
        var tableCount = 0;

        foreach (Section section in doc.Sections)
        {
            paragraphCount += section.Paragraphs.Count;
            tableCount += section.Tables.Count;
        }

        response.PageCount = doc.PageCount;
        response.Metrics = new DocumentMetrics
        {
            SectionCount = sectionCount,
            ParagraphCount = paragraphCount,
            TableCount = tableCount,
            MarginsCm = sectionCount > 0 ? ToMarginsFromSpire((Section)doc.Sections[0]) : null
        };

        // Spire 在当前环境仅用于统计，规则计算统一走 OpenXML（可复刻原逻辑并避免兼容问题）。
        if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            ReportProgress("正在分析正文、标题与表格");
            TryParseDocxWithOpenXml(path, response, rules);
        }
    }
    catch (Exception ex)
    {
        response.Warnings.Add($"Spire.Doc 解析失败: {ex.Message}");
        if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            TryParseDocxWithOpenXml(path, response, rules);
        }
    }
}

static void TryParsePdf(string path, ParseDocumentResponse response, RuleBook rules)
{
    try
    {
        ReportProgress("正在打开 PDF 文档");
        using var pdf = PdfDocument.Open(path);
        response.Parser = "dotnet-sidecar-v3-pdfpig-rules";
        response.PageCount = pdf.NumberOfPages;
        response.Metrics = new DocumentMetrics
        {
            SectionCount = pdf.NumberOfPages,
            ParagraphCount = 0,
            TableCount = 0,
            MarginsCm = null
        };

        var checkBlank = rules.GetBool("检查项", "检查空白行", false);
        var checkSpace = rules.GetBool("检查项", "检查空格", false);
        var formatCheck = rules.GetBool("检查项", "格式检查", false);
        var checkStyle = rules.GetBool("检查项", "检查加粗下划线斜体颜色", false);
        var checkPlace = rules.GetBool("检查项", "地名检查", false);
        var checkCompany = rules.GetBool("检查项", "公司名检查", false);
        var checkPerson = rules.GetBool("检查项", "人名检查", false);
        var checkPunctuation = rules.GetBool("检查项", "标点检查", false);
        var checkLineBreak = rules.GetBool("正文", "断行检查", false);
        var requireTitleSpace = rules.GetBool("标题", "序号后空格", false);
        var sensitiveWords = SplitTerms(
            GetFirstNonEmpty(
                rules.Get("检查项", "敏感词词典"),
                rules.Get("检查项", "敏感词列表"),
                rules.Get("检查项", "敏感词")
            )
        );
        var placeWords = SplitTerms(
            GetFirstNonEmpty(
                rules.Get("检查项", "地名词典"),
                rules.Get("检查项", "地名列表"),
                rules.Get("检查项", "地名"),
                rules.Get("检查项", "地名库")
            )
        );
        var companyWords = SplitTerms(
            GetFirstNonEmpty(
                rules.Get("检查项", "公司名词典"),
                rules.Get("检查项", "公司名列表"),
                rules.Get("检查项", "公司名"),
                rules.Get("检查项", "公司列表")
            )
        );
        var punctuationSymbols = BuildPunctuationSet(
            GetFirstNonEmpty(
                rules.Get("检查项", "标点词典"),
                rules.Get("检查项", "非中文符号"),
                rules.Get("检查项", "符号词典"),
                rules.Get("检查项", "标点符号")
            )
        );

        var totalWords = 0;
        for (var i = 1; i <= pdf.NumberOfPages; i++)
        {
            try
            {
                ReportProgress($"正在分析 PDF 第{i}/{pdf.NumberOfPages}页");
                var page = pdf.GetPage(i);
                var pageWords = page.GetWords().ToList();
                totalWords += pageWords.Count;
                var text = page.Text ?? string.Empty;
                var normalizedText = NormalizeSearchText(text);
                var location = $"P{i}";
                var lines = ExtractPageLines(text);
                var rawLines = ExtractPageRawLines(text);

                if (formatCheck)
                {
                    var (tableWordIndexes, tableBlockCount) = DetectPdfTableWords(pageWords);
                    CheckPdfPageRules(page, location, rules, response.Issues);
                    if (i == 1)
                    {
                        CheckPdfCoverRules(lines, location, rules, response.Issues);
                    }
                    CheckPdfTableLayoutRules(pageWords, tableWordIndexes, location, rules, response.Issues);
                    CheckPdfTitleAndLineBreakRules(lines, location, rules, requireTitleSpace, checkLineBreak, response.Issues);
                    CheckPdfTextFormatRules(pageWords, tableWordIndexes, location, rules, checkStyle, response.Issues);
                    response.Metrics.TableCount += tableBlockCount;
                }

                if (checkBlank && string.IsNullOrWhiteSpace(text))
                {
                    response.Issues.Add(new RuleIssue
                    {
                        Category = "检查项",
                        Rule = "检查空白行",
                        Message = "存在空白页。",
                        Location = location,
                        CurrentValue = "空白",
                        ExpectedValue = "非空白",
                        Severity = "warning",
                        Fixed = false
                    });
                    continue;
                }

                if (checkBlank || checkSpace)
                {
                    CheckPdfBlankAndSpaceRules(rawLines, location, checkBlank, checkSpace, response.Issues);
                }

                if (sensitiveWords.Count > 0)
                {
                    foreach (var word in sensitiveWords)
                    {
                        if (normalizedText.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            response.Issues.Add(new RuleIssue
                            {
                                Category = "检查项",
                                Rule = "敏感词",
                                Message = $"存在敏感词：{word}。",
                                Location = location,
                                CurrentValue = word,
                                ExpectedValue = "不出现敏感词",
                                Severity = "warning",
                                Fixed = false,
                                Snippet = Clip(text)
                            });
                        }
                    }
                }

                if (checkPlace && placeWords.Count > 0)
                {
                    foreach (var word in placeWords)
                    {
                        if (normalizedText.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            response.Issues.Add(new RuleIssue
                            {
                                Category = "检查项",
                                Rule = "地名检查",
                                Message = $"存在地名：{word}。",
                                Location = location,
                                CurrentValue = word,
                                ExpectedValue = "不出现地名",
                                Severity = "warning",
                                Fixed = false,
                                Snippet = Clip(text)
                            });
                        }
                    }
                }

                if (checkCompany && companyWords.Count > 0)
                {
                    foreach (var word in companyWords)
                    {
                        if (normalizedText.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            response.Issues.Add(new RuleIssue
                            {
                                Category = "检查项",
                                Rule = "公司名检查",
                                Message = $"存在公司名：{word}。",
                                Location = location,
                                CurrentValue = word,
                                ExpectedValue = "不出现公司名",
                                Severity = "warning",
                                Fixed = false,
                                Snippet = Clip(text)
                            });
                        }
                    }
                }

                if (checkPerson)
                {
                    var names = DetectChineseNames(normalizedText, 12);
                    foreach (var name in names)
                    {
                        response.Issues.Add(new RuleIssue
                        {
                            Category = "检查项",
                            Rule = "人名检查",
                            Message = $"存在疑似人名“{name}”。",
                            Location = location,
                            CurrentValue = name,
                            ExpectedValue = "不出现疑似人名",
                            Severity = "warning",
                            Fixed = false,
                            Snippet = Clip(text)
                        });
                    }
                }

                if (checkPunctuation)
                {
                    var hit = text.Where(ch => punctuationSymbols.Contains(ch)).Distinct().ToList();
                    if (hit.Count > 0)
                    {
                        var msg = string.Concat(hit.Select(ch => $"存在非中文符号“{ch}”。"));
                        response.Issues.Add(new RuleIssue
                        {
                            Category = "检查项",
                            Rule = "标点检查",
                            Message = msg,
                            Location = location,
                            CurrentValue = string.Concat(hit),
                            ExpectedValue = "不出现非中文符号",
                            Severity = "warning",
                            Fixed = false,
                            Snippet = Clip(text)
                        });
                    }
                }

                // rough paragraph estimate for progress display
                response.Metrics.ParagraphCount += text.Split('\n').Count(x => !string.IsNullOrWhiteSpace(x));
            }
            catch (Exception ex)
            {
                response.Warnings.Add($"PDF 第{i}页解析失败: {ex.Message}");
            }
        }

        if (totalWords == 0)
        {
            response.Warnings.Add("未提取到可解析文本，文档可能是扫描件或图片型PDF。");
            response.Issues.Add(new RuleIssue
            {
                Category = "其他",
                Rule = "可解析文本",
                Message = "未提取到可解析文本，疑似扫描件或图片型PDF。",
                Location = "P1",
                CurrentValue = "0",
                ExpectedValue = ">0",
                Severity = "warning",
                Fixed = false
            });
        }
    }
    catch (Exception ex)
    {
        response.Warnings.Add($"PDF 解析失败: {ex.Message}");
    }
}

static void CheckPdfPageRules(UglyToad.PdfPig.Content.Page page, string location, RuleBook rules, List<RuleIssue> issues)
{
    var words = page.GetWords().ToList();
    if (words.Count == 0)
    {
        return;
    }

    var pageWidthPt = page.Width;
    var pageHeightPt = page.Height;

    var topCm = PtToCm((float)(pageHeightPt - words.Max(w => w.BoundingBox.Top)));
    var bottomCm = PtToCm((float)words.Min(w => w.BoundingBox.Bottom));
    var leftCm = PtToCm((float)words.Min(w => w.BoundingBox.Left));
    var rightCm = PtToCm((float)(pageWidthPt - words.Max(w => w.BoundingBox.Right)));

    // 对齐原版 PDF 逻辑：上边距在原实现中未实际报错；下边距只检查“过小”；左右边距按偏差检查。
    var expectedBottomCm = rules.GetFloat("页面", "下边距");
    if (expectedBottomCm > 0 && bottomCm < expectedBottomCm)
    {
        issues.Add(new RuleIssue
        {
            Category = "页面",
            Rule = "下边距",
            Message = "下边距不正确。",
            Location = location,
            CurrentValue = bottomCm.ToString("0.###"),
            ExpectedValue = expectedBottomCm.ToString("0.###"),
            Severity = "warning",
            Fixed = false
        });
    }

    CheckPdfMargin("左边距", leftCm, 0.1, rules, location, issues);
    CheckPdfMargin("右边距", rightCm, 0.1, rules, location, issues);

    var pageSize = rules.Get("页面", "页面大小");
    if (string.Equals(pageSize, "A4", StringComparison.OrdinalIgnoreCase))
    {
        var a4w = 595.28;
        var a4h = 841.89;
        var isA4 = (Math.Abs(pageWidthPt - a4w) <= 6 && Math.Abs(pageHeightPt - a4h) <= 6)
                   || (Math.Abs(pageWidthPt - a4h) <= 6 && Math.Abs(pageHeightPt - a4w) <= 6);
        if (!isA4)
        {
            issues.Add(new RuleIssue
            {
                Category = "页面",
                Rule = "页面大小",
                Message = "页面大小不正确。",
                Location = location,
                CurrentValue = $"{pageWidthPt:0.#}x{pageHeightPt:0.#}pt",
                ExpectedValue = "A4",
                Severity = "warning",
                Fixed = false
            });
        }
    }

    var pageOrientation = rules.Get("页面", "页面方向");
    if (string.Equals(pageOrientation, "横向", StringComparison.OrdinalIgnoreCase) && pageWidthPt <= pageHeightPt)
    {
        issues.Add(new RuleIssue
        {
            Category = "页面",
            Rule = "页面方向",
            Message = "页面方向不正确。",
            Location = location,
            CurrentValue = "纵向",
            ExpectedValue = "横向",
            Severity = "warning",
            Fixed = false
        });
    }
    else if (string.Equals(pageOrientation, "纵向", StringComparison.OrdinalIgnoreCase) && pageWidthPt > pageHeightPt)
    {
        issues.Add(new RuleIssue
        {
            Category = "页面",
            Rule = "页面方向",
            Message = "页面方向不正确。",
            Location = location,
            CurrentValue = "横向",
            ExpectedValue = "纵向",
            Severity = "warning",
            Fixed = false
        });
    }
}

static void CheckPdfCoverRules(
    List<string> lines,
    string location,
    RuleBook rules,
    List<RuleIssue> issues)
{
    var expectedTitle = rules.Get("页面", "封面标题");
    if (string.IsNullOrWhiteSpace(expectedTitle))
    {
        return;
    }

    var exists = lines.Any(line => line.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase));
    if (exists)
    {
        return;
    }

    issues.Add(new RuleIssue
    {
        Category = "页面",
        Rule = "封面标题",
        Message = "封面标题不正确。",
        Location = location,
        CurrentValue = lines.Count > 0 ? Clip(lines[0]) : "未检测到文本",
        ExpectedValue = expectedTitle,
        Severity = "warning",
        Fixed = false
    });
}

static void CheckPdfMargin(string marginKey, double currentCm, double toleranceCm, RuleBook rules, string location, List<RuleIssue> issues)
{
    var expectedCm = rules.GetFloat("页面", marginKey);
    if (expectedCm <= 0)
    {
        return;
    }

    if (Math.Abs(currentCm - expectedCm) <= toleranceCm)
    {
        return;
    }

    issues.Add(new RuleIssue
    {
        Category = "页面",
        Rule = marginKey,
        Message = $"{marginKey}不正确。",
        Location = location,
        CurrentValue = currentCm.ToString("0.###"),
        ExpectedValue = expectedCm.ToString("0.###"),
        Severity = "warning",
        Fixed = false
    });
}

static List<string> SplitTerms(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return new List<string>();
    }

    return raw
        .Split(new[] { '\r', '\n', ',', '，', ';', '；', '|', '、', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string NormalizeSearchText(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return string.Empty;
    }

    return text
        .Replace("\n", string.Empty)
        .Replace("\t", string.Empty)
        .Replace("\r", string.Empty)
        .Replace("\a", string.Empty)
        .Trim();
}

static string GetFirstNonEmpty(params string[] values)
{
    foreach (var v in values)
    {
        if (!string.IsNullOrWhiteSpace(v))
        {
            return v;
        }
    }

    return string.Empty;
}

static HashSet<char> BuildPunctuationSet(string configured)
{
    var source = string.IsNullOrWhiteSpace(configured)
        ? @"!""#$%&'()*+,-./:;<=>?@[\]^_`{|}~"
        : configured;

    return source
        .Where(ch => !char.IsWhiteSpace(ch))
        .ToHashSet();
}

static List<string> DetectChineseNames(string text, int maxCount)
{
    if (string.IsNullOrWhiteSpace(text) || maxCount <= 0)
    {
        return new List<string>();
    }

    // 轻量回退实现：姓氏 + 1~2 个中文名字符；用于替代原版 NLP 人名识别能力。
    const string surnamePattern = "(欧阳|太史|端木|上官|司马|东方|独孤|南宫|万俟|闻人|夏侯|诸葛|尉迟|公羊|赫连|澹台|皇甫|宗政|濮阳|公冶|单于|长孙|慕容|司徒|司空|令狐|钟离|宇文|轩辕|百里|呼延|东郭|南门|羊舌|微生|梁丘|左丘|东门|西门|南荣|第五|艾|安|敖|巴|白|班|包|鲍|暴|毕|边|卞|蔡|曹|岑|柴|昌|常|晁|车|陈|成|程|池|迟|充|仇|储|楚|褚|淳|从|崔|戴|单|党|邓|狄|翟|刁|丁|董|窦|杜|段|樊|范|方|房|费|丰|冯|封|凤|伏|扶|符|傅|甘|高|郜|戈|宫|龚|巩|苟|辜|古|谷|顾|关|管|郭|韩|杭|郝|何|贺|赫|衡|洪|侯|胡|扈|花|华|滑|怀|桓|黄|霍|姬|嵇|纪|季|贾|简|江|姜|蒋|焦|金|靳|晋|荆|景|井|鞠|康|柯|孔|寇|匡|况|赖|蓝|郎|劳|雷|黎|李|厉|利|连|廉|练|梁|廖|林|蔺|凌|刘|柳|龙|隆|卢|鲁|陆|路|吕|栾|罗|骆|麻|马|满|毛|茅|梅|孟|苗|闵|明|莫|牟|穆|倪|聂|宁|欧|殴|潘|庞|裴|彭|皮|平|戚|齐|祁|钱|强|乔|秦|邱|丘|秋|曲|瞿|屈|任|饶|荣|容|阮|芮|桑|沙|山|单|商|上|邵|佘|申|沈|盛|施|石|史|时|寿|舒|束|双|司|宋|苏|宿|隋|孙|索|谭|汤|唐|陶|田|佟|童|涂|万|汪|王|危|韦|卫|魏|温|文|闻|翁|巫|邬|伍|武|席|夏|鲜|向|项|萧|谢|辛|邢|熊|徐|许|轩|宣|薛|严|闫|颜|晏|燕|杨|姚|叶|伊|易|殷|尹|应|雍|尤|游|余|俞|虞|元|袁|岳|云|曾|翟|詹|湛|张|章|赵|甄|郑|支|钟|仲|周|朱|诸|庄|卓|宗|邹|祖|左)";
    var regex = new System.Text.RegularExpressions.Regex(
        $"{surnamePattern}[\\u4e00-\\u9fa5]{{1,2}}",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    return regex.Matches(text)
        .Select(m => m.Value.Trim())
        .Where(v => v.Length is >= 2 and <= 4)
        .Distinct(StringComparer.Ordinal)
        .Take(maxCount)
        .ToList();
}

static List<string> ExtractPageLines(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return new List<string>();
    }

    return text
        .Replace('\r', '\n')
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .ToList();
}

static List<string> ExtractPageRawLines(string text)
{
    if (string.IsNullOrEmpty(text))
    {
        return new List<string>();
    }

    return text
        .Replace('\r', '\n')
        .Split('\n')
        .ToList();
}

static void CheckPdfBlankAndSpaceRules(
    List<string> rawLines,
    string location,
    bool checkBlank,
    bool checkSpace,
    List<RuleIssue> issues)
{
    if (rawLines.Count == 0)
    {
        return;
    }

    for (var idx = 0; idx < rawLines.Count; idx++)
    {
        var raw = rawLines[idx] ?? string.Empty;
        var trimmed = raw.Trim();
        if (checkBlank && idx > 0 && trimmed.Length > 0)
        {
            var prev = rawLines[idx - 1] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prev))
            {
                issues.Add(new RuleIssue
                {
                    Category = "检查项",
                    Rule = "检查空白行",
                    Message = "上方有空行。",
                    Location = $"{location}-L{idx + 1}",
                    CurrentValue = "空行间隔",
                    ExpectedValue = "无空行间隔",
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(raw)
                });
            }
        }

        if (!checkSpace || trimmed.Length == 0)
        {
            continue;
        }

        if (raw.Length > 0 && IsLeadingSpaceLike(raw[0]))
        {
            issues.Add(new RuleIssue
            {
                Category = "检查项",
                Rule = "检查空格",
                Message = "此处前面有空格。",
                Location = $"{location}-L{idx + 1}",
                CurrentValue = "前导空格",
                ExpectedValue = "无前导空格",
                Severity = "warning",
                Fixed = false,
                Snippet = Clip(raw)
            });
            continue;
        }

        if (ContainsInnerSpaceLike(raw))
        {
            issues.Add(new RuleIssue
            {
                Category = "检查项",
                Rule = "检查空格",
                Message = "此处有空格。",
                Location = $"{location}-L{idx + 1}",
                CurrentValue = "存在空格",
                ExpectedValue = "无空格",
                Severity = "warning",
                Fixed = false,
                Snippet = Clip(raw)
            });
        }
    }
}

static bool IsLeadingSpaceLike(char ch)
{
    return char.IsWhiteSpace(ch) || ch == '　';
}

static bool ContainsInnerSpaceLike(string text)
{
    if (string.IsNullOrEmpty(text))
    {
        return false;
    }

    // 复刻口径：正文中出现半角/全角空格均视为“此处有空格”。
    return text.Contains(' ') || text.Contains('　');
}

static void CheckPdfTitleAndLineBreakRules(
    List<string> lines,
    string location,
    RuleBook rules,
    bool requireTitleSpace,
    bool checkLineBreak,
    List<RuleIssue> issues)
{
    for (var idx = 0; idx < lines.Count; idx++)
    {
        var line = lines[idx];
        var level = DetectPdfHeadingLevel(line, rules, out var prefix);
        var headingCandidate = LooksLikeHeadingCandidate(line);

        if (headingCandidate && level == 0)
        {
            var guessedLevel = GuessHeadingLevelCandidate(line);
            var message = guessedLevel > 0
                ? $"此处为{guessedLevel}级标题，标题编号格式不正确。"
                : "标题编号格式不正确。";
            issues.Add(new RuleIssue
            {
                Category = "标题",
                Rule = "标题规则",
                Message = message,
                Location = $"{location}-L{idx + 1}",
                CurrentValue = Clip(line),
                ExpectedValue = "符合配置标题规则",
                Severity = "warning",
                Fixed = false,
                Snippet = Clip(line)
            });
        }

        if (requireTitleSpace && level > 0 && !string.IsNullOrEmpty(prefix) && line.Length > prefix.Length)
        {
            var nextChar = line[prefix.Length];
            if (nextChar != ' ' && nextChar != '　')
            {
                issues.Add(new RuleIssue
                {
                    Category = "标题",
                    Rule = "序号后空格",
                    Message = "此处为标题，缺乏空格。",
                    Location = $"{location}-L{idx + 1}",
                    CurrentValue = "无空格",
                    ExpectedValue = "有空格",
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(line)
                });
            }
        }

        if (!checkLineBreak || idx == lines.Count - 1)
        {
            continue;
        }

        var current = lines[idx].Trim();
        var next = lines[idx + 1].Trim();
        if (current.Length == 0 || next.Length == 0)
        {
            continue;
        }

        if (LooksLikeSentenceBreak(current) || DetectPdfHeadingLevel(next, rules, out _) > 0)
        {
            continue;
        }

        issues.Add(new RuleIssue
        {
            Category = "正文",
            Rule = "断行检查",
            Message = "疑似断行。",
            Location = $"{location}-L{idx + 1}",
            CurrentValue = current,
            ExpectedValue = "句末正常换行",
            Severity = "warning",
            Fixed = false,
            Snippet = Clip($"{current} | {next}")
        });
    }
}

static bool LooksLikeSentenceBreak(string line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        return true;
    }

    var endChars = "。！？；：.!?;:";
    var last = line.TrimEnd().LastOrDefault();
    return endChars.Contains(last);
}

static bool LooksLikeHeadingCandidate(string line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        return false;
    }

    var trimmed = line.TrimStart();
    return System.Text.RegularExpressions.Regex.IsMatch(
        trimmed,
        @"^([一二三四五六七八九十百千万]+[、.)）]|(\(?\d+\)?[、.)）])|(\d+(\.\d+){0,3}))"
    );
}

static int GuessHeadingLevelCandidate(string line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        return 0;
    }

    var trimmed = line.TrimStart();
    if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[一二三四五六七八九十百千万]+[、.)）]"))
    {
        return 1;
    }

    var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+(?:\.(\d+)){0,6}");
    if (!match.Success)
    {
        return 0;
    }

    var dotCount = trimmed.Take(match.Length).Count(ch => ch == '.');
    return Math.Clamp(dotCount + 1, 1, 7);
}

static int DetectPdfHeadingLevel(string line, RuleBook rules, out string prefix)
{
    prefix = string.Empty;
    if (string.IsNullOrWhiteSpace(line))
    {
        return 0;
    }

    for (var level = 1; level <= 7; level++)
    {
        var section = $"{ChineseLevelName(level)}标题";
        var pattern = rules.ResolveTitlePattern(rules.Get(section, "标题规则"));
        if (string.IsNullOrWhiteSpace(pattern))
        {
            continue;
        }

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, pattern);
            if (match.Success && match.Index == 0 && !string.IsNullOrWhiteSpace(match.Value))
            {
                prefix = match.Value;
                return level;
            }
        }
        catch
        {
            // ignore invalid regex from rule file
        }
    }

    return 0;
}

static void CheckPdfTextFormatRules(
    List<UglyToad.PdfPig.Content.Word> pageWords,
    HashSet<int> tableWordIndexes,
    string location,
    RuleBook rules,
    bool checkStyle,
    List<RuleIssue> issues)
{
    var expectedBodyFont = rules.Get("正文", "字体");
    var expectedBodyPt = FontSizeNameToPt(rules.Get("正文", "字号"));
    var expectedTableFont = rules.Get("表格", "字体");
    var expectedTablePt = FontSizeNameToPt(rules.Get("表格", "字号"));
    var expectedNonChineseFont = rules.Get("检查项", "非中文字体");

    for (var idx = 0; idx < pageWords.Count; idx++)
    {
        var word = pageWords[idx];
        var text = (word.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            continue;
        }

        var inTable = tableWordIndexes.Contains(idx);
        var expectedFont = expectedBodyFont;
        var expectedPt = expectedBodyPt;
        var expectedSizeLabel = rules.Get("正文", "字号");
        var expectBold = false;
        var headingLevel = 0;
        if (inTable)
        {
            expectedFont = expectedTableFont;
            expectedSizeLabel = rules.Get("表格", "字号");
            if (expectedTablePt > 0)
            {
                expectedPt = expectedTablePt;
            }
        }
        else
        {
            headingLevel = DetectPdfHeadingLevel(text, rules, out _);
            if (headingLevel > 0)
            {
                var section = $"{ChineseLevelName(headingLevel)}标题";
                expectedFont = rules.Get(section, "字体");
                expectedSizeLabel = rules.Get(section, "字号");
                var headingPt = FontSizeNameToPt(rules.Get(section, "字号"));
                if (headingPt > 0)
                {
                    expectedPt = headingPt;
                }
                expectBold = rules.GetBool(section, "加粗", false);
            }
        }

        var letters = word.Letters?.ToList() ?? new List<UglyToad.PdfPig.Content.Letter>();
        if (letters.Count == 0)
        {
            continue;
        }

        if (!string.IsNullOrWhiteSpace(expectedFont))
        {
            var fontMismatch = letters
                .Select(letter => NormalizePdfFontName(letter.FontName))
                .FirstOrDefault(current => !string.IsNullOrWhiteSpace(current) && !PdfFontMatches(current, expectedFont));
            if (!string.IsNullOrWhiteSpace(fontMismatch))
            {
                issues.Add(new RuleIssue
                {
                    Category = inTable ? "表格" : (headingLevel > 0 ? "标题" : "正文"),
                    Rule = "字体",
                    Message = $"字体不正确，当前是{fontMismatch}，应该是{expectedFont}。",
                    Location = location,
                    CurrentValue = fontMismatch,
                    ExpectedValue = expectedFont,
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(text)
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedNonChineseFont) && ContainsAsciiLetterOrDigit(text))
        {
            var nonChineseMismatch = letters
                .Where(letter => ContainsAsciiLetterOrDigit(letter.Value))
                .Select(letter => NormalizePdfFontName(letter.FontName))
                .FirstOrDefault(current => !string.IsNullOrWhiteSpace(current) && !PdfFontMatches(current, expectedNonChineseFont));
            if (!string.IsNullOrWhiteSpace(nonChineseMismatch))
            {
                issues.Add(new RuleIssue
                {
                    Category = inTable ? "表格" : (headingLevel > 0 ? "标题" : "正文"),
                    Rule = "非中文字体",
                    Message = $"非中文字体不正确，“{text}”当前是{nonChineseMismatch}，应该是{expectedNonChineseFont}。",
                    Location = location,
                    CurrentValue = nonChineseMismatch,
                    ExpectedValue = expectedNonChineseFont,
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(text)
                });
            }
        }

        if (expectedPt > 0)
        {
            var mismatchSize = letters
                .Select(letter => letter.PointSize)
                .FirstOrDefault(size => size > 0 && Math.Abs(size - expectedPt) > 0.8);
            if (mismatchSize > 0)
            {
                var expectedDisplay = !string.IsNullOrWhiteSpace(expectedSizeLabel)
                    ? expectedSizeLabel
                    : $"{expectedPt:0.##}pt";
                issues.Add(new RuleIssue
                {
                    Category = inTable ? "表格" : (headingLevel > 0 ? "标题" : "正文"),
                    Rule = "字号",
                    Message = $"字号不正确，当前是{mismatchSize:0.##}pt，应该是{expectedDisplay}。",
                    Location = location,
                    CurrentValue = mismatchSize.ToString("0.##"),
                    ExpectedValue = expectedDisplay,
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(text)
                });
            }
        }

        if (!checkStyle)
        {
            continue;
        }

        var boldMismatch = expectBold
            ? letters.FirstOrDefault(letter => !letter.Font.IsBold)
            : letters.FirstOrDefault(letter => letter.Font.IsBold);
        if (boldMismatch is not null)
        {
            var category = inTable ? "表格" : (headingLevel > 0 ? "标题" : "检查项");
            var expectBoldForCurrent = inTable ? false : expectBold;
            issues.Add(new RuleIssue
            {
                Category = category,
                Rule = "检查加粗下划线斜体颜色",
                Message = expectBoldForCurrent ? $"“{boldMismatch.Value}”未加粗。" : $"“{boldMismatch.Value}”被加粗。",
                Location = location,
                CurrentValue = expectBoldForCurrent ? "非加粗" : "加粗",
                ExpectedValue = expectBoldForCurrent ? "加粗" : "非加粗",
                Severity = "warning",
                Fixed = false,
                Snippet = Clip(text)
            });
        }

        var italicHit = letters.FirstOrDefault(letter => letter.Font.IsItalic);
        if (italicHit is not null)
        {
            issues.Add(new RuleIssue
            {
                Category = inTable ? "表格" : "检查项",
                Rule = "检查加粗下划线斜体颜色",
                Message = $"“{italicHit.Value}”是斜体。",
                Location = location,
                CurrentValue = "斜体",
                ExpectedValue = "非斜体",
                Severity = "warning",
                Fixed = false,
                Snippet = Clip(text)
            });
        }

        var colorHit = letters.FirstOrDefault(letter => !IsPdfLetterBlack(letter));
        if (colorHit is not null)
        {
            issues.Add(new RuleIssue
            {
                Category = inTable ? "表格" : "检查项",
                Rule = "检查加粗下划线斜体颜色",
                Message = $"“{colorHit.Value}”不是黑色。",
                Location = location,
                CurrentValue = "非黑色",
                ExpectedValue = "黑色",
                Severity = "warning",
                Fixed = false,
                Snippet = Clip(text)
            });
        }
    }
}

static (HashSet<int> tableWordIndexes, int tableBlockCount) DetectPdfTableWords(List<UglyToad.PdfPig.Content.Word> words)
{
    var tableWordIndexes = new HashSet<int>();
    if (words.Count == 0)
    {
        return (tableWordIndexes, 0);
    }

    var ordered = words
        .Select((w, idx) => (
            Index: idx,
            Y: (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0,
            Left: w.BoundingBox.Left,
            Right: w.BoundingBox.Right
        ))
        .OrderByDescending(x => x.Y)
        .ThenBy(x => x.Left)
        .ToList();

    var rows = new List<List<(int Index, double Y, double Left, double Right)>>();
    const double yTolerance = 2.5;
    foreach (var item in ordered)
    {
        var row = rows.FirstOrDefault(r => Math.Abs(r[0].Y - item.Y) <= yTolerance);
        if (row is null)
        {
            rows.Add(new List<(int Index, double Y, double Left, double Right)> { item });
        }
        else
        {
            row.Add(item);
        }
    }

    var tableRowFlags = new List<bool>();
    var rowWordIndexes = new List<List<int>>();
    foreach (var row in rows)
    {
        var sorted = row.OrderBy(x => x.Left).ToList();
        rowWordIndexes.Add(sorted.Select(x => x.Index).ToList());
        if (sorted.Count < 3)
        {
            tableRowFlags.Add(false);
            continue;
        }

        var breaks = 0;
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i].Left - sorted[i - 1].Right;
            if (gap > 14)
            {
                breaks++;
            }
        }

        var isTableRow = breaks >= 2;
        tableRowFlags.Add(isTableRow);
    }

    const int minRowsPerBlock = 2;
    var blockCount = 0;
    var runStart = -1;
    for (var i = 0; i <= tableRowFlags.Count; i++)
    {
        var isTableRow = i < tableRowFlags.Count && tableRowFlags[i];
        if (isTableRow && runStart < 0)
        {
            runStart = i;
        }
        else if (!isTableRow && runStart >= 0)
        {
            var runLength = i - runStart;
            if (runLength >= minRowsPerBlock)
            {
                blockCount++;
                for (var r = runStart; r < i; r++)
                {
                    foreach (var wordIndex in rowWordIndexes[r])
                    {
                        tableWordIndexes.Add(wordIndex);
                    }
                }
            }
            runStart = -1;
        }
    }

    return (tableWordIndexes, blockCount);
}

static void CheckPdfTableLayoutRules(
    List<UglyToad.PdfPig.Content.Word> pageWords,
    HashSet<int> tableWordIndexes,
    string location,
    RuleBook rules,
    List<RuleIssue> issues)
{
    if (tableWordIndexes.Count == 0)
    {
        return;
    }

    var expected = rules.Get("表格", "表格水平对齐方式");
    if (string.IsNullOrWhiteSpace(expected))
    {
        expected = rules.Get("表格", "对齐方式");
    }
    var expectedVerticalAlign = rules.Get("表格", "纵向对齐方式");
    if (string.IsNullOrWhiteSpace(expected))
    {
        expected = string.Empty;
    }

    var tableWords = tableWordIndexes
        .Where(i => i >= 0 && i < pageWords.Count)
        .Select(i => pageWords[i])
        .ToList();
    if (tableWords.Count == 0)
    {
        return;
    }

    var expectedLineWidth = rules.GetFloat("表格", "线条宽度");

    var tableLeft = tableWords.Min(w => w.BoundingBox.Left);
    var tableRight = tableWords.Max(w => w.BoundingBox.Right);
    var pageLeft = pageWords.Min(w => w.BoundingBox.Left);
    var pageRight = pageWords.Max(w => w.BoundingBox.Right);
    var pageWidth = Math.Max(1.0, pageRight - pageLeft);
    var leftGap = Math.Max(0, tableLeft - pageLeft);
    var rightGap = Math.Max(0, pageRight - tableRight);
    var alignTolerance = Math.Max(5.0, pageWidth * 0.03);

    string current;
    if (Math.Abs(leftGap - rightGap) <= alignTolerance)
    {
        current = "居中";
    }
    else if (leftGap < rightGap)
    {
        current = "左对齐";
    }
    else
    {
        current = "右对齐";
    }

    if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
    {
        issues.Add(new RuleIssue
        {
            Category = "表格",
            Rule = "表格水平对齐方式",
            Message = $"对齐方式不正确，当前是{current}，应该是{expected}。",
            Location = location,
            CurrentValue = current,
            ExpectedValue = expected,
            Severity = "warning",
            Fixed = false
        });
    }

    if (expectedLineWidth > 0)
    {
        CheckPdfTableLineWidthProxy(tableWords, expectedLineWidth, location, issues);
    }

    if (!string.IsNullOrWhiteSpace(expectedVerticalAlign))
    {
        CheckPdfTableVerticalAlignProxy(tableWords, expectedVerticalAlign, location, issues);
    }
}

static void CheckPdfTableLineWidthProxy(
    List<UglyToad.PdfPig.Content.Word> tableWords,
    double expectedLineWidth,
    string location,
    List<RuleIssue> issues)
{
    var separatorChars = new HashSet<string>(StringComparer.Ordinal)
    {
        "-", "_", "─", "━", "—", "＿", "═", "﹣"
    };
    var separatorLetters = tableWords
        .SelectMany(w => w.Letters)
        .Where(l => separatorChars.Contains(l.Value))
        .ToList();

    // 仅在可判定时比较，避免把普通文本表格误判为线宽问题。
    if (separatorLetters.Count() < 6)
    {
        return;
    }

    var heights = separatorLetters
        .Select(l => Math.Abs(l.GlyphRectangle.TopLeft.Y - l.GlyphRectangle.BottomLeft.Y))
        .Where(h => h > 0)
        .ToList();
    if (heights.Count() == 0)
    {
        return;
    }

    var currentProxy = heights.Average();
    if (Math.Abs(currentProxy - expectedLineWidth) <= 0.4)
    {
        return;
    }

    issues.Add(new RuleIssue
    {
        Category = "表格",
        Rule = "线条宽度",
        Message = $"线条宽度不正确，当前是{currentProxy:0.##}，应该是{expectedLineWidth:0.##}。",
        Location = location,
        CurrentValue = currentProxy.ToString("0.##"),
        ExpectedValue = expectedLineWidth.ToString("0.##"),
        Severity = "warning",
        Fixed = false
    });
}

static void CheckPdfTableVerticalAlignProxy(
    List<UglyToad.PdfPig.Content.Word> tableWords,
    string expectedVerticalAlign,
    string location,
    List<RuleIssue> issues)
{
    var expected = NormalizeVerticalAlignText(expectedVerticalAlign);
    if (string.IsNullOrWhiteSpace(expected))
    {
        return;
    }

    var rows = tableWords
        .Select(w => new
        {
            Word = w,
            CenterY = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0,
            Top = w.BoundingBox.Top,
            Bottom = w.BoundingBox.Bottom
        })
        .OrderByDescending(x => x.CenterY)
        .ToList();
    if (rows.Count < 8)
    {
        return;
    }

    const double yTolerance = 2.5;
    var rowClusters = new List<List<dynamic>>();
    foreach (var item in rows)
    {
        var row = rowClusters.FirstOrDefault(r => Math.Abs(((double)r[0].CenterY) - (double)item.CenterY) <= yTolerance);
        if (row is null)
        {
            rowClusters.Add(new List<dynamic> { item });
        }
        else
        {
            row.Add(item);
        }
    }

    if (rowClusters.Count < 3)
    {
        return;
    }

    var centers = rowClusters
        .Select(r => r.Average(x => (double)x.CenterY))
        .OrderByDescending(x => x)
        .ToList();
    if (centers.Count < 2)
    {
        return;
    }

    var centerGaps = new List<double>();
    for (var i = 1; i < centers.Count; i++)
    {
        var gap = centers[i - 1] - centers[i];
        if (gap > 0.6)
        {
            centerGaps.Add(gap);
        }
    }
    var medianGap = centerGaps.Count > 0 ? centerGaps.OrderBy(x => x).ElementAt(centerGaps.Count / 2) : 0.0;
    if (medianGap < 3.5)
    {
        return;
    }

    var votes = new List<string>();
    for (var i = 0; i < rowClusters.Count; i++)
    {
        var row = rowClusters[i];
        if (row.Count < 2)
        {
            continue;
        }

        var rowCenter = row.Average(x => (double)x.CenterY);
        var rowTextTop = row.Max(x => (double)x.Top);
        var rowTextBottom = row.Min(x => (double)x.Bottom);
        var textHeight = Math.Max(0.1, rowTextTop - rowTextBottom);

        var topBoundary = i == 0
            ? rowCenter + medianGap / 2.0
            : (rowCenter + centers[i - 1]) / 2.0;
        var bottomBoundary = i == centers.Count - 1
            ? rowCenter - medianGap / 2.0
            : (rowCenter + centers[i + 1]) / 2.0;

        var rowHeight = topBoundary - bottomBoundary;
        if (rowHeight <= 0 || rowHeight < textHeight * 1.35 || rowHeight < 6.0)
        {
            continue;
        }

        var ratio = (rowCenter - bottomBoundary) / rowHeight;
        if (ratio >= 0.72)
        {
            votes.Add("顶部对齐");
        }
        else if (ratio <= 0.28)
        {
            votes.Add("底部对齐");
        }
        else if (ratio >= 0.42 && ratio <= 0.58)
        {
            votes.Add("居中");
        }
    }

    if (votes.Count < 3)
    {
        return;
    }

    var winner = votes
        .GroupBy(v => v)
        .OrderByDescending(g => g.Count())
        .FirstOrDefault();
    if (winner is null)
    {
        return;
    }

    var confidence = winner.Count() / (double)votes.Count;
    if (confidence < 0.8)
    {
        return;
    }

    var current = winner.Key;
    if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    issues.Add(new RuleIssue
    {
        Category = "表格",
        Rule = "纵向对齐方式",
        Message = $"纵向对齐方式不正确，当前是{current}，应该是{expected}。",
        Location = location,
        CurrentValue = current,
        ExpectedValue = expected,
        Severity = "warning",
        Fixed = false
    });
}

static string NormalizeVerticalAlignText(string align)
{
    var text = (align ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(text))
    {
        return string.Empty;
    }

    if (text.Contains("顶"))
    {
        return "顶部对齐";
    }
    if (text.Contains("底"))
    {
        return "底部对齐";
    }
    if (text.Contains("中"))
    {
        return "居中";
    }

    return string.Empty;
}


static bool IsPdfLetterBlack(UglyToad.PdfPig.Content.Letter letter)
{
    try
    {
        var rgb = letter.Color.ToRGBValues();
        return rgb.Item1 <= 0.05 && rgb.Item2 <= 0.05 && rgb.Item3 <= 0.05;
    }
    catch
    {
        return true;
    }
}

static string NormalizePdfFontName(string? fontName)
{
    if (string.IsNullOrWhiteSpace(fontName))
    {
        return string.Empty;
    }

    var name = fontName.Trim();
    var plusIndex = name.IndexOf('+');
    if (plusIndex >= 0 && plusIndex + 1 < name.Length)
    {
        name = name[(plusIndex + 1)..];
    }

    name = name.ToLowerInvariant()
        .Replace("-", string.Empty)
        .Replace("_", string.Empty)
        .Replace(" ", string.Empty);

    return name;
}

static bool PdfFontMatches(string currentFont, string expectedFontCn)
{
    var cur = NormalizePdfFontName(currentFont);
    var aliases = GetPdfFontAliases(expectedFontCn);
    return aliases.Any(alias => cur.Contains(alias));
}

static bool ContainsAsciiLetterOrDigit(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return false;
    }

    return text.Any(ch => ch <= 127 && char.IsLetterOrDigit(ch));
}

static List<string> GetPdfFontAliases(string expectedFontCn)
{
    var key = (expectedFontCn ?? string.Empty).Trim();
    return key switch
    {
        "宋体" => new List<string> { "simsun", "song", "stsong" },
        "黑体" => new List<string> { "simhei", "heiti", "stheiti", "hei" },
        "楷体" => new List<string> { "kaiti", "stkaiti", "kai" },
        "仿宋" => new List<string> { "fangsong", "stfangsong", "fang" },
        "微软雅黑" => new List<string> { "yahei", "microsoftyahei" },
        _ => new List<string> { NormalizePdfFontName(key) }
    };
}

static string ChineseLevelName(int level)
{
    return level switch
    {
        1 => "一级",
        2 => "二级",
        3 => "三级",
        4 => "四级",
        5 => "五级",
        6 => "六级",
        7 => "七级",
        _ => string.Empty
    };
}

static PageMarginsCm ToMarginsFromSpire(Section section)
{
    var ps = section.PageSetup;
    return new PageMarginsCm
    {
        Top = PtToCm(ps.Margins.Top),
        Bottom = PtToCm(ps.Margins.Bottom),
        Left = PtToCm(ps.Margins.Left),
        Right = PtToCm(ps.Margins.Right),
        HeaderDistance = PtToCm(ps.HeaderDistance),
        FooterDistance = PtToCm(ps.FooterDistance)
    };
}

static void TryParseDocxWithOpenXml(string path, ParseDocumentResponse response, RuleBook rules)
{
    try
    {
        ReportProgress("正在加载 OpenXML 结构");
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            response.Warnings.Add("OpenXML 解析失败: 文档主体为空");
            return;
        }

        var paragraphs = body.Descendants<Paragraph>().ToList();
        var tables = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().ToList();
        var sectionProps = body.Descendants<SectionProperties>().ToList();

        response.Parser = "dotnet-sidecar-v3-openxml-rules";
        response.Metrics = new DocumentMetrics
        {
            SectionCount = Math.Max(1, sectionProps.Count),
            ParagraphCount = paragraphs.Count,
            TableCount = tables.Count,
            MarginsCm = GetMargins(sectionProps.FirstOrDefault())
        };

        ReportProgress("正在检查页面规则");
        CheckPageRules(sectionProps.FirstOrDefault(), rules, response.Issues);
        ReportProgress("正在检查正文格式");
        CheckParagraphRules(paragraphs, rules, response.Issues);
        ReportProgress("正在检查表格规则");
        CheckTableRules(tables, rules, response.Issues);
        ReportProgress("正在检查彩色图片");
        CheckColorImageRules(doc, rules, response.Issues);
        ReportProgress("正在检查通用项目");
        CheckGeneralChecks(paragraphs, rules, response.Issues);
    }
    catch (Exception ex)
    {
        response.Warnings.Add($"OpenXML 解析失败: {ex.Message}");
    }
}

static void CheckTableRules(List<DocumentFormat.OpenXml.Wordprocessing.Table> tables, RuleBook rules, List<RuleIssue> issues)
{
    var formatEnabled = rules.GetBool("检查项", "格式检查", false);
    if (!formatEnabled)
    {
        return;
    }

    var expectedTableAlign = rules.Get("表格", "表格水平对齐方式");
    if (string.IsNullOrWhiteSpace(expectedTableAlign))
    {
        expectedTableAlign = rules.Get("表格", "对齐方式");
    }

    var expectedCellVAlign = rules.Get("表格", "纵向对齐方式");
    var expectedLineWidth = rules.GetFloat("表格", "线条宽度");
    var checkBeforeAfter = rules.GetBool("检查项", "检查段前段后", false);
    var smartFix = rules.GetBool("检查项", "智能修正", false);

    for (var i = 0; i < tables.Count; i++)
    {
        var table = tables[i];
        var tPr = table.GetFirstChild<TableProperties>();
        var location = $"T{i + 1}";

        if (!string.IsNullOrWhiteSpace(expectedTableAlign))
        {
            var current = tPr?.TableJustification?.Val?.Value;
            var expected = MapTableAlign(expectedTableAlign);
            if (expected is not null && current != expected)
            {
                issues.Add(new RuleIssue
                {
                    Category = "表格",
                    Rule = "表格水平对齐方式",
                    Message = $"该表格对齐方式不正确，当前是{current?.ToString() ?? "未设置"}，应该是{expectedTableAlign}。",
                    Location = location,
                    CurrentValue = current?.ToString() ?? "未设置",
                    ExpectedValue = expectedTableAlign,
                    Severity = "warning",
                    Fixed = false
                });
            }
        }

        if (expectedLineWidth > 0)
        {
            var borders = tPr?.TableBorders;
            var sizes = new List<double>();
            if (borders?.TopBorder?.Size?.Value is { } top) sizes.Add(top / 8.0);
            if (borders?.BottomBorder?.Size?.Value is { } bottom) sizes.Add(bottom / 8.0);
            if (borders?.LeftBorder?.Size?.Value is { } left) sizes.Add(left / 8.0);
            if (borders?.RightBorder?.Size?.Value is { } right) sizes.Add(right / 8.0);
            if (borders?.InsideHorizontalBorder?.Size?.Value is { } insideH) sizes.Add(insideH / 8.0);
            if (borders?.InsideVerticalBorder?.Size?.Value is { } insideV) sizes.Add(insideV / 8.0);

            if (sizes.Count > 0)
            {
                var avg = sizes.Average();
                if (Math.Abs(avg - expectedLineWidth) > 0.1)
                {
                    issues.Add(new RuleIssue
                    {
                        Category = "表格",
                        Rule = "线条宽度",
                        Message = $"该表格线条宽度不正确，当前是{avg:0.##}，应该是{expectedLineWidth:0.##}。",
                        Location = location,
                        CurrentValue = avg.ToString("0.##"),
                        ExpectedValue = expectedLineWidth.ToString("0.##"),
                        Severity = "warning",
                        Fixed = false
                    });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedCellVAlign))
        {
            var expected = MapVerticalAlign(expectedCellVAlign);
            if (expected is null)
            {
                continue;
            }

            var cellIndex = 0;
            foreach (var cell in table.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableCell>())
            {
                cellIndex++;
                var current = cell.TableCellProperties?.TableCellVerticalAlignment?.Val?.Value;
                if (current != expected)
                {
                    issues.Add(new RuleIssue
                    {
                        Category = "表格",
                        Rule = "纵向对齐方式",
                        Message = $"单元格纵向对齐不正确，当前={current?.ToString() ?? "未设置"}，应为={expectedCellVAlign}",
                        Location = $"{location}-C{cellIndex}",
                        CurrentValue = current?.ToString() ?? "未设置",
                        ExpectedValue = expectedCellVAlign,
                        Severity = "warning",
                        Fixed = false
                    });
                    break;
                }
            }
        }

        if (checkBeforeAfter)
        {
            var paraIndex = 0;
            foreach (var p in table.Descendants<Paragraph>())
            {
                paraIndex++;
                var text = p.InnerText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }
                var pPr = p.ParagraphProperties;
                if (pPr is null || IsOpenXmlParagraphSpacingClean(pPr))
                {
                    continue;
                }
                issues.Add(new RuleIssue
                {
                    Category = "表格",
                    Rule = "检查段前段后",
                    Message = "表格段落段前段后不为0。",
                    Location = $"{location}-P{paraIndex}",
                    CurrentValue = OpenXmlParagraphSpacingSummary(pPr),
                    ExpectedValue = "左右缩进/段前后行/段前后间距均为0",
                    Severity = "warning",
                    Fixed = smartFix,
                    Snippet = Clip(text)
                });
            }
        }
    }
}

static TableRowAlignmentValues? MapTableAlign(string align)
{
    return align switch
    {
        "左对齐" => TableRowAlignmentValues.Left,
        "居中" => TableRowAlignmentValues.Center,
        "右对齐" => TableRowAlignmentValues.Right,
        _ => null
    };
}

static TableVerticalAlignmentValues? MapVerticalAlign(string align)
{
    return align switch
    {
        "顶部对齐" => TableVerticalAlignmentValues.Top,
        "居中" => TableVerticalAlignmentValues.Center,
        "底部对齐" => TableVerticalAlignmentValues.Bottom,
        _ => null
    };
}

static void CheckGeneralChecks(List<Paragraph> paragraphs, RuleBook rules, List<RuleIssue> issues)
{
    var checkBlankLine = rules.GetBool("检查项", "检查空白行", false);
    var checkSpace = rules.GetBool("检查项", "检查空格", false);
    var checkStyle = rules.GetBool("检查项", "检查加粗下划线斜体颜色", false);

    for (var i = 0; i < paragraphs.Count; i++)
    {
        var paragraph = paragraphs[i];
        var text = paragraph.InnerText ?? string.Empty;
        var location = $"P{i + 1}";

        if (checkBlankLine && string.IsNullOrWhiteSpace(text))
        {
            issues.Add(new RuleIssue
            {
                Category = "检查项",
                Rule = "检查空白行",
                Message = "存在空白行。",
                Location = location,
                CurrentValue = "空白",
                ExpectedValue = "非空白",
                Severity = "warning",
                Fixed = false
            });
            continue;
        }

        if (checkSpace && text.Contains(" "))
        {
            issues.Add(new RuleIssue
            {
                Category = "检查项",
                Rule = "检查空格",
                Message = "存在空格。",
                Location = location,
                CurrentValue = "包含空格",
                ExpectedValue = "不包含空格",
                Severity = "warning",
                Fixed = false,
                Snippet = Clip(text)
            });
        }

        if (!checkStyle)
        {
            continue;
        }

        foreach (var run in paragraph.Elements<Run>())
        {
            var runText = run.InnerText;
            if (string.IsNullOrWhiteSpace(runText))
            {
                continue;
            }

            var rPr = run.RunProperties;
            if (rPr is null)
            {
                continue;
            }

            if (rPr.Bold is not null && rPr.Bold.Val?.Value != false)
            {
                issues.Add(new RuleIssue
                {
                    Category = "检查项",
                    Rule = "检查加粗下划线斜体颜色",
                    Message = "文本存在加粗。",
                    Location = location,
                    CurrentValue = "加粗",
                    ExpectedValue = "不加粗",
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(runText)
                });
            }

            if (rPr.Italic is not null && rPr.Italic.Val?.Value != false)
            {
                issues.Add(new RuleIssue
                {
                    Category = "检查项",
                    Rule = "检查加粗下划线斜体颜色",
                    Message = "文本存在斜体。",
                    Location = location,
                    CurrentValue = "斜体",
                    ExpectedValue = "非斜体",
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(runText)
                });
            }

            if (rPr.Underline is not null && rPr.Underline.Val?.Value != UnderlineValues.None)
            {
                issues.Add(new RuleIssue
                {
                    Category = "检查项",
                    Rule = "检查加粗下划线斜体颜色",
                    Message = "文本存在下划线。",
                    Location = location,
                    CurrentValue = "下划线",
                    ExpectedValue = "无下划线",
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(runText)
                });
            }

            var colorVal = rPr.Color?.Val?.Value;
            if (!string.IsNullOrWhiteSpace(colorVal) && !string.Equals(colorVal, "000000", StringComparison.OrdinalIgnoreCase) && !string.Equals(colorVal, "auto", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new RuleIssue
                {
                    Category = "检查项",
                    Rule = "检查加粗下划线斜体颜色",
                    Message = $"文本颜色不是黑色，当前={colorVal}。",
                    Location = location,
                    CurrentValue = colorVal,
                    ExpectedValue = "000000",
                    Severity = "warning",
                    Fixed = false,
                    Snippet = Clip(runText)
                });
            }
        }
    }
}

static void CheckColorImageRules(WordprocessingDocument doc, RuleBook rules, List<RuleIssue> issues)
{
    if (!rules.GetBool("检查项", "彩色图片检查", false))
    {
        return;
    }
    var main = doc.MainDocumentPart;
    if (main is null || main.Document is null)
    {
        return;
    }

    var imageIndex = 0;
    var drawings = main.Document.Descendants<Drawing>().ToList();
    foreach (var drawing in drawings)
    {
        var blip = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault();
        var relId = blip?.Embed?.Value;
        if (string.IsNullOrWhiteSpace(relId))
        {
            continue;
        }

        ImagePart? imagePart = null;
        try
        {
            imagePart = main.GetPartById(relId) as ImagePart;
        }
        catch
        {
            imagePart = null;
        }
        if (imagePart is null)
        {
            continue;
        }

        byte[] bytes;
        using (var s = imagePart.GetStream())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            bytes = ms.ToArray();
        }

        if (!IsLikelyColorImage(bytes))
        {
            continue;
        }
        imageIndex++;
        issues.Add(new RuleIssue
        {
            Category = "检查项",
            Rule = "彩色图片检查",
            Message = "存在彩色图片。",
            Location = $"IMG{imageIndex}",
            CurrentValue = "彩色",
            ExpectedValue = "黑白",
            Severity = "warning",
            Fixed = false
        });
    }
}

static bool IsLikelyColorImage(byte[] bytes)
{
    if (bytes is null || bytes.Length < 12)
    {
        return false;
    }
    if (IsPng(bytes))
    {
        return IsPngColor(bytes);
    }
    if (IsJpeg(bytes))
    {
        return IsJpegColor(bytes);
    }
    if (IsGif(bytes))
    {
        return true;
    }
    if (IsWebP(bytes))
    {
        return true;
    }
    return false;
}

static bool IsPng(byte[] b) =>
    b.Length > 8 &&
    b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
    b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

static bool IsPngColor(byte[] b)
{
    if (b.Length < 26)
    {
        return false;
    }
    // PNG IHDR color type: 0灰度,2真彩,3索引彩色,4灰度+alpha,6真彩+alpha
    var colorType = b[25];
    return colorType is 2 or 3 or 6;
}

static bool IsJpeg(byte[] b) => b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8;

static bool IsJpegColor(byte[] b)
{
    var i = 2;
    while (i + 9 < b.Length)
    {
        if (b[i] != 0xFF)
        {
            i++;
            continue;
        }
        var marker = b[i + 1];
        // SOF0/SOF1/SOF2/SOF3/SOF5/SOF6/SOF7/SOF9/SOF10/SOF11/SOF13/SOF14/SOF15
        var isSof = marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
        if (isSof)
        {
            var components = b[i + 9];
            return components >= 3;
        }
        if (marker == 0xD9 || marker == 0xDA)
        {
            break;
        }
        if (i + 4 >= b.Length)
        {
            break;
        }
        var len = (b[i + 2] << 8) + b[i + 3];
        if (len < 2)
        {
            break;
        }
        i += 2 + len;
    }
    return false;
}

static bool IsGif(byte[] b) =>
    b.Length > 6 &&
    b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 &&
    b[3] == 0x38 && (b[4] == 0x39 || b[4] == 0x37) && b[5] == 0x61;

static bool IsWebP(byte[] b) =>
    b.Length > 12 &&
    b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
    b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50;

static void CheckPageRules(SectionProperties? section, RuleBook rules, List<RuleIssue> issues)
{
    if (section is null)
    {
        return;
    }

    var pgMar = section.GetFirstChild<PageMargin>();
    if (pgMar is null)
    {
        return;
    }

    CheckMargin("上边距", TwipToCm((int)(pgMar.Top?.Value ?? 0)));
    CheckMargin("下边距", TwipToCm((int)(pgMar.Bottom?.Value ?? 0)));
    CheckMargin("左边距", TwipToCm((int)(pgMar.Left?.Value ?? 0)));
    CheckMargin("右边距", TwipToCm((int)(pgMar.Right?.Value ?? 0)));
    CheckMargin("页眉顶端边距", TwipToCm((int)(pgMar.Header?.Value ?? 0)));
    CheckMargin("页眉底端边距", TwipToCm((int)(pgMar.Footer?.Value ?? 0)));

    void CheckMargin(string key, double current)
    {
        var expected = rules.GetFloat("页面", key);
        var smartFix = rules.GetBool("检查项", "智能修正", false);
        if (expected <= 0)
        {
            return;
        }

        if (Math.Abs(current - expected) > 0.01)
        {
            issues.Add(new RuleIssue
            {
                Category = "页面",
                Rule = key,
                Message = $"{key}不正确，当前={current:0.###}，应为={expected:0.###}",
                Location = "P1",
                CurrentValue = current.ToString("0.###"),
                ExpectedValue = expected.ToString("0.###"),
                Severity = "warning",
                Fixed = smartFix
            });
        }
    }
}

static void CheckParagraphRules(List<Paragraph> paragraphs, RuleBook rules, List<RuleIssue> issues)
{
    var formatEnabled = rules.GetBool("检查项", "格式检查", false);
    if (!formatEnabled)
    {
        return;
    }

    var expectedFontCn = rules.Get("正文", "字体");
    var expectedFontSizeName = rules.Get("正文", "字号");
    var expectedFontSizePt = FontSizeNameToPt(expectedFontSizeName);
    var expectedIndentChars = rules.GetFloat("正文", "首行缩进字符数");
    var expectedAlign = rules.Get("正文", "对齐方式");
    var checkBeforeAfter = rules.GetBool("检查项", "检查段前段后", false);
    var smartFix = rules.GetBool("检查项", "智能修正", false);

    for (var i = 0; i < paragraphs.Count; i++)
    {
        var paragraph = paragraphs[i];
        var text = paragraph.InnerText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            continue;
        }

        var pPr = paragraph.ParagraphProperties;
        if (pPr is not null)
        {
            if (checkBeforeAfter && !IsOpenXmlParagraphSpacingClean(pPr))
            {
                issues.Add(new RuleIssue
                {
                    Category = "检查项",
                    Rule = "检查段前段后",
                    Message = "段前段后不为0。",
                    Location = $"P{i + 1}",
                    CurrentValue = OpenXmlParagraphSpacingSummary(pPr),
                    ExpectedValue = "左右缩进/段前后行/段前后间距均为0",
                    Severity = "warning",
                    Fixed = smartFix,
                    Snippet = Clip(text)
                });
            }

            if (expectedIndentChars > 0)
            {
                var firstLineCharsRaw = pPr.Indentation?.FirstLineChars?.Value;
                var firstLineChars = firstLineCharsRaw.HasValue ? firstLineCharsRaw.Value / 100.0 : 0.0;
                if (Math.Abs(firstLineChars - expectedIndentChars) > 0.01)
                {
                    issues.Add(new RuleIssue
                    {
                        Category = "正文",
                        Rule = "首行缩进字符数",
                        Message = $"首行缩进字符数不正确，当前={firstLineChars:0.##}，应为={expectedIndentChars:0.##}",
                        Location = $"P{i + 1}",
                        CurrentValue = firstLineChars.ToString("0.##"),
                        ExpectedValue = expectedIndentChars.ToString("0.##"),
                        Severity = "warning",
                        Fixed = false,
                        Snippet = Clip(text)
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedAlign))
            {
                var currentJc = pPr.Justification?.Val?.Value;
                var expectedJc = MapAlign(expectedAlign);
                if (expectedJc is not null && currentJc != expectedJc)
                {
                    issues.Add(new RuleIssue
                    {
                        Category = "正文",
                        Rule = "对齐方式",
                        Message = $"对齐方式不正确，当前={currentJc?.ToString() ?? "未设置"}，应为={expectedAlign}",
                        Location = $"P{i + 1}",
                        CurrentValue = currentJc?.ToString() ?? "未设置",
                        ExpectedValue = expectedAlign,
                        Severity = "warning",
                        Fixed = false,
                        Snippet = Clip(text)
                    });
                }
            }
        }

        foreach (var run in paragraph.Elements<Run>())
        {
            var runText = run.InnerText;
            if (string.IsNullOrWhiteSpace(runText))
            {
                continue;
            }

            var rPr = run.RunProperties;
            var rFonts = rPr?.RunFonts;
            var sz = rPr?.FontSize;

            if (!string.IsNullOrWhiteSpace(expectedFontCn))
            {
                var currentFont = rFonts?.EastAsia?.Value ?? rFonts?.Ascii?.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(currentFont) && !string.Equals(currentFont, expectedFontCn, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new RuleIssue
                    {
                        Category = "正文",
                        Rule = "字体",
                        Message = $"字体不正确，当前={currentFont}，应为={expectedFontCn}",
                        Location = $"P{i + 1}",
                        CurrentValue = currentFont,
                        ExpectedValue = expectedFontCn,
                        Severity = "warning",
                        Fixed = false,
                        Snippet = Clip(runText)
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedFontSizeName) && expectedFontSizePt > 0)
            {
                var currentHalf = ParseDouble(sz?.Val?.Value);
                var currentPt = currentHalf > 0 ? currentHalf / 2.0 : 0;
                if (currentPt > 0 && Math.Abs(currentPt - expectedFontSizePt) > 0.1)
                {
                    issues.Add(new RuleIssue
                    {
                        Category = "正文",
                        Rule = "字号",
                        Message = $"字号不正确，当前={currentPt:0.##}pt，应为={expectedFontSizeName}({expectedFontSizePt:0.##}pt)",
                        Location = $"P{i + 1}",
                        CurrentValue = currentPt.ToString("0.##"),
                        ExpectedValue = expectedFontSizeName,
                        Severity = "warning",
                        Fixed = false,
                        Snippet = Clip(runText)
                    });
                }
            }
        }
    }
}

static bool IsOpenXmlParagraphSpacingClean(ParagraphProperties pPr)
{
    var ind = pPr.Indentation;
    var spacing = pPr.SpacingBetweenLines;

    static bool IsZeroString(StringValue? v)
    {
        return string.IsNullOrWhiteSpace(v?.Value) || v!.Value == "0";
    }
    static bool IsZeroInt(Int32Value? v)
    {
        return v is null || v.Value == 0;
    }

    var leftZero = ind is null || IsZeroString(ind.Left);
    var rightZero = ind is null || IsZeroString(ind.Right);
    var beforeLinesZero = spacing is null || IsZeroInt(spacing.BeforeLines);
    var afterLinesZero = spacing is null || IsZeroInt(spacing.AfterLines);
    var beforeZero = spacing is null || IsZeroString(spacing.Before);
    var afterZero = spacing is null || IsZeroString(spacing.After);

    return leftZero && rightZero && beforeLinesZero && afterLinesZero && beforeZero && afterZero;
}

static string OpenXmlParagraphSpacingSummary(ParagraphProperties pPr)
{
    var ind = pPr.Indentation;
    var spacing = pPr.SpacingBetweenLines;
    var beforeLines = spacing?.BeforeLines?.Value is int bl ? bl.ToString() : "0";
    var afterLines = spacing?.AfterLines?.Value is int al ? al.ToString() : "0";
    return $"left={ind?.Left?.Value ?? "0"},right={ind?.Right?.Value ?? "0"},beforeLines={beforeLines},afterLines={afterLines},before={spacing?.Before?.Value ?? "0"},after={spacing?.After?.Value ?? "0"}";
}

static string Clip(string text)
{
    var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
    return t.Length <= 28 ? t : t.Substring(0, 28) + "...";
}

static JustificationValues? MapAlign(string align)
{
    return align switch
    {
        "左对齐" => JustificationValues.Left,
        "居中" => JustificationValues.Center,
        "右对齐" => JustificationValues.Right,
        "两端对齐" => JustificationValues.Both,
        "分散对齐" => JustificationValues.Distribute,
        _ => null
    };
}

static double FontSizeNameToPt(string? name)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return 0;
    }

    var map = new Dictionary<string, double>
    {
        ["初号"] = 42,
        ["小初"] = 36,
        ["一号"] = 26,
        ["小一"] = 24,
        ["二号"] = 22,
        ["小二"] = 18,
        ["三号"] = 16,
        ["小三"] = 15,
        ["四号"] = 14,
        ["小四"] = 12,
        ["五号"] = 10.5,
        ["小五"] = 9,
        ["六号"] = 7.5,
        ["小六"] = 6.5,
        ["七号"] = 5.5,
        ["八号"] = 5
    };

    return map.TryGetValue(name.Trim(), out var pt) ? pt : ParseDouble(name);
}

static double ParseDouble(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return 0;
    }

    return double.TryParse(value, out var d) ? d : 0;
}

static PageMarginsCm? GetMargins(SectionProperties? section)
{
    var pgMar = section?.GetFirstChild<PageMargin>();
    if (pgMar is null)
    {
        return null;
    }

    return new PageMarginsCm
    {
        Top = TwipToCm((int)(pgMar.Top?.Value ?? 0)),
        Bottom = TwipToCm((int)(pgMar.Bottom?.Value ?? 0)),
        Left = TwipToCm((int)(pgMar.Left?.Value ?? 0)),
        Right = TwipToCm((int)(pgMar.Right?.Value ?? 0)),
        HeaderDistance = TwipToCm((int)(pgMar.Header?.Value ?? 0)),
        FooterDistance = TwipToCm((int)(pgMar.Footer?.Value ?? 0))
    };
}

static double PtToCm(float pt) => Math.Round(pt * 2.54 / 72.0, 3);
static double TwipToCm(int twip) => Math.Round(twip * 2.54 / 1440.0, 3);

static string DetectFileType(string? filePath)
{
    var ext = Path.GetExtension(filePath ?? string.Empty).ToLowerInvariant();
    return ext switch
    {
        ".doc" => "doc",
        ".docx" => "docx",
        ".pdf" => "pdf",
        _ => "unknown"
    };
}

static void FinalizeReportArtifacts(ParseDocumentResponse response, int batchSerial, string? overviewMode)
{
    var resultDir = ResolveResultDir(response.FilePath);
    Directory.CreateDirectory(resultDir);

    try
    {
        WriteLegacySourceCopy(response.FilePath, resultDir);
    }
    catch (Exception ex)
    {
        response.Warnings.Add($"结果副本写入失败: {ex.Message}");
    }

    ReportProgress("正在生成文本报告");
    response.ReportText = BuildReportText(response);

    try
    {
        var reportPath = WriteReportFile(response, resultDir);
        response.ReportPath = reportPath;
        WriteLegacySectionFiles(response, resultDir, batchSerial);
        var sourceName = Path.GetFileNameWithoutExtension(response.FilePath);
        WriteLegacyOverviewFile(resultDir, string.IsNullOrWhiteSpace(sourceName) ? "document" : sourceName, batchSerial, overviewMode);
    }
    catch (Exception ex)
    {
        response.Warnings.Add($"文本报告写入失败: {ex.Message}");
    }

    try
    {
        ReportProgress("正在生成 DOCX 报告");
        var reportDocxPath = WriteReportDocxFile(response, resultDir);
        response.ReportDocxPath = reportDocxPath;
    }
    catch (Exception ex)
    {
        response.Warnings.Add($"Docx报告写入失败: {ex.Message}");
    }
}

static string BuildReportText(ParseDocumentResponse response)
{
    var sb = new StringBuilder();
    sb.AppendLine("AiCheckBid 检查报告");
    sb.AppendLine($"文件: {response.FilePath}");
    sb.AppendLine($"类型: {response.FileType}");
    sb.AppendLine($"解析器: {response.Parser}");
    sb.AppendLine($"页数: {response.PageCount?.ToString() ?? "-"}");
    sb.AppendLine($"问题数: {response.Issues.Count}");
    sb.AppendLine(new string('-', 36));

    var sections = GetLegacySections();
    var grouped = GroupIssuesByLegacySection(response.Issues);

    foreach (var section in sections)
    {
        sb.AppendLine($"{section}:");
        if (!grouped.TryGetValue(section, out var list) || list.Count == 0)
        {
            sb.AppendLine("未发现问题。");
            sb.AppendLine();
            continue;
        }

        foreach (var issue in list)
        {
            var content = !string.IsNullOrWhiteSpace(issue.Snippet) ? issue.Snippet! : issue.CurrentValue;
            var location = response.OutputPageNumber ? issue.Location : string.Empty;
            var message = ApplyCommentMarker(issue.Message, response.CommentMarker);
            sb.AppendLine(FormatLegacyReportRow(location, message, content));
        }
        sb.AppendLine();
    }

    if (response.Warnings.Count > 0)
    {
        sb.AppendLine(new string('-', 36));
        sb.AppendLine("告警:");
        foreach (var warning in response.Warnings)
        {
            sb.AppendLine($"- {warning}");
        }
    }

    return sb.ToString();
}

static List<RuleIssue> DeduplicateIssues(List<RuleIssue> issues)
{
    return issues
        .GroupBy(x => $"{x.Category}|{x.Rule}|{x.Message}|{x.Location}|{x.CurrentValue}|{x.ExpectedValue}")
        .Select(g => g.First())
        .ToList();
}

static List<RuleIssue> NormalizeIssues(List<RuleIssue> issues)
{
    foreach (var issue in issues)
    {
        if (!string.IsNullOrWhiteSpace(issue.Snippet))
        {
            issue.Snippet = NormalizeSnippet(issue.Snippet!);
        }
    }

    return issues
        .OrderBy(i => ToLocationSortKey(i.Location))
        .ThenBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(i => i.Rule, StringComparer.OrdinalIgnoreCase)
        .ThenBy(i => i.Message, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string NormalizeSnippet(string snippet)
{
    var sb = new StringBuilder(snippet.Length);
    foreach (var ch in snippet)
    {
        if (char.IsControl(ch) && ch != '\t')
        {
            continue;
        }
        if (!IsReadableSnippetChar(ch))
        {
            continue;
        }
        sb.Append(ch);
    }
    var normalized = sb.ToString().Replace('\t', ' ').Trim();
    return normalized.Length <= 60 ? normalized : normalized.Substring(0, 60) + "...";
}

static bool IsReadableSnippetChar(char ch)
{
    if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
    {
        return true;
    }

    if (ch >= 0x4e00 && ch <= 0x9fff)
    {
        return true;
    }

    const string commonPunct = ".,;:!?()[]{}<>+-=_/*\\'\"，。；：！？（）《》、【】“”‘’·";
    return commonPunct.IndexOf(ch) >= 0;
}

static int ToLocationSortKey(string? location)
{
    if (string.IsNullOrWhiteSpace(location))
    {
        return int.MaxValue;
    }

    var loc = location.Trim().ToUpperInvariant();
    if (loc.StartsWith("P"))
    {
        var n = ParseLeadingNumber(loc, 1);
        return n >= 0 ? n : int.MaxValue - 3;
    }

    if (loc.StartsWith("T"))
    {
        var n = ParseLeadingNumber(loc, 1);
        return n >= 0 ? 100000 + n : int.MaxValue - 2;
    }

    return int.MaxValue - 1;
}

static int ParseLeadingNumber(string text, int start)
{
    if (start < 0 || start >= text.Length)
    {
        return -1;
    }

    var i = start;
    while (i < text.Length && char.IsDigit(text[i]))
    {
        i++;
    }

    if (i == start)
    {
        return -1;
    }

    var raw = text.Substring(start, i - start);
    return int.TryParse(raw, out var n) ? n : -1;
}

static string[] GetLegacySections()
{
    return new[]
    {
        "格式检查结果",
        "公司名检查结果",
        "地名检查结果",
        "人名检查结果",
        "敏感词检查结果",
        "标点符号检查结果",
        "其他检查结果"
    };
}

static Dictionary<string, List<RuleIssue>> GroupIssuesByLegacySection(List<RuleIssue> issues)
{
    return issues
        .GroupBy(MapIssueToLegacySection)
        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
}

static void WriteLegacySectionFiles(ParseDocumentResponse response, string resultDir, int batchSerial)
{
    var grouped = GroupIssuesByLegacySection(response.Issues);
    var sourceName = Path.GetFileNameWithoutExtension(response.FilePath);
    if (string.IsNullOrWhiteSpace(sourceName))
    {
        sourceName = "document";
    }

    foreach (var section in GetLegacySections())
    {
        var body = new StringBuilder();
        if (!grouped.TryGetValue(section, out var list) || list.Count == 0)
        {
            body.AppendLine("未发现问题。");
        }
        else
        {
            foreach (var issue in list)
            {
                var content = !string.IsNullOrWhiteSpace(issue.Snippet) ? issue.Snippet! : issue.CurrentValue;
                var location = response.OutputPageNumber ? NormalizeLegacyLocation(issue.Location) : string.Empty;
                var message = ApplyCommentMarker(issue.Message, response.CommentMarker);
                body.AppendLine(FormatLegacyReportRow(location, message, content));
            }
        }

        var sectionPath = Path.Combine(resultDir, $"{batchSerial}的{section}.txt");
        File.WriteAllText(sectionPath, body.ToString(), new UTF8Encoding(false));
    }
}

static string ApplyCommentMarker(string message, string? marker)
{
    _ = marker;
    // 原版“批注标记”用于批注/标注行为，不改写报告文本内容。
    return message;
}

static string FormatLegacyReportRow(string location, string message, string content)
{
    if (string.IsNullOrWhiteSpace(location))
    {
        return $"{message}\t未处理\t{content}\t";
    }

    return $"{location}\t{message}\t未处理\t{content}\t";
}

static string NormalizeLegacyLocation(string location)
{
    if (string.IsNullOrWhiteSpace(location))
    {
        return string.Empty;
    }

    var match = Regex.Match(location, @"P\d+");
    return match.Success ? match.Value : string.Empty;
}

static string MapIssueToLegacySection(RuleIssue issue)
{
    if (issue.Rule == "公司名检查")
    {
        return "公司名检查结果";
    }

    if (issue.Rule == "地名检查")
    {
        return "地名检查结果";
    }

    if (issue.Rule == "敏感词")
    {
        return "敏感词检查结果";
    }

    if (issue.Rule == "标点检查")
    {
        return "标点符号检查结果";
    }

    if (issue.Rule == "人名检查")
    {
        return "人名检查结果";
    }

    if (issue.Category is "页面" or "正文" or "标题" or "表格")
    {
        return "格式检查结果";
    }

    return "其他检查结果";
}

static void WriteLegacySourceCopy(string sourcePath, string resultDir)
{
    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
    {
        return;
    }

    var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
    if (string.IsNullOrWhiteSpace(sourceName))
    {
        sourceName = "document";
    }
    var targetPath = Path.Combine(resultDir, $"{sourceName}m{Path.GetExtension(sourcePath)}");
    if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
    {
        return;
    }
    File.Copy(sourcePath, targetPath, true);
}

static void WriteLegacyOverviewFile(string resultDir, string sourceName, int batchSerial, string? overviewMode)
{
    if (!Directory.Exists(resultDir))
    {
        return;
    }

    var sb = new StringBuilder();
    sb.Append(batchSerial);
    sb.Append('\t');
    sb.Append(sourceName);
    sb.Append("m\t");
    sb.Append(batchSerial);
    sb.Append("的格式检查结果\t");
    sb.Append(batchSerial);
    sb.Append("的公司名检查结果\t");
    sb.Append(batchSerial);
    sb.Append("的地名检查结果\t");
    sb.Append(batchSerial);
    sb.Append("的人名检查结果\t");
    sb.Append(batchSerial);
    sb.Append("的敏感词检查结果\t");
    sb.Append(batchSerial);
    sb.Append("的标点符号检查结果\t");
    sb.Append(batchSerial);
    sb.AppendLine("的其他检查结果");

    var overviewPath = Path.Combine(resultDir, "检查结果概要.txt");
    if (string.Equals(overviewMode, "append", StringComparison.OrdinalIgnoreCase) && File.Exists(overviewPath))
    {
        File.AppendAllText(overviewPath, sb.ToString(), new UTF8Encoding(false));
    }
    else
    {
        File.WriteAllText(overviewPath, sb.ToString(), new UTF8Encoding(false));
    }
}

static string WriteReportFile(ParseDocumentResponse response, string resultDir)
{
    var sourceName = Path.GetFileNameWithoutExtension(response.FilePath);
    var reportPath = Path.Combine(resultDir, $"检查结果-{sourceName}m.txt");
    File.WriteAllText(reportPath, response.ReportText ?? string.Empty, new UTF8Encoding(false));
    return reportPath;
}

static string LegacyCheckTypeLabel(string section)
{
    return section.EndsWith("结果", StringComparison.OrdinalIgnoreCase)
        ? section[..^2]
        : section;
}

static Paragraph CreateLegacyParagraph(string text, bool bold = false)
{
    var run = new Run();
    if (bold)
    {
        run.RunProperties = new RunProperties(new Bold());
    }
    run.Append(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
    return new Paragraph(run);
}

static DocumentFormat.OpenXml.Wordprocessing.TableCell CreateLegacyCell(string text, string width, bool bold = false)
{
    var cell = new DocumentFormat.OpenXml.Wordprocessing.TableCell(CreateLegacyParagraph(text, bold));
    cell.TableCellProperties = new TableCellProperties(
        new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = width },
        new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }
    );
    return cell;
}

static DocumentFormat.OpenXml.Wordprocessing.TableRow CreateLegacyRow(string col1, string col2, string col3, string col4, bool bold = false)
{
    var row = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
    row.Append(
        CreateLegacyCell(col1, "900", bold),
        CreateLegacyCell(col2, "2200", bold),
        CreateLegacyCell(col3, "900", bold),
        CreateLegacyCell(col4, "2000", bold)
    );
    return row;
}

static DocumentFormat.OpenXml.Wordprocessing.Table CreateLegacySectionTable(ParseDocumentResponse response, string sourceName, string section, List<RuleIssue> issues)
{
    var table = new DocumentFormat.OpenXml.Wordprocessing.Table();
    table.AppendChild(new TableProperties(
        new TableWidth { Type = TableWidthUnitValues.Pct, Width = "6000" },
        new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 8U },
            new BottomBorder { Val = BorderValues.Single, Size = 8U },
            new LeftBorder { Val = BorderValues.Single, Size = 8U },
            new RightBorder { Val = BorderValues.Single, Size = 8U },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 8U },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 8U }
        )
    ));

    table.Append(CreateLegacyRow("文件名", $"{sourceName}m", "检查类型", LegacyCheckTypeLabel(section), true));
    table.Append(CreateLegacyRow(response.OutputPageNumber ? "页码" : "位置", "问题详情", "处理结果", "原文内容", true));

    if (issues.Count == 0)
    {
        table.Append(CreateLegacyRow(string.Empty, "未发现问题。", string.Empty, string.Empty));
        return table;
    }

    foreach (var issue in issues)
    {
        var location = response.OutputPageNumber ? NormalizeLegacyLocation(issue.Location) : string.Empty;
        var message = ApplyCommentMarker(issue.Message, response.CommentMarker);
        var content = !string.IsNullOrWhiteSpace(issue.Snippet) ? issue.Snippet! : issue.CurrentValue;
        table.Append(CreateLegacyRow(location, message, "未处理", content));
    }
    return table;
}

static string WriteReportDocxFile(ParseDocumentResponse response, string resultDir)
{
    var sourceName = Path.GetFileNameWithoutExtension(response.FilePath);
    var reportDocxPath = Path.Combine(resultDir, $"检查结果-{sourceName}m.docx");

    using var doc = WordprocessingDocument.Create(reportDocxPath, WordprocessingDocumentType.Document);
    var main = doc.AddMainDocumentPart();
    main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
        new DocumentFormat.OpenXml.Wordprocessing.Body()
    );
    var body = main.Document.Body!;

    var grouped = GroupIssuesByLegacySection(response.Issues);
    foreach (var section in GetLegacySections())
    {
        body.AppendChild(CreateLegacyParagraph(section, true));
        var issues = grouped.TryGetValue(section, out var list) ? list : new List<RuleIssue>();
        body.AppendChild(CreateLegacySectionTable(response, sourceName, section, issues));
        body.AppendChild(CreateLegacyParagraph(string.Empty));
    }

    main.Document.Save();
    return reportDocxPath;
}

static string? ResolveReportRootDir(string? sourcePath)
{
    var sourceDir = Path.GetDirectoryName(sourcePath ?? string.Empty);
    if (string.IsNullOrWhiteSpace(sourceDir))
    {
        return sourceDir;
    }

    var dirName = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    if (string.Equals(dirName, "result", StringComparison.OrdinalIgnoreCase))
    {
        return Directory.GetParent(sourceDir)?.FullName ?? sourceDir;
    }

    return sourceDir;
}

static string ResolveResultDir(string? sourcePath)
{
    var configured = Environment.GetEnvironmentVariable("AICHECKBID_RESULT_DIR");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured.Trim();
    }

    var sourceDir = ResolveReportRootDir(sourcePath);
    if (string.IsNullOrWhiteSpace(sourceDir))
    {
        sourceDir = Directory.GetCurrentDirectory();
    }

    return Path.Combine(sourceDir, "result");
}

public sealed class ParseDocumentRequest
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("rulesPath")]
    public string? RulesPath { get; set; }

    [JsonPropertyName("batchSerial")]
    public int? BatchSerial { get; set; }

    [JsonPropertyName("batchTotal")]
    public int? BatchTotal { get; set; }

    [JsonPropertyName("overviewMode")]
    public string? OverviewMode { get; set; }
}

public sealed class ParseDocumentResponse
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("fileType")]
    public string FileType { get; set; } = "unknown";

    [JsonPropertyName("parser")]
    public string Parser { get; set; } = string.Empty;

    [JsonPropertyName("pageCount")]
    public int? PageCount { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("metrics")]
    public DocumentMetrics? Metrics { get; set; }

    [JsonPropertyName("issues")]
    public List<RuleIssue> Issues { get; set; } = new();

    [JsonPropertyName("reportText")]
    public string? ReportText { get; set; }

    [JsonPropertyName("reportPath")]
    public string? ReportPath { get; set; }

    [JsonPropertyName("reportDocxPath")]
    public string? ReportDocxPath { get; set; }

    [JsonPropertyName("outputPageNumber")]
    public bool OutputPageNumber { get; set; } = true;

    [JsonPropertyName("commentMarker")]
    public string? CommentMarker { get; set; }
}

public sealed class RuleIssue
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("rule")]
    public string Rule { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("currentValue")]
    public string CurrentValue { get; set; } = string.Empty;

    [JsonPropertyName("expectedValue")]
    public string ExpectedValue { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "warning";

    [JsonPropertyName("fixed")]
    public bool Fixed { get; set; }

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }
}

public sealed class DocumentMetrics
{
    [JsonPropertyName("sectionCount")]
    public int SectionCount { get; set; }

    [JsonPropertyName("paragraphCount")]
    public int ParagraphCount { get; set; }

    [JsonPropertyName("tableCount")]
    public int TableCount { get; set; }

    [JsonPropertyName("marginsCm")]
    public PageMarginsCm? MarginsCm { get; set; }
}

public sealed class PageMarginsCm
{
    [JsonPropertyName("top")]
    public double Top { get; set; }

    [JsonPropertyName("bottom")]
    public double Bottom { get; set; }

    [JsonPropertyName("left")]
    public double Left { get; set; }

    [JsonPropertyName("right")]
    public double Right { get; set; }

    [JsonPropertyName("headerDistance")]
    public double HeaderDistance { get; set; }

    [JsonPropertyName("footerDistance")]
    public double FooterDistance { get; set; }
}

public sealed class RuleBook
{
    private readonly Dictionary<string, Dictionary<string, string>> _data = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _titlePatterns = new(StringComparer.OrdinalIgnoreCase);

    public static RuleBook Parse(string path)
    {
        var content = ReadTextSmart(path);
        var rb = new RuleBook();
        string section = string.Empty;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!rb._data.ContainsKey(section))
                {
                    rb._data[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0 || string.IsNullOrWhiteSpace(section))
            {
                continue;
            }

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            rb._data[section][key] = value;
        }

        rb.LoadTitlePresets(path);
        return rb;
    }

    public string Get(string section, string key)
    {
        return _data.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var value) ? value : string.Empty;
    }

    public bool GetBool(string section, string key, bool defaultValue)
    {
        var value = Get(section, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var b))
        {
            return b;
        }

        value = value.Trim();
        if (value is "1" or "是" or "启用" or "True" or "TRUE")
        {
            return true;
        }

        if (value is "0" or "否" or "禁用" or "False" or "FALSE")
        {
            return false;
        }

        return defaultValue;
    }

    public float GetFloat(string section, string key)
    {
        var value = Get(section, key);
        return float.TryParse(value, out var f) ? f : 0f;
    }

    public string ResolveTitlePattern(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        if (value.Contains("^") || value.Contains("(") || value.Contains("[") || value.Contains(@"\d"))
        {
            return value;
        }

        if (_titlePatterns.TryGetValue(value, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        return value;
    }

    private void LoadTitlePresets(string rulesPath)
    {
        foreach (var path in GetTitlePresetCandidates(rulesPath))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var content = ReadTextSmart(path);
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';') || line.StartsWith('#'))
                {
                    continue;
                }

                var parts = line.Split("*****", StringSplitOptions.None);
                if (parts.Length < 2)
                {
                    continue;
                }

                var label = parts[0].Trim();
                var pattern = parts[1].Trim();
                if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(pattern))
                {
                    _titlePatterns[label] = pattern;
                }
            }

            if (_titlePatterns.Count > 0)
            {
                return;
            }
        }
    }

    private static IEnumerable<string> GetTitlePresetCandidates(string rulesPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(rulesPath))
        {
            var dir = Path.GetDirectoryName(rulesPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                candidates.Add(Path.Combine(dir, "title-presets.txt"));
            }
        }

        var appBase = AppContext.BaseDirectory;
        var cwd = Directory.GetCurrentDirectory();
        candidates.Add(Path.Combine(appBase, "rules", "title-presets.txt"));
        candidates.Add(Path.Combine(appBase, "resources", "rules", "title-presets.txt"));
        candidates.Add(Path.Combine(cwd, "rules", "title-presets.txt"));
        candidates.Add(Path.Combine(cwd, "title-presets.txt"));
        candidates.Add(Path.Combine(cwd, "..", "rules", "title-presets.txt"));

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ReadTextSmart(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        var utf8 = Encoding.UTF8.GetString(bytes);
        if (!utf8.Contains('�'))
        {
            return utf8;
        }

        return Encoding.GetEncoding("GB18030").GetString(bytes);
    }
}
