using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Spire.Doc;
using Spire.Doc.Documents;
using Spire.Doc.Fields;

namespace DocParserSidecarNet48
{
    internal class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static int Main(string[] args)
        {
            try
            {
                var filePath = args.Length > 0 ? args[0] : string.Empty;
                var rulesPath = args.Length > 1 ? args[1] : string.Empty;
                ReportProgress("正在初始化检查任务");

                var response = new ParseDocumentResponse
                {
                    filePath = filePath ?? string.Empty,
                    fileType = DetectFileType(filePath),
                    parser = "dotnet-sidecar-net48-spire",
                    warnings = new List<string>(),
                    issues = new List<RuleIssue>()
                };

                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    response.warnings.Add("文件不存在或路径为空");
                    Write(response);
                    return 0;
                }

                ReportProgress("正在读取规则配置");
                var rules = RuleBook.Load(rulesPath, response.warnings);
                response.outputPageNumber = rules.GetBool("检查项", "输出页码", true);
                response.commentMarker = rules.Get("检查项", "批注标记");
                response.smartFixEnabled = rules.GetBool("检查项", "智能修正", false);
                if (response.fileType == "doc" || response.fileType == "docx")
                {
                    ReportProgress("正在分析 Word 文档");
                    CheckWord(filePath, rules, response);
                    ReportProgress("正在生成文本报告");
                    response.reportText = BuildReportText(response);
                    response.reportPath = WriteReportFile(response);
                    ReportProgress("正在生成 DOCX 报告");
                    response.reportDocxPath = WriteReportDocxFile(response);
                }
                else
                {
                    response.warnings.Add("net48 sidecar 当前仅支持 doc/docx");
                }

                Write(response);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static void CheckWord(string path, RuleBook rules, ParseDocumentResponse response)
        {
            ReportProgress("正在加载 Word 文档");
            var doc = new Document();
            doc.LoadFromFile(path);

            response.pageCount = doc.PageCount;
            response.metrics = new DocumentMetrics();
            response.metrics.sectionCount = doc.Sections.Count;
            response.metrics.paragraphCount = 0;
            response.metrics.tableCount = 0;

            foreach (Section section in doc.Sections)
            {
                response.metrics.paragraphCount += section.Paragraphs.Count;
                response.metrics.tableCount += section.Tables.Count;
            }

            if (doc.Sections.Count > 0)
            {
                var sec = doc.Sections[0] as Section;
                var ps = sec.PageSetup;
                response.metrics.marginsCm = new PageMarginsCm
                {
                    top = PtToCm(ps.Margins.Top),
                    bottom = PtToCm(ps.Margins.Bottom),
                    left = PtToCm(ps.Margins.Left),
                    right = PtToCm(ps.Margins.Right),
                    headerDistance = PtToCm(ps.HeaderDistance),
                    footerDistance = PtToCm(ps.FooterDistance)
                };
            }

            ReportProgress("正在按原版口径检查 Word");
            CheckWordLegacyParity(doc, path, rules, response);
            SaveSmartFixedCopy(doc, path, response);
        }

        private static void CheckWordLegacyParity(Document doc, string path, RuleBook rules, ParseDocumentResponse response)
        {
            if (doc == null || rules == null || response == null)
            {
                return;
            }

            var text = ExtractLegacyWordText(doc);
            var sections = new LegacyWordSections();
            sections.format = string.Format("页数：{0}；字符数：{1}。", response.pageCount ?? doc.PageCount, text.Length);

            var placeWords = LoadLegacyWordList(
                rules,
                "词典1.doc",
                rules.Get("检查项", "地名词典"),
                rules.Get("检查项", "地名列表"),
                rules.Get("检查项", "地名"),
                rules.Get("检查项", "地名库")
            );
            sections.place = ScanLegacyWordList(
                text,
                placeWords,
                "存在地名：{0}。",
                rules.GetBool("检查项", "地名检查", true),
                response.issues,
                "地名检查"
            );

            var companyWords = LoadLegacyWordList(
                rules,
                "词典3.doc",
                rules.Get("检查项", "公司名词典"),
                rules.Get("检查项", "公司名列表"),
                rules.Get("检查项", "公司名"),
                rules.Get("检查项", "公司列表")
            );
            sections.company = ScanLegacyWordList(
                text,
                companyWords,
                "存在公司名：{0}。",
                rules.GetBool("检查项", "公司名检查", true),
                response.issues,
                "公司名检查"
            );

            sections.person = string.Empty;
            sections.sensitive = ScanLegacySensitiveWords(text, rules, response.issues);
            sections.punctuation = ScanLegacyPunctuation(
                text,
                LoadLegacyPunctuationSource(rules),
                rules.GetBool("检查项", "标点检查", true),
                response.issues
            );
            sections.other = string.Empty;
            response.legacySections = sections;
        }

        private static string ExtractLegacyWordText(Document doc)
        {
            try
            {
                return (doc.GetText() ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<string> LoadLegacyWordList(RuleBook rules, string fileName, params string[] fallbacks)
        {
            var words = LoadLegacySetDocWords(rules, fileName);
            if (words.Count > 0)
            {
                return words;
            }
            return SplitTerms(FirstNonEmpty(fallbacks));
        }

        private static List<string> LoadLegacySetDocWords(RuleBook rules, string fileName)
        {
            var result = new List<string>();
            foreach (var path in GetLegacySetDocCandidates(rules, fileName))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using (var document = new Document())
                    {
                        document.LoadFromFile(path, FileFormat.Auto, "shuhuan123456");
                        foreach (Section section in document.Sections)
                        {
                            foreach (Paragraph paragraph in section.Paragraphs)
                            {
                                var text = (paragraph.Text ?? string.Empty);
                                foreach (var part in text.Split(new[] { '*', '；', ';' }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var value = part.Trim();
                                    if (!string.IsNullOrWhiteSpace(value))
                                    {
                                        result.Add(value);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }
            return result;
        }

        private static string LoadLegacyPunctuationSource(RuleBook rules)
        {
            foreach (var path in GetLegacySetDocCandidates(rules, "词典4.doc"))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using (var document = new Document())
                    {
                        document.LoadFromFile(path, FileFormat.Auto, "shuhuan123456");
                        var text = (document.GetText() ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                }
                catch
                {
                }
            }

            return FirstNonEmpty(
                rules.Get("检查项", "标点词典"),
                rules.Get("检查项", "符号词典"),
                rules.Get("检查项", "标点符号"),
                rules.Get("检查项", "非中文符号")
            );
        }

        private static IEnumerable<string> GetLegacySetDocCandidates(RuleBook rules, string fileName)
        {
            var list = new List<string>();
            var rulesPath = rules == null ? string.Empty : rules.loadedPath;
            if (!string.IsNullOrWhiteSpace(rulesPath))
            {
                var rulesDir = Path.GetDirectoryName(rulesPath);
                if (!string.IsNullOrWhiteSpace(rulesDir))
                {
                    list.Add(Path.Combine(rulesDir, "set", fileName));
                    list.Add(Path.Combine(rulesDir, "..", "set", fileName));
                }
            }

            var cwd = Directory.GetCurrentDirectory();
            list.Add(Path.Combine(cwd, "set", fileName));
            list.Add(Path.Combine(cwd, "..", "set", fileName));

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            list.Add(Path.Combine(baseDir, "set", fileName));
            list.Add(Path.Combine(baseDir, "..", "set", fileName));
            list.Add(Path.Combine(baseDir, "resources", "set", fileName));

            return list
                .Select(path => Path.GetFullPath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string ScanLegacyWordList(
            string text,
            List<string> words,
            string pattern,
            bool enabled,
            List<RuleIssue> issues,
            string rule)
        {
            if (!enabled || string.IsNullOrEmpty(text) || words == null || words.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word) || !text.Contains(word))
                {
                    continue;
                }

                sb.AppendFormat(pattern, word);
                issues.Add(new RuleIssue
                {
                    category = "检查项",
                    rule = rule,
                    message = string.Format(pattern, word),
                    location = string.Empty,
                    currentValue = word,
                    expectedValue = string.Empty,
                    severity = "warning",
                    @fixed = false,
                    snippet = word
                });
            }

            return sb.ToString();
        }

        private static string ScanLegacySensitiveWords(string text, RuleBook rules, List<RuleIssue> issues)
        {
            if (string.IsNullOrEmpty(text) || rules == null)
            {
                return string.Empty;
            }

            var words = (rules.Get("检查项", "敏感词") ?? string.Empty).Replace('；', ';');
            if (string.IsNullOrWhiteSpace(words))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var token in words.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var word = token.Trim();
                if (string.IsNullOrWhiteSpace(word) || !text.Contains(word))
                {
                    continue;
                }

                var message = string.Format("存在敏感词：{0}。", word);
                sb.Append(message);
                issues.Add(new RuleIssue
                {
                    category = "检查项",
                    rule = "敏感词",
                    message = message,
                    location = string.Empty,
                    currentValue = word,
                    expectedValue = string.Empty,
                    severity = "warning",
                    @fixed = false,
                    snippet = word
                });
            }

            return sb.ToString();
        }

        private static string ScanLegacyPunctuation(string text, string punctuationSource, bool enabled, List<RuleIssue> issues)
        {
            if (!enabled || string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(punctuationSource))
            {
                return string.Empty;
            }

            var hits = new List<char>();
            var seen = new HashSet<char>();
            foreach (var ch in text)
            {
                if (punctuationSource.IndexOf(ch) >= 0 && seen.Add(ch))
                {
                    hits.Add(ch);
                }
            }

            if (hits.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var ch in hits)
            {
                var message = string.Format("存在非中文符号“{0}”。", ch);
                sb.Append(message);
                issues.Add(new RuleIssue
                {
                    category = "检查项",
                    rule = "标点检查",
                    message = message,
                    location = string.Empty,
                    currentValue = ch.ToString(),
                    expectedValue = string.Empty,
                    severity = "warning",
                    @fixed = false,
                    snippet = ch.ToString()
                });
            }

            return sb.ToString();
        }

        private static void ReportProgress(string message)
        {
            Console.Error.WriteLine("PROGRESS|" + message);
        }

        private static void CheckPage(Document doc, RuleBook rules, List<RuleIssue> issues)
        {
            if (doc.Sections.Count == 0)
            {
                return;
            }

            var sec = doc.Sections[0] as Section;
            var ps = sec.PageSetup;
            var top = PtToCm(ps.Margins.Top);
            var bottom = PtToCm(ps.Margins.Bottom);
            var left = PtToCm(ps.Margins.Left);
            var right = PtToCm(ps.Margins.Right);
            var header = PtToCm(ps.HeaderDistance);
            var footer = PtToCm(ps.FooterDistance);

            CheckMarginIssue(rules, issues, "上边距", top);
            CheckMarginIssue(rules, issues, "下边距", bottom);
            CheckMarginIssue(rules, issues, "左边距", left);
            CheckMarginIssue(rules, issues, "右边距", right);
            CheckMarginIssue(rules, issues, "页眉顶端边距", header);
            CheckMarginIssue(rules, issues, "页眉底端边距", footer);
        }

        private static void CheckMarginIssue(RuleBook rules, List<RuleIssue> issues, string key, double current)
        {
            var expected = rules.GetFloat("页面", key);
            if (expected <= 0)
            {
                return;
            }

            if (Math.Abs(current - expected) > 0.01)
            {
                issues.Add(new RuleIssue
                {
                    category = "页面",
                    rule = key,
                    message = key + "不正确，当前=" + current.ToString("0.###") + "，应为=" + expected.ToString("0.###"),
                    location = "P1",
                    currentValue = current.ToString("0.###"),
                    expectedValue = expected.ToString("0.###"),
                    severity = "warning"
                });
            }
        }

        private static void CheckParagraphs(Document doc, RuleBook rules, List<RuleIssue> issues)
        {
            if (!rules.GetBool("检查项", "格式检查", false))
            {
                return;
            }

            var expectedFont = rules.Get("正文", "字体");
            var expectedSizeName = rules.Get("正文", "字号");
            var expectedSize = FontSizeNameToPt(expectedSizeName);
            var expectedIndent = rules.GetFloat("正文", "首行缩进字符数");
            var expectedAlign = rules.Get("正文", "对齐方式");
            var expectedLineSpacingName = rules.Get("正文", "行距");
            var expectedLineSpacing = GetExpectedLineSpacing(expectedLineSpacingName);
            var expectedFixed = rules.GetFloat("正文", "固定值");
            var expectedCharSpacing = rules.Get("正文", "字间距");
            var checkSpace = rules.GetBool("检查项", "检查空格", false);
            var checkBlank = rules.GetBool("检查项", "检查空白行", false);
            var checkBeforeAfter = rules.GetBool("检查项", "检查段前段后", false);
            var checkTitleNumberSpace = rules.GetBool("标题", "序号后空格", false);
            var checkLineBreak = rules.GetBool("正文", "断行检查", false);
            var checkStyle = rules.GetBool("检查项", "检查加粗下划线斜体颜色", false);
            var expectedNonChineseFont = rules.Get("检查项", "非中文字体");

            var idx = 0;
            var prevNonEmptyText = string.Empty;
            var prevNonEmptyLoc = string.Empty;
            foreach (Section section in doc.Sections)
            {
                foreach (Paragraph p in section.Paragraphs)
                {
                    idx++;
                    var text = (p.Text ?? string.Empty).Trim();

                    if (checkBlank && text == string.Empty)
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "检查项",
                            rule = "检查空白行",
                            message = "存在空白行。",
                            location = "P" + idx,
                            currentValue = "空白",
                            expectedValue = "非空白",
                            severity = "warning"
                        });
                        continue;
                    }

                    var headingLevel = DetectHeadingLevel(p, rules);
                    var textForSpaceCheck = StripAllowedTitleGap(text, headingLevel);
                    if (checkSpace && textForSpaceCheck.Contains(" "))
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "检查项",
                            rule = "检查空格",
                            message = "存在空格。",
                            location = "P" + idx,
                            currentValue = "包含空格",
                            expectedValue = "不包含空格",
                            severity = "warning",
                            snippet = Clip(text)
                        });
                    }

                    if (text == string.Empty)
                    {
                        continue;
                    }

                    if (checkLineBreak
                        && !string.IsNullOrWhiteSpace(prevNonEmptyText)
                        && !LooksLikeSentenceEnd(prevNonEmptyText)
                        && headingLevel <= 0)
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "正文",
                            rule = "断行检查",
                            message = "疑似断行。",
                            location = prevNonEmptyLoc,
                            currentValue = Clip(prevNonEmptyText),
                            expectedValue = "句末正常换行",
                            severity = "warning",
                            snippet = Clip(prevNonEmptyText + " | " + text)
                        });
                    }

                    if (headingLevel <= 0 && checkBeforeAfter && !IsParagraphSpacingClean(p))
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "检查项",
                            rule = "检查段前段后",
                            message = "段前段后不为0。",
                            location = "P" + idx,
                            currentValue = ParagraphSpacingSummary(p),
                            expectedValue = "左右缩进/段前后行/段前后间距均为0",
                            severity = "warning",
                            snippet = Clip(text)
                        });
                    }

                    if (headingLevel <= 0 && expectedFixed > 0 && p.Format.LineSpacingRule == LineSpacingRule.Exactly && Math.Abs(p.Format.LineSpacing - expectedFixed) > 0.1)
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "正文",
                            rule = "固定值",
                            message = "固定行距不正确，当前=" + p.Format.LineSpacing.ToString("0.##") + "，应为=" + expectedFixed.ToString("0.##"),
                            location = "P" + idx,
                            currentValue = p.Format.LineSpacing.ToString("0.##"),
                            expectedValue = expectedFixed.ToString("0.##"),
                            severity = "warning",
                            snippet = Clip(text)
                        });
                    }
                    else if (headingLevel <= 0 && expectedLineSpacing > 0 && p.Format.LineSpacingRule != LineSpacingRule.Exactly && Math.Abs(p.Format.LineSpacing - expectedLineSpacing) > 0.5)
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "正文",
                            rule = "行距",
                            message = "行距不正确，当前=" + p.Format.LineSpacing.ToString("0.##") + "，应为=" + expectedLineSpacingName,
                            location = "P" + idx,
                            currentValue = p.Format.LineSpacing.ToString("0.##"),
                            expectedValue = expectedLineSpacingName,
                            severity = "warning",
                            snippet = Clip(text)
                        });
                    }

                    if (checkTitleNumberSpace && HasTitleNumberNoSpace(text))
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "标题",
                            rule = "序号后空格",
                            message = "标题序号后没有空格。",
                            location = "P" + idx,
                            currentValue = Clip(text),
                            expectedValue = "序号后带空格",
                            severity = "warning",
                            snippet = Clip(text)
                        });
                    }
                    else if (checkTitleNumberSpace && HasListNumberNoSpace(p))
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "标题",
                            rule = "序号后空格",
                            message = "标题序号后没有空格。",
                            location = "P" + idx,
                            currentValue = Clip(text),
                            expectedValue = "序号后带空格",
                            severity = "warning",
                            snippet = Clip(text)
                        });
                    }

                    if (headingLevel <= 0 && expectedIndent > 0 && Math.Abs(p.Format.FirstLineIndentChars - expectedIndent) > 0.01)
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "正文",
                            rule = "首行缩进字符数",
                            message = "首行缩进字符数不正确，当前=" + p.Format.FirstLineIndentChars.ToString("0.##") + "，应为=" + expectedIndent.ToString("0.##"),
                            location = "P" + idx,
                            currentValue = p.Format.FirstLineIndentChars.ToString("0.##"),
                            expectedValue = expectedIndent.ToString("0.##"),
                            severity = "warning",
                            snippet = Clip(text)
                        });
                    }

                    if (headingLevel <= 0 && !string.IsNullOrWhiteSpace(expectedAlign))
                    {
                        var currentAlign = p.Format.HorizontalAlignment;
                        var expected = MapAlign(expectedAlign);
                        if (expected >= 0 && (int)currentAlign != expected)
                        {
                            issues.Add(new RuleIssue
                            {
                                category = "正文",
                                rule = "对齐方式",
                                message = "对齐方式不正确，当前=" + currentAlign.ToString() + "，应为=" + expectedAlign,
                                location = "P" + idx,
                                currentValue = currentAlign.ToString(),
                                expectedValue = expectedAlign,
                                severity = "warning",
                                snippet = Clip(text)
                            });
                        }
                    }

                    if (headingLevel <= 0 && !string.IsNullOrWhiteSpace(expectedCharSpacing))
                    {
                        var currentCharSpacing = p.BreakCharacterFormat.CharacterSpacing;
                        if (!IsCharSpacingMatch(expectedCharSpacing, currentCharSpacing))
                        {
                            issues.Add(new RuleIssue
                            {
                                category = "正文",
                                rule = "字间距",
                                message = "字间距不正确，当前是" + CharSpacingLabel(currentCharSpacing) + "，应该是" + expectedCharSpacing + "。",
                                location = "P" + idx,
                                currentValue = currentCharSpacing.ToString("0.##"),
                                expectedValue = expectedCharSpacing,
                                severity = "warning",
                                snippet = Clip(text)
                            });
                        }
                    }

                    foreach (DocumentObject child in p.ChildObjects)
                    {
                        if (child.DocumentObjectType != DocumentObjectType.TextRange)
                        {
                            continue;
                        }

                        var tr = child as TextRange;
                        if (tr == null || string.IsNullOrWhiteSpace(tr.Text))
                        {
                            continue;
                        }

                        if (headingLevel <= 0 && !string.IsNullOrWhiteSpace(expectedNonChineseFont) && ContainsAsciiLetterOrDigit(tr.Text))
                        {
                            var curAsciiFont = GetEffectiveAsciiFont(tr);
                            if (!string.IsNullOrWhiteSpace(curAsciiFont) && !FontMatches(curAsciiFont, expectedNonChineseFont))
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = "正文",
                                    rule = "非中文字体",
                                    message = "非中文字体不正确，当前=" + curAsciiFont + "，应为=" + expectedNonChineseFont,
                                    location = "P" + idx,
                                    currentValue = curAsciiFont,
                                    expectedValue = expectedNonChineseFont,
                                    severity = "warning",
                                    snippet = Clip(tr.Text)
                                });
                            }
                        }

                        if (headingLevel <= 0 && !string.IsNullOrWhiteSpace(expectedFont))
                        {
                            var cur = GetEffectiveChineseFont(tr);
                            if (!string.IsNullOrWhiteSpace(cur) && !string.Equals(cur, expectedFont, StringComparison.OrdinalIgnoreCase))
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = "正文",
                                    rule = "字体",
                                    message = "字体不正确，当前=" + cur + "，应为=" + expectedFont,
                                    location = "P" + idx,
                                    currentValue = cur,
                                    expectedValue = expectedFont,
                                    severity = "warning",
                                    snippet = Clip(tr.Text)
                                });
                            }
                        }

                        if (headingLevel <= 0 && expectedSize > 0)
                        {
                            var curSize = GetEffectiveFontSize(tr);
                            if (Math.Abs(curSize - expectedSize) > 0.1)
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = "正文",
                                    rule = "字号",
                                    message = "字号不正确，当前=" + curSize.ToString("0.##") + "pt，应为=" + expectedSizeName + "(" + expectedSize.ToString("0.##") + "pt)",
                                    location = "P" + idx,
                                    currentValue = curSize.ToString("0.##"),
                                    expectedValue = expectedSizeName,
                                    severity = "warning",
                                    snippet = Clip(tr.Text)
                                });
                            }
                        }

                        if (headingLevel <= 0 && checkStyle)
                        {
                            if (tr.CharacterFormat.Bold)
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = "检查项",
                                    rule = "检查加粗下划线斜体颜色",
                                    message = "文本存在加粗。",
                                    location = "P" + idx,
                                    currentValue = "加粗",
                                    expectedValue = "不加粗",
                                    severity = "warning",
                                    snippet = Clip(tr.Text)
                                });
                            }
                            if (tr.CharacterFormat.Italic)
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = "检查项",
                                    rule = "检查加粗下划线斜体颜色",
                                    message = "文本存在斜体。",
                                    location = "P" + idx,
                                    currentValue = "斜体",
                                    expectedValue = "非斜体",
                                    severity = "warning",
                                    snippet = Clip(tr.Text)
                                });
                            }
                            if (tr.CharacterFormat.UnderlineStyle != UnderlineStyle.None)
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = "检查项",
                                    rule = "检查加粗下划线斜体颜色",
                                    message = "文本存在下划线。",
                                    location = "P" + idx,
                                    currentValue = "下划线",
                                    expectedValue = "无下划线",
                                    severity = "warning",
                                    snippet = Clip(tr.Text)
                                });
                            }
                            if (!IsBlack(tr.CharacterFormat.TextColor))
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = "检查项",
                                    rule = "检查加粗下划线斜体颜色",
                                    message = "文本颜色不是黑色。",
                                    location = "P" + idx,
                                    currentValue = "非黑色",
                                    expectedValue = "黑色",
                                    severity = "warning",
                                    snippet = Clip(tr.Text)
                                });
                            }
                        }
                    }

                    prevNonEmptyText = text;
                    prevNonEmptyLoc = "P" + idx;
                }
            }
        }

        private static void CheckHeadingRules(Document doc, RuleBook rules, List<RuleIssue> issues)
        {
            var sectionByLevel = new Dictionary<int, string>
            {
                { 1, "一级标题" },
                { 2, "二级标题" },
                { 3, "三级标题" },
                { 4, "四级标题" },
                { 5, "五级标题" },
                { 6, "六级标题" },
                { 7, "七级标题" }
            };

            var idx = 0;
            foreach (Section section in doc.Sections)
            {
                foreach (Paragraph p in section.Paragraphs)
                {
                    idx++;
                    var text = (p.Text ?? string.Empty).Trim();
                    if (text == string.Empty)
                    {
                        continue;
                    }

                    var level = DetectHeadingLevel(p, rules);
                    if (level <= 0 || !sectionByLevel.ContainsKey(level))
                    {
                        continue;
                    }

                    var titleSection = sectionByLevel[level];
                    var expectedFont = rules.Get(titleSection, "字体");
                    var expectedSizeName = rules.Get(titleSection, "字号");
                    var expectedSize = FontSizeNameToPt(expectedSizeName);
                    var expectedBold = rules.GetBool(titleSection, "加粗", false);
                    var titlePattern = ResolveTitlePattern(rules, rules.Get(titleSection, "标题规则"));

                    if (!string.IsNullOrWhiteSpace(titlePattern))
                    {
                        try
                        {
                            if (!Regex.IsMatch(text, titlePattern))
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = titleSection,
                                    rule = "标题规则",
                                    message = titleSection + "编号格式不符合规则。",
                                    location = "P" + idx,
                                    currentValue = Clip(text),
                                    expectedValue = titlePattern,
                                    severity = "warning",
                                    snippet = Clip(text)
                                });
                            }
                        }
                        catch
                        {
                            // keep backward compatibility if regex itself is invalid
                        }
                    }

                    foreach (DocumentObject child in p.ChildObjects)
                    {
                        if (child.DocumentObjectType != DocumentObjectType.TextRange)
                        {
                            continue;
                        }

                        var tr = child as TextRange;
                        if (tr == null || string.IsNullOrWhiteSpace(tr.Text))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(expectedFont))
                        {
                            var cur = GetEffectiveChineseFont(tr);
                            if (!string.IsNullOrWhiteSpace(cur) && !string.Equals(cur, expectedFont, StringComparison.OrdinalIgnoreCase))
                            {
                                issues.Add(new RuleIssue
                                {
                                    category = titleSection,
                                    rule = "字体",
                                    message = titleSection + "字体不正确，当前=" + cur + "，应为=" + expectedFont,
                                    location = "P" + idx,
                                    currentValue = cur,
                                    expectedValue = expectedFont,
                                    severity = "warning",
                                    snippet = Clip(text)
                                });
                            }
                        }

                        var currentTitleSize = GetEffectiveFontSize(tr);
                        if (expectedSize > 0 && Math.Abs(currentTitleSize - expectedSize) > 0.1)
                        {
                            issues.Add(new RuleIssue
                            {
                                category = titleSection,
                                rule = "字号",
                                message = titleSection + "字号不正确，当前=" + currentTitleSize.ToString("0.##") + "，应为=" + expectedSizeName,
                                location = "P" + idx,
                                currentValue = currentTitleSize.ToString("0.##"),
                                expectedValue = expectedSizeName,
                                severity = "warning",
                                snippet = Clip(text)
                            });
                        }

                        var isBold = tr.CharacterFormat.Bold;
                        if (isBold != expectedBold)
                        {
                            issues.Add(new RuleIssue
                            {
                                category = titleSection,
                                rule = "加粗",
                                message = titleSection + "加粗设置不正确，当前=" + (isBold ? "加粗" : "不加粗") + "，应为=" + (expectedBold ? "加粗" : "不加粗"),
                                location = "P" + idx,
                                currentValue = isBold ? "True" : "False",
                                expectedValue = expectedBold ? "True" : "False",
                                severity = "warning",
                                snippet = Clip(text)
                            });
                        }
                        break;
                    }
                }
            }
        }

        private static void CheckTables(Document doc, RuleBook rules, List<RuleIssue> issues)
        {
            if (!rules.GetBool("检查项", "格式检查", false))
            {
                return;
            }

            var expectedAlign = rules.Get("表格", "表格水平对齐方式");
            if (string.IsNullOrWhiteSpace(expectedAlign))
            {
                expectedAlign = rules.Get("表格", "对齐方式");
            }
            var expectedVAlign = rules.Get("表格", "纵向对齐方式");
            var expectedLineWidth = rules.GetFloat("表格", "线条宽度");
            var expectedFont = rules.Get("表格", "字体");
            var expectedSizeName = rules.Get("表格", "字号");
            var expectedSize = FontSizeNameToPt(expectedSizeName);
            var expectedTableIndent = rules.GetFloat("表格", "首行缩进字符数");
            var expectedTableAlignName = rules.Get("表格", "对齐方式");
            var expectedTableAlign = MapAlign(expectedTableAlignName);
            var expectedTableLineSpacingName = rules.Get("表格", "行距");
            var expectedTableLineSpacing = GetExpectedLineSpacing(expectedTableLineSpacingName);
            var expectedTableFixed = rules.GetFloat("表格", "固定值");
            var expectedTableCharSpacing = rules.Get("表格", "字间距");
            var checkBeforeAfter = rules.GetBool("检查项", "检查段前段后", false);
            var smartFix = rules.GetBool("检查项", "智能修正", false);

            var tIndex = 0;
            foreach (Section section in doc.Sections)
            {
                foreach (Table table in section.Tables)
                {
                    tIndex++;
                    var location = "T" + tIndex;

                    if (!string.IsNullOrWhiteSpace(expectedAlign))
                    {
                        var expected = MapAlign(expectedAlign);
                        if (expected >= 0 && (int)table.Format.HorizontalAlignment != expected)
                        {
                            if (smartFix)
                            {
                                table.Format.HorizontalAlignment = (RowAlignment)expected;
                            }
                            else
                            {
                            var currentAlignLabel = NormalizeHorizontalAlignLabel(table.Format.HorizontalAlignment.ToString());
                            issues.Add(new RuleIssue
                            {
                                category = "表格",
                                rule = "表格水平对齐方式",
                                message = "表格对齐方式不正确，当前=" + currentAlignLabel + "，应为=" + expectedAlign,
                                location = location,
                                currentValue = currentAlignLabel,
                                expectedValue = expectedAlign,
                                severity = "warning"
                            });
                            }
                        }
                    }

                    if (expectedLineWidth > 0)
                    {
                        var widths = new List<float>();
                        foreach (TableRow row in table.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                widths.Add(cell.CellFormat.Borders.Left.LineWidth);
                                widths.Add(cell.CellFormat.Borders.Right.LineWidth);
                                widths.Add(cell.CellFormat.Borders.Top.LineWidth);
                                widths.Add(cell.CellFormat.Borders.Bottom.LineWidth);
                            }
                        }
                        if (widths.Count > 0)
                        {
                            var avg = widths.Average();
                            if (Math.Abs(avg - expectedLineWidth) > 0.1)
                            {
                                if (smartFix)
                                {
                                    foreach (TableRow row in table.Rows)
                                    {
                                        foreach (TableCell cell in row.Cells)
                                        {
                                            cell.CellFormat.Borders.Left.LineWidth = expectedLineWidth;
                                            cell.CellFormat.Borders.Right.LineWidth = expectedLineWidth;
                                            cell.CellFormat.Borders.Top.LineWidth = expectedLineWidth;
                                            cell.CellFormat.Borders.Bottom.LineWidth = expectedLineWidth;
                                        }
                                    }
                                }
                                else
                                {
                                issues.Add(new RuleIssue
                                {
                                    category = "表格",
                                    rule = "线条宽度",
                                    message = "表格线条宽度不正确，当前≈" + avg.ToString("0.##") + "，应为=" + expectedLineWidth.ToString("0.##"),
                                    location = location,
                                    currentValue = avg.ToString("0.##"),
                                    expectedValue = expectedLineWidth.ToString("0.##"),
                                    severity = "warning"
                                });
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(expectedVAlign))
                    {
                        var expected = MapTextAlign(expectedVAlign);
                        var found = false;
                        foreach (TableRow row in table.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                if (expected >= 0 && (int)cell.CellFormat.VerticalAlignment != expected)
                                {
                                    found = true;
                                    break;
                                }
                            }
                            if (found) break;
                        }
                        if (found)
                        {
                            issues.Add(new RuleIssue
                            {
                                category = "表格",
                                rule = "纵向对齐方式",
                                message = "单元格纵向对齐不正确，应为=" + expectedVAlign,
                                location = location,
                                currentValue = "混合",
                                expectedValue = expectedVAlign,
                                severity = "warning"
                            });
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(expectedFont) || expectedSize > 0)
                    {
                        var foundMismatch = false;
                        foreach (TableRow row in table.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                foreach (Paragraph p in cell.Paragraphs)
                                {
                                    foreach (DocumentObject child in p.ChildObjects)
                                    {
                                        if (child.DocumentObjectType != DocumentObjectType.TextRange)
                                        {
                                            continue;
                                        }

                                        var tr = child as TextRange;
                                        if (tr == null || string.IsNullOrWhiteSpace(tr.Text))
                                        {
                                            continue;
                                        }

                                        if (!string.IsNullOrWhiteSpace(expectedFont))
                                        {
                                            var cur = GetEffectiveChineseFont(tr);
                                            if (!string.IsNullOrWhiteSpace(cur) && !string.Equals(cur, expectedFont, StringComparison.OrdinalIgnoreCase))
                                            {
                                                issues.Add(new RuleIssue
                                                {
                                                    category = "表格",
                                                    rule = "字体",
                                                    message = "表格字体不正确，当前=" + cur + "，应为=" + expectedFont,
                                                    location = location,
                                                    currentValue = cur,
                                                    expectedValue = expectedFont,
                                                    severity = "warning",
                                                    snippet = Clip(tr.Text)
                                                });
                                                foundMismatch = true;
                                                break;
                                            }
                                        }

                                        var currentTableSize = GetEffectiveFontSize(tr);
                                        if (expectedSize > 0 && Math.Abs(currentTableSize - expectedSize) > 0.1)
                                        {
                                            issues.Add(new RuleIssue
                                            {
                                                category = "表格",
                                                rule = "字号",
                                                message = "表格字号不正确，当前=" + currentTableSize.ToString("0.##") + "pt，应为=" + expectedSizeName,
                                                location = location,
                                                currentValue = currentTableSize.ToString("0.##"),
                                                expectedValue = expectedSizeName,
                                                severity = "warning",
                                                snippet = Clip(tr.Text)
                                            });
                                            foundMismatch = true;
                                            break;
                                        }
                                    }

                                    if (foundMismatch)
                                    {
                                        break;
                                    }
                                }

                                if (foundMismatch)
                                {
                                    break;
                                }
                            }

                            if (foundMismatch)
                            {
                                break;
                            }
                        }
                    }

                    foreach (TableRow row in table.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Paragraph p in cell.Paragraphs)
                            {
                                var text = (p.Text ?? string.Empty).Trim();
                                if (text.Length == 0)
                                {
                                    continue;
                                }

                                if (expectedTableFixed > 0 && p.Format.LineSpacingRule == LineSpacingRule.Exactly && Math.Abs(p.Format.LineSpacing - expectedTableFixed) > 0.1)
                                {
                                    issues.Add(new RuleIssue
                                    {
                                        category = "表格",
                                        rule = "固定值",
                                        message = "表格固定行距不正确，当前=" + p.Format.LineSpacing.ToString("0.##") + "，应为=" + expectedTableFixed.ToString("0.##"),
                                        location = location,
                                        currentValue = p.Format.LineSpacing.ToString("0.##"),
                                        expectedValue = expectedTableFixed.ToString("0.##"),
                                        severity = "warning",
                                        snippet = Clip(text)
                                    });
                                }
                                else if (expectedTableLineSpacing > 0 && p.Format.LineSpacingRule != LineSpacingRule.Exactly && Math.Abs(p.Format.LineSpacing - expectedTableLineSpacing) > 0.5)
                                {
                                    issues.Add(new RuleIssue
                                    {
                                        category = "表格",
                                        rule = "行距",
                                        message = "表格行距不正确，当前=" + p.Format.LineSpacing.ToString("0.##") + "，应为=" + expectedTableLineSpacingName,
                                        location = location,
                                        currentValue = p.Format.LineSpacing.ToString("0.##"),
                                        expectedValue = expectedTableLineSpacingName,
                                        severity = "warning",
                                        snippet = Clip(text)
                                    });
                                }

                                if (expectedTableIndent > 0 && Math.Abs(p.Format.FirstLineIndentChars - expectedTableIndent) > 0.01)
                                {
                                    issues.Add(new RuleIssue
                                    {
                                        category = "表格",
                                        rule = "首行缩进字符数",
                                        message = "表格首行缩进字符数不正确，当前=" + p.Format.FirstLineIndentChars.ToString("0.##") + "，应为=" + expectedTableIndent.ToString("0.##"),
                                        location = location,
                                        currentValue = p.Format.FirstLineIndentChars.ToString("0.##"),
                                        expectedValue = expectedTableIndent.ToString("0.##"),
                                        severity = "warning",
                                        snippet = Clip(text)
                                    });
                                }

                                var currentTableAlign = NormalizeHorizontalAlignLabel(p.Format.HorizontalAlignment.ToString());
                                if (expectedTableAlign > -1 && !TableParagraphAlignMatches(expectedTableAlignName, currentTableAlign))
                                {
                                    issues.Add(new RuleIssue
                                    {
                                        category = "表格",
                                        rule = "对齐方式",
                                        message = "表格段落对齐方式不正确，当前=" + currentTableAlign + "，应为=" + expectedTableAlignName,
                                        location = location,
                                        currentValue = currentTableAlign,
                                        expectedValue = expectedTableAlignName,
                                        severity = "warning",
                                        snippet = Clip(text)
                                    });
                                }

                                if (checkBeforeAfter && !IsParagraphSpacingClean(p))
                                {
                                    issues.Add(new RuleIssue
                                    {
                                        category = "表格",
                                        rule = "检查段前段后",
                                        message = "表格段落段前段后不为0。",
                                        location = location,
                                        currentValue = ParagraphSpacingSummary(p),
                                        expectedValue = "左右缩进/段前后行/段前后间距均为0",
                                        severity = "warning",
                                        snippet = Clip(text)
                                    });
                                }

                                if (!string.IsNullOrWhiteSpace(expectedTableCharSpacing))
                                {
                                    var cs = p.BreakCharacterFormat.CharacterSpacing;
                                    if (!IsCharSpacingMatch(expectedTableCharSpacing, cs))
                                    {
                                        issues.Add(new RuleIssue
                                        {
                                            category = "表格",
                                            rule = "字间距",
                                            message = "表格字间距不正确，当前是" + CharSpacingLabel(cs) + "，应该是" + expectedTableCharSpacing + "。",
                                            location = location,
                                            currentValue = cs.ToString("0.##"),
                                            expectedValue = expectedTableCharSpacing,
                                            severity = "warning",
                                            snippet = Clip(text)
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void CheckWordTextRules(Document doc, RuleBook rules, List<RuleIssue> issues)
        {
            if (doc == null || rules == null || issues == null)
            {
                return;
            }

            var sensitiveWords = SplitTerms(
                FirstNonEmpty(
                    rules.Get("检查项", "敏感词词典"),
                    rules.Get("检查项", "敏感词列表"),
                    rules.Get("检查项", "敏感词")
                )
            );
            var placeWords = SplitTerms(
                FirstNonEmpty(
                    rules.Get("检查项", "地名词典"),
                    rules.Get("检查项", "地名列表"),
                    rules.Get("检查项", "地名"),
                    rules.Get("检查项", "地名库")
                )
            );
            var checkPlace = rules.GetBool("检查项", "地名检查", placeWords.Count > 0);
            var companyWords = SplitTerms(
                FirstNonEmpty(
                    rules.Get("检查项", "公司名词典"),
                    rules.Get("检查项", "公司名列表"),
                    rules.Get("检查项", "公司名"),
                    rules.Get("检查项", "公司列表")
                )
            );
            var checkCompany = rules.GetBool("检查项", "公司名检查", companyWords.Count > 0);
            var punctuationConfig = FirstNonEmpty(
                rules.Get("检查项", "标点词典"),
                rules.Get("检查项", "非中文符号"),
                rules.Get("检查项", "符号词典"),
                rules.Get("检查项", "标点符号")
            );
            var checkPunctuation = rules.GetBool("检查项", "标点检查", !string.IsNullOrWhiteSpace(punctuationConfig));
            var punctuationSymbols = BuildPunctuationSet(
                punctuationConfig
            );

            var idx = 0;
            foreach (Section section in doc.Sections)
            {
                foreach (Paragraph p in section.Paragraphs)
                {
                    idx += 1;
                    var text = (p == null ? string.Empty : p.Text) ?? string.Empty;
                    text = text.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    var location = "P" + idx;
                    var normalizedText = NormalizeSearchText(text);
                    if (normalizedText.Length == 0)
                    {
                        continue;
                    }

                    AddWordListIssues(issues, normalizedText, text, location, sensitiveWords, "敏感词", "存在敏感词：", "不出现敏感词");
                    if (checkPlace)
                    {
                        AddWordListIssues(issues, normalizedText, text, location, placeWords, "地名检查", "存在地名：", "不出现地名");
                    }
                    if (checkCompany)
                    {
                        AddWordListIssues(issues, normalizedText, text, location, companyWords, "公司名检查", "存在公司名：", "不出现公司名");
                    }
                    if (checkPunctuation)
                    {
                        AddPunctuationIssues(issues, text, location, punctuationSymbols);
                    }
                }
            }
        }

        private static void AddWordListIssues(
            List<RuleIssue> issues,
            string normalizedText,
            string rawText,
            string location,
            List<string> words,
            string rule,
            string messagePrefix,
            string expectedValue)
        {
            if (issues == null || words == null || words.Count == 0 || string.IsNullOrWhiteSpace(normalizedText))
            {
                return;
            }

            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word))
                {
                    continue;
                }

                if (normalizedText.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    issues.Add(new RuleIssue
                    {
                        category = "检查项",
                        rule = rule,
                        message = messagePrefix + word + "。",
                        location = location,
                        currentValue = word,
                        expectedValue = expectedValue,
                        severity = "warning",
                        @fixed = false,
                        snippet = Clip(rawText)
                    });
                }
            }
        }

        private static void AddPunctuationIssues(List<RuleIssue> issues, string rawText, string location, HashSet<char> punctuationSymbols)
        {
            if (issues == null || punctuationSymbols == null || punctuationSymbols.Count == 0 || string.IsNullOrWhiteSpace(rawText))
            {
                return;
            }

            var hit = rawText.Where(ch => punctuationSymbols.Contains(ch)).Distinct().ToList();
            if (hit.Count == 0)
            {
                return;
            }

            var msg = string.Concat(hit.Select(ch => "存在非中文符号“" + ch + "”。"));
            issues.Add(new RuleIssue
            {
                category = "检查项",
                rule = "标点检查",
                message = msg,
                location = location,
                currentValue = string.Concat(hit),
                expectedValue = "不出现非中文符号",
                severity = "warning",
                @fixed = false,
                snippet = Clip(rawText)
            });
        }

        private static void CheckPageAdvanced(Document doc, RuleBook rules, List<RuleIssue> issues)
        {
            if (doc.Sections.Count == 0)
            {
                return;
            }
            var sec = doc.Sections[0] as Section;
            var ps = sec.PageSetup;

            var expectedPageSize = rules.Get("页面", "页面大小");
            if (!string.IsNullOrWhiteSpace(expectedPageSize) && expectedPageSize.Equals("A4", StringComparison.OrdinalIgnoreCase))
            {
                var w = ps.PageSize.Width;
                var h = ps.PageSize.Height;
                var a4w = 595.28f;
                var a4h = 841.89f;
                var ok = (Math.Abs(w - a4w) <= 6 && Math.Abs(h - a4h) <= 6)
                         || (Math.Abs(w - a4h) <= 6 && Math.Abs(h - a4w) <= 6);
                if (!ok)
                {
                    issues.Add(new RuleIssue
                    {
                        category = "页面",
                        rule = "页面大小",
                        message = "页面大小不正确。",
                        location = "P1",
                        currentValue = w.ToString("0.#") + "x" + h.ToString("0.#") + "pt",
                        expectedValue = "A4",
                        severity = "warning"
                    });
                }
            }

            var expectedOrientation = rules.Get("页面", "页面方向");
            if (!string.IsNullOrWhiteSpace(expectedOrientation))
            {
                var current = ps.Orientation == PageOrientation.Landscape ? "横向" : "纵向";
                if (!string.Equals(current, expectedOrientation, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new RuleIssue
                    {
                        category = "页面",
                        rule = "页面方向",
                        message = "页面方向不正确。",
                        location = "P1",
                        currentValue = current,
                        expectedValue = expectedOrientation,
                        severity = "warning"
                    });
                }
            }
        }

        private static void CheckCoverRules(Document doc, RuleBook rules, List<RuleIssue> issues)
        {
            if (doc.Sections.Count == 0)
            {
                return;
            }
            var sec = doc.Sections[0] as Section;
            if (sec.Paragraphs.Count == 0)
            {
                return;
            }

            var expectedTitle = rules.Get("页面", "封面标题");
            if (string.IsNullOrWhiteSpace(expectedTitle))
            {
                return;
            }

            Paragraph found = null;
            foreach (Paragraph p in sec.Paragraphs)
            {
                var t = (p.Text ?? string.Empty).Trim();
                if (t.Length == 0)
                {
                    continue;
                }
                if (t.Contains(expectedTitle))
                {
                    found = p;
                    break;
                }
            }

            if (found == null)
            {
                issues.Add(new RuleIssue
                {
                    category = "页面",
                    rule = "封面标题",
                    message = "封面标题不正确。",
                    location = "P1",
                    currentValue = "未找到",
                    expectedValue = expectedTitle,
                    severity = "warning"
                });
                return;
            }

            var expectedCoverFont = rules.Get("页面", "封面字体");
            var expectedCoverSizeName = rules.Get("页面", "封面字号");
            var expectedCoverSize = FontSizeNameToPt(expectedCoverSizeName);
            var expectedCoverAlign = rules.Get("页面", "封面水平对齐方式");
            var expectedCoverVAlign = rules.Get("页面", "封面垂直对齐方式");

            foreach (DocumentObject child in found.ChildObjects)
            {
                if (child.DocumentObjectType != DocumentObjectType.TextRange)
                {
                    continue;
                }
                var tr = child as TextRange;
                if (tr == null || string.IsNullOrWhiteSpace(tr.Text))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(expectedCoverFont))
                {
                    var cur = GetEffectiveChineseFont(tr);
                    if (!string.IsNullOrWhiteSpace(cur) && !string.Equals(cur, expectedCoverFont, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new RuleIssue
                        {
                            category = "页面",
                            rule = "封面字体",
                            message = "封面字体不正确。",
                            location = "P1",
                            currentValue = cur,
                            expectedValue = expectedCoverFont,
                            severity = "warning"
                        });
                    }
                }

                var currentCoverSize = GetEffectiveFontSize(tr);
                if (expectedCoverSize > 0 && Math.Abs(currentCoverSize - expectedCoverSize) > 0.1)
                {
                    issues.Add(new RuleIssue
                    {
                        category = "页面",
                        rule = "封面字号",
                        message = "封面字号不正确。",
                        location = "P1",
                        currentValue = currentCoverSize.ToString("0.##"),
                        expectedValue = expectedCoverSizeName,
                        severity = "warning"
                    });
                }
                break;
            }

            if (!string.IsNullOrWhiteSpace(expectedCoverAlign))
            {
                var expected = MapAlign(expectedCoverAlign);
                var current = (int)found.Format.HorizontalAlignment;
                if (expected >= 0 && current != expected)
                {
                    issues.Add(new RuleIssue
                    {
                        category = "页面",
                        rule = "封面水平对齐方式",
                        message = "封面水平对齐方式不正确。",
                        location = "P1",
                        currentValue = NormalizeHorizontalAlignLabel(found.Format.HorizontalAlignment.ToString()),
                        expectedValue = expectedCoverAlign,
                        severity = "warning"
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedCoverVAlign))
            {
                var currentVAlign = GetSectionVerticalAlignLabel(sec);
                if (!string.IsNullOrWhiteSpace(currentVAlign) && !VerticalAlignMatches(expectedCoverVAlign, currentVAlign))
                {
                    issues.Add(new RuleIssue
                    {
                        category = "页面",
                        rule = "封面垂直对齐方式",
                        message = "封面垂直对齐方式不正确。",
                        location = "P1",
                        currentValue = currentVAlign,
                        expectedValue = expectedCoverVAlign,
                        severity = "warning"
                    });
                }
            }
        }

        private static void CheckColorImages(Document doc, RuleBook rules, List<RuleIssue> issues)
        {
            if (!rules.GetBool("检查项", "彩色图片检查", false))
            {
                return;
            }

            var index = 0;
            foreach (Section section in doc.Sections)
            {
                foreach (Paragraph p in section.Paragraphs)
                {
                    foreach (DocumentObject child in p.ChildObjects)
                    {
                        if (child.DocumentObjectType != DocumentObjectType.Picture)
                        {
                            continue;
                        }
                        var pic = child as DocPicture;
                        if (pic == null || pic.Image == null)
                        {
                            continue;
                        }
                        if (IsGrayImage(pic.Image))
                        {
                            continue;
                        }
                        index++;
                        issues.Add(new RuleIssue
                        {
                            category = "检查项",
                            rule = "彩色图片检查",
                            message = "存在彩色图片。",
                            location = "IMG" + index,
                            currentValue = "彩色",
                            expectedValue = "黑白",
                            severity = "warning"
                        });
                    }
                }
            }
        }

        private static bool IsGrayImage(Image img)
        {
            try
            {
                using (var bmp = new Bitmap(img))
                {
                    var stepX = Math.Max(1, bmp.Width / 24);
                    var stepY = Math.Max(1, bmp.Height / 24);
                    for (int y = 0; y < bmp.Height; y += stepY)
                    {
                        for (int x = 0; x < bmp.Width; x += stepX)
                        {
                            var c = bmp.GetPixel(x, y);
                            if (!(Math.Abs(c.R - c.G) <= 4 && Math.Abs(c.G - c.B) <= 4))
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            catch
            {
                return true;
            }
            return true;
        }

        private static bool LooksLikeSentenceEnd(string text)
        {
            var t = (text ?? string.Empty).Trim();
            if (t.Length == 0)
            {
                return true;
            }
            var c = t[t.Length - 1];
            return "。！？；：.!?;:".IndexOf(c) >= 0;
        }

        private static bool ContainsAsciiLetterOrDigit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            return text.Any(ch => ch <= 127 && char.IsLetterOrDigit(ch));
        }

        private static bool FontMatches(string current, string expectedCn)
        {
            var cur = NormalizeFont(current);
            foreach (var alias in FontAliases(expectedCn))
            {
                if (cur.Contains(alias))
                {
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeFont(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }
            return name.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        }

        private static List<string> FontAliases(string expectedCn)
        {
            var v = (expectedCn ?? string.Empty).Trim();
            switch (v)
            {
                case "宋体": return new List<string> { "simsun", "song", "stsong" };
                case "黑体": return new List<string> { "simhei", "heiti", "stheiti", "hei" };
                case "楷体": return new List<string> { "kaiti", "stkaiti", "kai" };
                case "仿宋": return new List<string> { "fangsong", "stfangsong", "fang" };
                case "微软雅黑": return new List<string> { "yahei", "microsoftyahei" };
                default: return new List<string> { NormalizeFont(v) };
            }
        }

        private static bool IsBlack(Color c)
        {
            return c.R <= 8 && c.G <= 8 && c.B <= 8;
        }

        private static string GetSectionVerticalAlignLabel(Section section)
        {
            if (section == null || section.PageSetup == null)
            {
                return string.Empty;
            }
            try
            {
                // 兼容不同 Spire 版本：PageSetup.VerticalAlignment / VerticalAlign / PageVerticalAlignment
                var psType = section.PageSetup.GetType();
                foreach (var propName in new[] { "VerticalAlignment", "VerticalAlign", "PageVerticalAlignment" })
                {
                    var prop = psType.GetProperty(propName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (prop == null)
                    {
                        continue;
                    }
                    var raw = prop.GetValue(section.PageSetup, null);
                    if (raw == null)
                    {
                        continue;
                    }
                    var text = raw.ToString();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }
                    return NormalizeVerticalAlignLabel(text);
                }
            }
            catch
            {
                return string.Empty;
            }
            return string.Empty;
        }

        private static string NormalizeVerticalAlignLabel(string raw)
        {
            var t = (raw ?? string.Empty).Trim();
            if (t.Length == 0)
            {
                return string.Empty;
            }
            if (t.Contains("Top") || t.Contains("顶"))
            {
                return "顶端对齐";
            }
            if (t.Contains("Center") || t.Contains("中"))
            {
                return "居中对齐";
            }
            if (t.Contains("Bottom") || t.Contains("底"))
            {
                return "底端对齐";
            }
            if (t.Contains("Auto") || t.Contains("自动"))
            {
                return "自动";
            }
            if (t.Contains("Baseline") || t.Contains("基线"))
            {
                return "基线对齐";
            }
            return t;
        }

        private static string NormalizeHorizontalAlignLabel(string raw)
        {
            var t = (raw ?? string.Empty).Trim();
            if (t.Length == 0)
            {
                return string.Empty;
            }
            if (t.Contains("Left") || t.Contains("左"))
            {
                return "左对齐";
            }
            if (t.Contains("Center") || t.Contains("中"))
            {
                return "居中";
            }
            if (t.Contains("Right") || t.Contains("右"))
            {
                return "右对齐";
            }
            if (t.Contains("Justify") || t.Contains("两端"))
            {
                return "两端对齐";
            }
            if (t.Contains("Distributed") || t.Contains("分散"))
            {
                return "分散对齐";
            }
            return t;
        }

        private static bool VerticalAlignMatches(string expectedRaw, string currentRaw)
        {
            var expected = NormalizeVerticalAlignLabel(expectedRaw);
            var current = NormalizeVerticalAlignLabel(currentRaw);
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(current))
            {
                return true;
            }
            return string.Equals(expected, current, StringComparison.OrdinalIgnoreCase);
        }

        private static string Clip(string s)
        {
            var t = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length <= 28 ? t : t.Substring(0, 28) + "...";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static List<string> SplitTerms(string raw)
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

        private static string NormalizeSearchText(string text)
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

        private static HashSet<char> BuildPunctuationSet(string configured)
        {
            var source = string.IsNullOrWhiteSpace(configured)
                ? "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"
                : configured;

            return new HashSet<char>(source.Where(ch => !char.IsWhiteSpace(ch)));
        }

        private static string GetEffectiveChineseFont(TextRange tr)
        {
            if (tr == null || tr.CharacterFormat == null)
            {
                return string.Empty;
            }

            return FirstNonEmpty(
                tr.CharacterFormat.FontNameFarEast,
                tr.CharacterFormat.FontName
            );
        }

        private static string GetEffectiveAsciiFont(TextRange tr)
        {
            if (tr == null || tr.CharacterFormat == null)
            {
                return string.Empty;
            }

            return FirstNonEmpty(
                tr.CharacterFormat.FontNameNonFarEast,
                tr.CharacterFormat.FontNameAscii,
                tr.CharacterFormat.FontName
            );
        }

        private static float GetEffectiveFontSize(TextRange tr)
        {
            if (tr == null || tr.CharacterFormat == null)
            {
                return 0f;
            }

            try
            {
                if (tr.CharacterFormat.Font != null && tr.CharacterFormat.Font.Size > 0.01f)
                {
                    return tr.CharacterFormat.Font.Size;
                }
            }
            catch
            {
            }

            return tr.CharacterFormat.FontSize > 0.01f ? tr.CharacterFormat.FontSize : 0f;
        }

        private static string NormalizeLegacyLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return string.Empty;
            }

            var match = Regex.Match(location, @"P\d+");
            return match.Success ? match.Value : string.Empty;
        }

        private static int MapAlign(string align)
        {
            if (align == "左对齐") return 0;
            if (align == "居中") return 1;
            if (align == "右对齐") return 2;
            if (align == "两端对齐") return 3;
            return -1;
        }

        private static int MapTextAlign(string align)
        {
            if (align == "顶部对齐") return 0;
            if (align == "居中") return 1;
            if (align == "底部对齐") return 2;
            return -1;
        }

        private static int DetectHeadingLevel(Paragraph p)
        {
            if (p == null)
            {
                return 0;
            }

            var outline = (int)p.Format.OutlineLevel;
            if (outline >= 0 && outline <= 6)
            {
                return outline + 1;
            }

            var styleName = string.Empty;
            if (p.StyleName != null)
            {
                styleName = p.StyleName.Trim();
            }

            if (string.IsNullOrWhiteSpace(styleName))
            {
                return 0;
            }

            if (styleName.Contains("Heading 1") || styleName.Contains("标题 1") || styleName.Contains("标题1") || styleName.Contains("一级标题")) return 1;
            if (styleName.Contains("Heading 2") || styleName.Contains("标题 2") || styleName.Contains("标题2") || styleName.Contains("二级标题")) return 2;
            if (styleName.Contains("Heading 3") || styleName.Contains("标题 3") || styleName.Contains("标题3") || styleName.Contains("三级标题")) return 3;
            if (styleName.Contains("Heading 4") || styleName.Contains("标题 4") || styleName.Contains("标题4") || styleName.Contains("四级标题")) return 4;
            if (styleName.Contains("Heading 5") || styleName.Contains("标题 5") || styleName.Contains("标题5") || styleName.Contains("五级标题")) return 5;
            if (styleName.Contains("Heading 6") || styleName.Contains("标题 6") || styleName.Contains("标题6") || styleName.Contains("六级标题")) return 6;
            if (styleName.Contains("Heading 7") || styleName.Contains("标题 7") || styleName.Contains("标题7") || styleName.Contains("七级标题")) return 7;
            return 0;
        }

        private static int DetectHeadingLevel(Paragraph p, RuleBook rules)
        {
            var level = DetectHeadingLevel(p);
            if (level > 0)
            {
                return level;
            }

            var text = (p == null ? string.Empty : p.Text) ?? string.Empty;
            text = text.Trim();
            if (text.Length == 0)
            {
                return 0;
            }

            var sectionByLevel = new Dictionary<int, string>
            {
                { 1, "一级标题" },
                { 2, "二级标题" },
                { 3, "三级标题" },
                { 4, "四级标题" },
                { 5, "五级标题" },
                { 6, "六级标题" },
                { 7, "七级标题" }
            };

            foreach (var kv in sectionByLevel)
            {
                var raw = rules.Get(kv.Value, "标题规则");
                var pattern = ResolveTitlePattern(rules, raw);
                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    try
                    {
                        if (Regex.IsMatch(text, pattern))
                        {
                            return kv.Key;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (Regex.IsMatch(text, @"^[一二三四五六七八九十百千]+、")) return 1;
            if (Regex.IsMatch(text, @"^（[一二三四五六七八九十百千]+）")) return 2;
            if (Regex.IsMatch(text, @"^[0-9]{1,3}\.[^0-9]")) return 1;
            if (Regex.IsMatch(text, @"^[0-9]{1,3}\.[0-9]{1,3}([^0-9\.]|$)")) return 2;
            if (Regex.IsMatch(text, @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}([^0-9\.]|$)")) return 3;

            return 0;
        }

        private static bool TableParagraphAlignMatches(string expectedRaw, string currentRaw)
        {
            var expected = NormalizeHorizontalAlignLabel(expectedRaw);
            var current = NormalizeHorizontalAlignLabel(currentRaw);
            if (string.Equals(expected, current, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return expected == "左对齐" && (current == "两端对齐" || current == "分散对齐");
        }

        private static string StripAllowedTitleGap(string text, int headingLevel)
        {
            var value = (text ?? string.Empty).Trim();
            if (headingLevel <= 0 || value.Length <= 1)
            {
                return value;
            }

            var titlePrefixPatterns = new[]
            {
                @"^[一二三四五六七八九十百千]+、",
                @"^（[一二三四五六七八九十百千]+）",
                @"^\([一二三四五六七八九十百千]+\)",
                @"^[0-9]{1,3}\.",
                @"^[0-9]{1,3}、",
                @"^[0-9]{1,3}\.[0-9]{1,3}",
                @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}",
                @"^（[0-9]{1,3}）",
                @"^\([0-9]{1,3}\)",
                @"^[0-9]{1,3}）"
            };

            foreach (var pattern in titlePrefixPatterns)
            {
                var match = Regex.Match(value, pattern);
                if (match.Success)
                {
                    var rest = value.Substring(match.Length);
                    if (rest.StartsWith(" "))
                    {
                        return rest.Substring(1);
                    }
                    return rest;
                }
            }

            return value;
        }

        private static bool HasTitleNumberNoSpace(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = text.TrimStart();
            return Regex.IsMatch(normalized, @"^([0-9]{1,3}(\.[0-9]{1,3})*|[一二三四五六七八九十百千]+)[、\.\)](?=\S)");
        }

        private static bool HasListNumberNoSpace(Paragraph p)
        {
            try
            {
                if (p == null || string.IsNullOrWhiteSpace(p.ListText) || p.ListFormat == null || p.ListFormat.CurrentListLevel == null)
                {
                    return false;
                }

                // 1 means FollowCharacter.Space in old project behavior.
                return (int)p.ListFormat.CurrentListLevel.FollowCharacter != 1;
            }
            catch
            {
                return false;
            }
        }

        private static float GetExpectedLineSpacing(string spacingName)
        {
            if (string.IsNullOrWhiteSpace(spacingName))
            {
                return 0f;
            }

            spacingName = spacingName.Trim();
            if (spacingName == "单倍行距" || spacingName == "1倍行距") return 12f;
            if (spacingName == "1.5倍行距") return 18f;
            if (spacingName == "2倍行距") return 24f;
            return 0f;
        }

        private static string ResolveTitlePattern(RuleBook rules, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var v = raw.Trim();
            if (v.Contains("^") || v.Contains("(") || v.Contains("[") || v.Contains("\\d"))
            {
                return v;
            }

            var preset = rules.ResolveTitlePatternLabel(v);
            if (!string.IsNullOrWhiteSpace(preset))
            {
                return preset;
            }

            var presets = new Dictionary<string, string>
            {
                { "一、", @"^[一二三四五六七八九十百千]+、" },
                { "（一）", @"^（[一二三四五六七八九十百千]+）" },
                { "(一)", @"^\([一二三四五六七八九十百千]+\)" },
                { "1.", @"^[0-9]{1,3}\." },
                { "1、", @"^[0-9]{1,3}、" },
                { "1.1", @"^[0-9]{1,3}\.[0-9]{1,3}" },
                { "1.1.1", @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}" },
                { "（1）", @"^（[0-9]{1,3}）" },
                { "(1)", @"^\([0-9]{1,3}\)" },
                { "1）", @"^[0-9]{1,3}）" },
                { "①", @"^[①②③④⑤⑥⑦⑧⑨⑩]" },
                { "第X章", @"^第[一二三四五六七八九十百千0-9]+章" },
                { "第X节", @"^第[一二三四五六七八九十百千0-9]+节" },
                { "附件X", @"^附件[一二三四五六七八九十百千0-9]+" }
            };

            return presets.ContainsKey(v) ? presets[v] : v;
        }

        private static bool IsCharSpacingMatch(string expected, float current)
        {
            var v = expected.Trim();
            if (v == "标准") return Math.Abs(current) < 0.01;
            if (v == "增大") return current > 0f;
            if (v == "紧缩") return current < 0f;
            return true;
        }

        private static string CharSpacingLabel(float value)
        {
            if (Math.Abs(value) < 0.01) return "标准";
            if (value > 0f) return "增大";
            return "紧缩";
        }

        private static bool IsParagraphSpacingClean(Paragraph p)
        {
            return Math.Abs(p.Format.RightIndentChars) < 0.01
                && Math.Abs(p.Format.LeftIndentChars) < 0.01
                && Math.Abs(p.Format.AfterSpacingLines) < 0.01
                && Math.Abs(p.Format.BeforeSpacingLines) < 0.01
                && Math.Abs(p.Format.AfterSpacing) < 0.01
                && Math.Abs(p.Format.BeforeSpacing) < 0.01;
        }

        private static string ParagraphSpacingSummary(Paragraph p)
        {
            return string.Format(
                "left={0:0.##}, right={1:0.##}, beforeLines={2:0.##}, afterLines={3:0.##}, before={4:0.##}, after={5:0.##}",
                p.Format.LeftIndentChars,
                p.Format.RightIndentChars,
                p.Format.BeforeSpacingLines,
                p.Format.AfterSpacingLines,
                p.Format.BeforeSpacing,
                p.Format.AfterSpacing
            );
        }

        private static double FontSizeNameToPt(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            var map = new Dictionary<string, double>
            {
                { "初号", 42 },
                { "小初", 36 },
                { "一号", 26 },
                { "小一", 24 },
                { "二号", 22 },
                { "小二", 18 },
                { "三号", 16 },
                { "小三", 15 },
                { "四号", 14 },
                { "小四", 12 },
                { "五号", 10.5 },
                { "小五", 9 },
                { "六号", 7.5 },
                { "小六", 6.5 },
                { "七号", 5.5 },
                { "八号", 5 }
            };
            return map.ContainsKey(name.Trim()) ? map[name.Trim()] : 0;
        }

        private static string DetectFileType(string filePath)
        {
            var ext = Path.GetExtension(filePath ?? string.Empty).ToLowerInvariant();
            if (ext == ".doc") return "doc";
            if (ext == ".docx") return "docx";
            if (ext == ".pdf") return "pdf";
            return "unknown";
        }

        private static double PtToCm(float pt)
        {
            return Math.Round(pt * 2.54 / 72.0, 3);
        }

        private static void Write(ParseDocumentResponse response)
        {
            Console.WriteLine(Json.Serialize(response));
        }

        private static string BuildReportText(ParseDocumentResponse response)
        {
            var sb = new StringBuilder();
            sb.AppendLine("AiCheckBid 检查报告");
            sb.AppendLine("文件: " + response.filePath);
            sb.AppendLine("类型: " + response.fileType);
            sb.AppendLine("解析器: " + response.parser);
            sb.AppendLine("页数: " + (response.pageCount.HasValue ? response.pageCount.Value.ToString() : "-"));
            sb.AppendLine("问题数: " + response.issues.Count);
            sb.AppendLine(new string('-', 36));

            var sections = GetLegacySections();
            foreach (var section in sections)
            {
                sb.AppendLine(section + ":");
                var sectionText = GetLegacySectionText(response, section);
                if (string.IsNullOrWhiteSpace(sectionText))
                {
                    sb.AppendLine("未发现问题。");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine(sectionText);
                sb.AppendLine();
            }

            if (response.warnings != null && response.warnings.Count > 0)
            {
                sb.AppendLine(new string('-', 36));
                sb.AppendLine("告警:");
                foreach (var warning in response.warnings)
                {
                    sb.AppendLine("- " + warning);
                }
            }

            return sb.ToString();
        }

        private static string[] GetLegacySections()
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

        private static Dictionary<string, List<RuleIssue>> GroupIssuesByLegacySection(List<RuleIssue> issues)
        {
            return issues
                .GroupBy(MapIssueToLegacySection)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }

        private static string GetLegacySectionText(ParseDocumentResponse response, string section)
        {
            if (response != null && response.legacySections != null)
            {
                if (string.Equals(section, "格式检查结果", StringComparison.OrdinalIgnoreCase)) return response.legacySections.format;
                if (string.Equals(section, "公司名检查结果", StringComparison.OrdinalIgnoreCase)) return response.legacySections.company;
                if (string.Equals(section, "地名检查结果", StringComparison.OrdinalIgnoreCase)) return response.legacySections.place;
                if (string.Equals(section, "人名检查结果", StringComparison.OrdinalIgnoreCase)) return response.legacySections.person;
                if (string.Equals(section, "敏感词检查结果", StringComparison.OrdinalIgnoreCase)) return response.legacySections.sensitive;
                if (string.Equals(section, "标点符号检查结果", StringComparison.OrdinalIgnoreCase)) return response.legacySections.punctuation;
                if (string.Equals(section, "其他检查结果", StringComparison.OrdinalIgnoreCase)) return response.legacySections.other;
            }

            var grouped = GroupIssuesByLegacySection(response.issues ?? new List<RuleIssue>());
            List<RuleIssue> list;
            if (!grouped.TryGetValue(section, out list) || list.Count == 0)
            {
                return string.Empty;
            }

            var body = new StringBuilder();
            foreach (var issue in list)
            {
                var content = !string.IsNullOrWhiteSpace(issue.snippet) ? issue.snippet : issue.currentValue;
                var location = response.outputPageNumber ? NormalizeLegacyLocation(issue.location) : string.Empty;
                var message = ApplyCommentMarker(issue.message, response.commentMarker);
                body.AppendLine(FormatLegacyReportRow(location, message, content));
            }
            return body.ToString().TrimEnd();
        }

        private static string MapIssueToLegacySection(RuleIssue issue)
        {
            if (issue.rule == "公司名检查") return "公司名检查结果";
            if (issue.rule == "地名检查") return "地名检查结果";
            if (issue.rule == "敏感词") return "敏感词检查结果";
            if (issue.rule == "标点检查") return "标点符号检查结果";
            if (issue.rule == "人名检查") return "人名检查结果";
            if (issue.category == "页面" || issue.category == "正文" || issue.category == "标题" || issue.category == "表格")
            {
                return "格式检查结果";
            }
            return "其他检查结果";
        }

        private static string ApplyCommentMarker(string message, string marker)
        {
            marker = marker ?? string.Empty;
            // 原版“批注标记”用于批注/标注行为，不改写报告文本内容。
            return message ?? string.Empty;
        }

        private static string FormatLegacyReportRow(string location, string message, string content)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return string.Format("{0}\t未处理\t{1}\t", message, content);
            }

            return string.Format("{0}\t{1}\t未处理\t{2}\t", location, message, content);
        }

        private static void WriteLegacySourceCopy(ParseDocumentResponse response, string resultDir)
        {
            var sourcePath = response == null ? string.Empty : response.filePath;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return;
            }

            var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                sourceName = "document";
            }
            var targetPath = Path.Combine(resultDir, sourceName + "m" + Path.GetExtension(sourcePath));
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                if (response != null && response.smartFixSaved && File.Exists(targetPath))
                {
                    if (!CanWriteLegacyComments(response))
                    {
                        return;
                    }

                    try
                    {
                        using (var doc = new Document())
                        {
                            doc.LoadFromFile(targetPath);
                            ApplyLegacyComments(doc, response.issues, response.commentMarker);
                            doc.SaveToFile(targetPath, ResolveWordFileFormat(sourcePath));
                        }
                        return;
                    }
                    catch
                    {
                    }
                }

                if (CanWriteLegacyComments(response))
                {
                    try
                    {
                        using (var doc = new Document())
                        {
                            doc.LoadFromFile(sourcePath);
                            ApplyLegacyComments(doc, response.issues, response.commentMarker);
                            doc.SaveToFile(targetPath, ResolveWordFileFormat(sourcePath));
                        }
                        return;
                    }
                    catch
                    {
                    }
                }

                File.Copy(sourcePath, targetPath, true);
            }
        }

        private static void SaveSmartFixedCopy(Document doc, string sourcePath, ParseDocumentResponse response)
        {
            if (doc == null || response == null || !response.smartFixEnabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return;
            }

            try
            {
                var resultDir = ResolveResultDir(sourcePath);
                Directory.CreateDirectory(resultDir);
                var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(sourceName))
                {
                    sourceName = "document";
                }

                var targetPath = Path.Combine(resultDir, sourceName + "m" + Path.GetExtension(sourcePath));
                if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                doc.SaveToFile(targetPath, ResolveWordFileFormat(sourcePath));
                response.smartFixSaved = true;
            }
            catch (Exception ex)
            {
                response.warnings.Add("智能修正结果保存失败: " + ex.Message);
            }
        }

        private static bool CanWriteLegacyComments(ParseDocumentResponse response)
        {
            if (response == null || response.issues == null || response.issues.Count == 0)
            {
                return false;
            }

            return IsCommentMarkerEnabled(response.commentMarker)
                && (string.Equals(response.fileType, "doc", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(response.fileType, "docx", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCommentMarkerEnabled(string marker)
        {
            return string.Equals(marker, "全标记", StringComparison.Ordinal)
                || string.Equals(marker, "可疑问题不批注", StringComparison.Ordinal);
        }

        private static void ApplyLegacyComments(Document doc, List<RuleIssue> issues, string marker)
        {
            if (doc == null || issues == null || issues.Count == 0 || !IsCommentMarkerEnabled(marker))
            {
                return;
            }

            var paragraphMap = BuildLegacyParagraphMap(doc);
            var tableAnchorMap = BuildLegacyTableAnchorMap(doc);
            var commentMap = BuildLegacyCommentMap(issues, marker);
            foreach (var pair in commentMap)
            {
                Paragraph paragraph;
                if (pair.Key.StartsWith("P", StringComparison.Ordinal))
                {
                    int index;
                    if (!TryParseLegacyLocationIndex(pair.Key, 'P', out index) || !paragraphMap.TryGetValue(index, out paragraph))
                    {
                        continue;
                    }
                }
                else if (pair.Key.StartsWith("T", StringComparison.Ordinal))
                {
                    int index;
                    if (!TryParseLegacyLocationIndex(pair.Key, 'T', out index) || !tableAnchorMap.TryGetValue(index, out paragraph))
                    {
                        continue;
                    }
                }
                else
                {
                    continue;
                }

                AppendLegacyComment(paragraph, pair.Value);
            }
        }

        private static Dictionary<int, Paragraph> BuildLegacyParagraphMap(Document doc)
        {
            var map = new Dictionary<int, Paragraph>();
            var index = 0;
            foreach (Section section in doc.Sections)
            {
                foreach (Paragraph paragraph in section.Paragraphs)
                {
                    index++;
                    map[index] = paragraph;
                }
            }
            return map;
        }

        private static Dictionary<int, Paragraph> BuildLegacyTableAnchorMap(Document doc)
        {
            var map = new Dictionary<int, Paragraph>();
            var index = 0;
            foreach (Section section in doc.Sections)
            {
                foreach (Table table in section.Tables)
                {
                    index++;
                    var anchor = FindTableAnchorParagraph(table);
                    if (anchor != null)
                    {
                        map[index] = anchor;
                    }
                }
            }
            return map;
        }

        private static Paragraph FindTableAnchorParagraph(Table table)
        {
            if (table == null)
            {
                return null;
            }

            foreach (TableRow row in table.Rows)
            {
                foreach (TableCell cell in row.Cells)
                {
                    foreach (Paragraph paragraph in cell.Paragraphs)
                    {
                        if (paragraph != null)
                        {
                            return paragraph;
                        }
                    }
                }
            }

            if (table.Rows.Count == 0 || table.Rows[0].Cells.Count == 0)
            {
                return null;
            }

            return table.Rows[0].Cells[0].AddParagraph();
        }

        private static Dictionary<string, string> BuildLegacyCommentMap(List<RuleIssue> issues, string marker)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var issue in issues)
            {
                if (!ShouldWriteIssueComment(issue, marker))
                {
                    continue;
                }

                var location = (issue.location ?? string.Empty).Trim();
                if (location.Length == 0)
                {
                    continue;
                }

                List<string> messages;
                if (!map.TryGetValue(location, out messages))
                {
                    messages = new List<string>();
                    map[location] = messages;
                }

                var message = BuildLegacyCommentMessage(issue);
                if (!string.IsNullOrWhiteSpace(message) && !messages.Contains(message))
                {
                    messages.Add(message);
                }
            }

            return map.ToDictionary(
                pair => pair.Key,
                pair => string.Join(Environment.NewLine, pair.Value.Where(x => !string.IsNullOrWhiteSpace(x))),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool ShouldWriteIssueComment(RuleIssue issue, string marker)
        {
            if (issue == null || !IsCommentMarkerEnabled(marker))
            {
                return false;
            }

            var location = (issue.location ?? string.Empty).Trim();
            if (!(location.StartsWith("P", StringComparison.OrdinalIgnoreCase) || location.StartsWith("T", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!string.Equals(marker, "可疑问题不批注", StringComparison.Ordinal))
            {
                return true;
            }

            var rule = (issue.rule ?? string.Empty).Trim();
            return !string.Equals(rule, "标点检查", StringComparison.Ordinal)
                && !string.Equals(rule, "公司名检查", StringComparison.Ordinal)
                && !string.Equals(rule, "人名检查", StringComparison.Ordinal)
                && !string.Equals(rule, "断行检查", StringComparison.Ordinal);
        }

        private static string BuildLegacyCommentMessage(RuleIssue issue)
        {
            if (issue == null)
            {
                return string.Empty;
            }

            var text = (issue.message ?? string.Empty).Trim();
            var snippet = (issue.snippet ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                return text + " " + snippet;
            }
            return text;
        }

        private static bool TryParseLegacyLocationIndex(string location, char prefix, out int index)
        {
            index = 0;
            if (string.IsNullOrWhiteSpace(location) || char.ToUpperInvariant(location[0]) != char.ToUpperInvariant(prefix))
            {
                return false;
            }

            return int.TryParse(location.Substring(1), out index) && index > 0;
        }

        private static void AppendLegacyComment(Paragraph paragraph, string text)
        {
            if (paragraph == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                var comment = paragraph.AppendComment(text);
                if (comment != null && comment.Format != null)
                {
                    comment.Format.Author = "智脑科技";
                }
            }
            catch
            {
            }
        }

        private static FileFormat ResolveWordFileFormat(string sourcePath)
        {
            return string.Equals(Path.GetExtension(sourcePath), ".doc", StringComparison.OrdinalIgnoreCase)
                ? FileFormat.Doc
                : FileFormat.Docx;
        }

        private static void WriteLegacySectionFiles(ParseDocumentResponse response, string resultDir, int batchSerial)
        {
            foreach (var section in GetLegacySections())
            {
                var content = GetLegacySectionText(response, section);
                File.WriteAllText(
                    Path.Combine(resultDir, batchSerial + "的" + section + ".txt"),
                    string.IsNullOrWhiteSpace(content) ? "未发现问题。" : content,
                    new UTF8Encoding(false));
            }
        }

        private static void WriteLegacyOverviewFile(string resultDir, string sourceName, int batchSerial, string overviewMode)
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

        private static string WriteReportFile(ParseDocumentResponse response)
        {
            try
            {
                var resultDir = ResolveResultDir(response.filePath);
                Directory.CreateDirectory(resultDir);
                var name = Path.GetFileNameWithoutExtension(response.filePath);
                WriteLegacySourceCopy(response, resultDir);
                var batchSerial = ReadBatchSerial();
                WriteLegacySectionFiles(response, resultDir, batchSerial);
                WriteLegacyOverviewFile(resultDir, name, batchSerial, ReadOverviewMode());
                var reportPath = Path.Combine(resultDir, "检查结果-" + name + "m.txt");
                File.WriteAllText(reportPath, response.reportText ?? string.Empty, new UTF8Encoding(false));
                return reportPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WriteReportDocxFile(ParseDocumentResponse response)
        {
            try
            {
                var resultDir = ResolveResultDir(response.filePath);
                Directory.CreateDirectory(resultDir);
                var name = Path.GetFileNameWithoutExtension(response.filePath);
                var path = Path.Combine(resultDir, "检查结果-" + name + "m.docx");

                var doc = new Document();
                foreach (var sectionName in GetLegacySections())
                {
                    var section = doc.AddSection();
                    section.AddParagraph().AppendText(sectionName);
                    section.AddParagraph().AppendText(string.IsNullOrWhiteSpace(GetLegacySectionText(response, sectionName)) ? "未发现问题。" : GetLegacySectionText(response, sectionName));
                }

                doc.SaveToFile(path, FileFormat.Docx);
                doc.Close();
                return path;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveResultDir(string sourcePath)
        {
            var configured = Environment.GetEnvironmentVariable("AICHECKBID_RESULT_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            var dir = Path.GetDirectoryName(sourcePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, "result");
        }

        private static int ReadBatchSerial()
        {
            var raw = Environment.GetEnvironmentVariable("AICHECKBID_BATCH_SERIAL");
            int value;
            return int.TryParse(raw, out value) && value > 0 ? value : 1;
        }

        private static string ReadOverviewMode()
        {
            var raw = Environment.GetEnvironmentVariable("AICHECKBID_OVERVIEW_MODE");
            return string.IsNullOrWhiteSpace(raw) ? "replace" : raw.Trim();
        }
    }

    internal class ParseDocumentResponse
    {
        public string filePath { get; set; }
        public string fileType { get; set; }
        public string parser { get; set; }
        public int? pageCount { get; set; }
        public List<string> warnings { get; set; }
        public DocumentMetrics metrics { get; set; }
        public List<RuleIssue> issues { get; set; }
        public string reportText { get; set; }
        public string reportPath { get; set; }
        public string reportDocxPath { get; set; }
        public bool outputPageNumber { get; set; }
        public string commentMarker { get; set; }
        public bool smartFixEnabled { get; set; }
        public bool smartFixSaved { get; set; }
        public LegacyWordSections legacySections { get; set; }
    }

    internal class DocumentMetrics
    {
        public int sectionCount { get; set; }
        public int paragraphCount { get; set; }
        public int tableCount { get; set; }
        public PageMarginsCm marginsCm { get; set; }
    }

    internal class PageMarginsCm
    {
        public double top { get; set; }
        public double bottom { get; set; }
        public double left { get; set; }
        public double right { get; set; }
        public double headerDistance { get; set; }
        public double footerDistance { get; set; }
    }

    internal class RuleIssue
    {
        public string category { get; set; }
        public string rule { get; set; }
        public string message { get; set; }
        public string location { get; set; }
        public string currentValue { get; set; }
        public string expectedValue { get; set; }
        public string severity { get; set; }
        public bool @fixed { get; set; }
        public string snippet { get; set; }
    }

    internal class LegacyWordSections
    {
        public string format { get; set; }
        public string company { get; set; }
        public string place { get; set; }
        public string person { get; set; }
        public string sensitive { get; set; }
        public string punctuation { get; set; }
        public string other { get; set; }
    }

    internal class RuleBook
    {
        private readonly Dictionary<string, Dictionary<string, string>> _data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _titleRuleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string loadedPath { get; private set; }

        public static RuleBook Load(string preferredPath, List<string> warnings)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredPath)) candidates.Add(preferredPath);
            var cwd = Directory.GetCurrentDirectory();
            candidates.Add(Path.Combine(cwd, "rules", "set.ini"));
            candidates.Add(Path.Combine(cwd, "set", "set.ini"));

            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var rb = Parse(path);
                    rb.loadedPath = path;
                    rb.LoadTitleRulePresets(path);
                    return rb;
                }
                catch (Exception ex)
                {
                    warnings.Add("规则文件读取失败(" + path + "): " + ex.Message);
                }
            }

            warnings.Add("未找到 set.ini，当前按空规则执行。");
            var empty = new RuleBook();
            empty.LoadTitleRulePresets(preferredPath);
            return empty;
        }

        public string Get(string section, string key)
        {
            Dictionary<string, string> sec;
            string value;
            if (_data.TryGetValue(section, out sec) && sec.TryGetValue(key, out value))
            {
                return value;
            }
            return string.Empty;
        }

        public bool GetBool(string section, string key, bool defaultValue)
        {
            var v = Get(section, key);
            if (string.IsNullOrWhiteSpace(v)) return defaultValue;
            bool b;
            if (bool.TryParse(v, out b)) return b;
            if (v == "1" || v == "是" || v == "True" || v == "TRUE") return true;
            if (v == "0" || v == "否" || v == "False" || v == "FALSE") return false;
            return defaultValue;
        }

        public float GetFloat(string section, string key)
        {
            var v = Get(section, key);
            float f;
            return float.TryParse(v, out f) ? f : 0f;
        }

        public string ResolveTitlePatternLabel(string label)
        {
            string pattern;
            return _titleRuleMap.TryGetValue(label ?? string.Empty, out pattern) ? pattern : string.Empty;
        }

        private static RuleBook Parse(string path)
        {
            var rb = new RuleBook();
            var bytes = File.ReadAllBytes(path);
            string text;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            else
            {
                var utf8 = Encoding.UTF8.GetString(bytes);
                text = utf8.Contains("�") ? Encoding.GetEncoding("GB18030").GetString(bytes) : utf8;
            }

            string section = string.Empty;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimEnd('\r');
                if (line == string.Empty || line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    if (!rb._data.ContainsKey(section)) rb._data[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                var idx = line.IndexOf('=');
                if (idx <= 0 || string.IsNullOrWhiteSpace(section)) continue;
                var key = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();
                rb._data[section][key] = value;
            }

            return rb;
        }

        private void LoadTitleRulePresets(string rulesPath)
        {
            foreach (var path in GetTitlePresetCandidates(rulesPath))
            {
                if (!File.Exists(path)) continue;
                foreach (var raw in ReadTextSmart(path).Split('\n'))
                {
                    var line = raw.Trim().TrimEnd('\r');
                    if (line == string.Empty || line.StartsWith(";") || line.StartsWith("#")) continue;

                    var parts = line.Split(new[] { "*****" }, StringSplitOptions.None);
                    if (parts.Length >= 2)
                    {
                        var label = parts[0].Trim();
                        var pattern = parts[1].Trim();
                        if (label != string.Empty && pattern != string.Empty)
                        {
                            _titleRuleMap[label] = pattern;
                        }
                    }
                }
                if (_titleRuleMap.Count > 0)
                {
                    return;
                }
            }
        }

        private static IEnumerable<string> GetTitlePresetCandidates(string rulesPath)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(rulesPath))
            {
                var dir = Path.GetDirectoryName(rulesPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    list.Add(Path.Combine(dir, "title-presets.txt"));
                }
            }

            var cwd = Directory.GetCurrentDirectory();
            list.Add(Path.Combine(cwd, "rules", "title-presets.txt"));
            list.Add(Path.Combine(cwd, "title-presets.txt"));
            list.Add(Path.Combine(cwd, "..", "rules", "title-presets.txt"));

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            list.Add(Path.Combine(baseDir, "rules", "title-presets.txt"));
            list.Add(Path.Combine(baseDir, "resources", "rules", "title-presets.txt"));

            return list.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string ReadTextSmart(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            var utf8 = Encoding.UTF8.GetString(bytes);
            return utf8.Contains("�") ? Encoding.GetEncoding("GB18030").GetString(bytes) : utf8;
        }
    }
}
