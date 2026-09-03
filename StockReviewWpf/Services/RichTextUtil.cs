using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using RTB = System.Windows.Controls.RichTextBox;

namespace StockReviewWpf.Services;

/// <summary>
/// 心得富文本工具。读取兼容两种格式：旧版 RichTextEditor(wangEditor) 存的 HTML、
/// WPF 版自己存的 RTF；写入仍存 RTF（原生 TextRange，免 HTML 序列化）。
/// HTML 解析按 wangEditor 产出子集实现：p/h1-h6/ul/ol/li/blockquote/div/pre/table、
/// b/strong/i/em/u/s/span/a/font 的 color 与 style(color/background-color/font-size/...)。
/// </summary>
public static class RichTextUtil
{
    public static string ToRtf(RTB rtb)
    {
        var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
        using var ms = new System.IO.MemoryStream();
        range.Save(ms, System.Windows.DataFormats.Rtf);
        return ms.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void LoadInto(RTB rtb, string content)
    {
        rtb.Document.Blocks.Clear();
        if (string.IsNullOrEmpty(content)) return;
        if (content.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            try
            {
                var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
                using var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                range.Load(ms, System.Windows.DataFormats.Rtf);
                return;
            }
            catch
            {
                // RTF 解析失败则按纯文本回退
            }
        }
        if (LooksLikeHtml(content))
        {
            try
            {
                HtmlToFlow(rtb.Document, content);
                return;
            }
            catch
            {
                // HTML 解析失败则按纯文本回退
            }
        }
        rtb.Document.Blocks.Clear();
        rtb.Document.Blocks.Add(new Paragraph(new Run(content)));
    }

    /// <summary>
    /// 只读详情视图排版归一化：正文 15px（编辑器 14px，阅读视图略大更省眼）、
    /// 行距按段落最大字号 ×1.68（标题等大字号段落不会被固定行高裁剪）、
    /// 段间距 8px / 列表项 3px（FlowDocument 默认段边距为 0，正文挤在一起）。
    /// 仅用于展示侧；不影响 ToRtf/ToHtml 序列化（序列化只读取显式值）。
    /// </summary>
    public static void ApplyReaderTypography(RTB rtb, double baseSize = 15)
    {
        rtb.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        rtb.FontSize = baseSize;
        ApplyReaderTypography(rtb.Document.Blocks, baseSize, inListItem: false);
    }

    private static void ApplyReaderTypography(BlockCollection blocks, double baseSize, bool inListItem)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case List list:
                    foreach (var li in list.ListItems) ApplyReaderTypography(li.Blocks, baseSize, inListItem: true);
                    break;
                case Paragraph p:
                    // 行高取段落内最大字号（含段落级标题字号），大字号标题不会被固定行高裁剪
                    var max = baseSize;
                    if (p.FontSize > max) max = p.FontSize;
                    foreach (var r in EnumerateRuns(p.Inlines))
                        if (r.FontSize > max) max = r.FontSize;
                    p.LineHeight = Math.Round(max * 1.68);
                    p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                    // 段边距为零时才补默认段距，保留已有局部边距（如 blockquote 缩进）
                    if (p.Margin.Left == 0 && p.Margin.Top == 0 && p.Margin.Right == 0 && p.Margin.Bottom == 0)
                        p.Margin = inListItem ? new Thickness(0, 3, 0, 3) : new Thickness(0, 8, 0, 8);
                    break;
            }
        }
    }

    private static IEnumerable<Run> EnumerateRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            else if (inline is Span span)
                foreach (var r in EnumerateRuns(span.Inlines)) yield return r;
        }
    }

    /// <summary>
    /// 任意存量内容 → HTML（wangeditor 编辑器加载用）：HTML 原样返回，
    /// RTF（WPF 旧版写入）走 FlowDocument 转换，纯文本包一层 p。
    /// </summary>
    public static string ToHtml(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        if (content.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            try
            {
                var rtb = new RTB();
                LoadInto(rtb, content);
                var sb = new StringBuilder();
                foreach (var block in rtb.Document.Blocks) BlockToHtml(sb, block);
                var html = sb.ToString();
                return html.Length == 0 ? "" : html;
            }
            catch
            {
                return PlainToHtml(content);
            }
        }
        if (LooksLikeHtml(content)) return content;
        return PlainToHtml(content);
    }

    private static string PlainToHtml(string text) =>
        "<p>" + System.Net.WebUtility.HtmlEncode(text) + "</p>";

    // ============ RTF → HTML（仅覆盖 WPF 版写入过的格式子集：B/I/U/删除线/颜色/字号/列表） ============

    private static void BlockToHtml(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case Paragraph p:
                sb.Append("<p>");
                InlinesToHtml(sb, p.Inlines);
                sb.Append("</p>");
                break;
            case List list:
                sb.Append(list.MarkerStyle == TextMarkerStyle.Decimal ? "<ol>" : "<ul>");
                foreach (var li in list.ListItems)
                {
                    sb.Append("<li>");
                    foreach (var b in li.Blocks) BlockToHtml(sb, b);
                    sb.Append("</li>");
                }
                sb.Append(list.MarkerStyle == TextMarkerStyle.Decimal ? "</ol>" : "</ul>");
                break;
            default: // Section 等容器：平铺
                if (block is Section sec)
                    foreach (var b in sec.Blocks) BlockToHtml(sb, b);
                break;
        }
    }

    private static void InlinesToHtml(StringBuilder sb, InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    sb.Append(RunToHtml(run));
                    break;
                case LineBreak:
                    sb.Append("<br/>");
                    break;
                case Span span: // Bold/Italic/Underline/Hyperlink 均派生自 Span
                    var tag = InlineTag(span);
                    if (tag == null) { InlinesToHtml(sb, span.Inlines); break; }
                    sb.Append('<').Append(tag).Append(RunStyle(span)).Append('>');
                    InlinesToHtml(sb, span.Inlines);
                    sb.Append("</").Append(tag).Append('>');
                    break;
            }
        }
    }

    private static string? InlineTag(Span span) => span switch
    {
        Bold => "strong",
        Italic => "em",
        Underline => "u",
        Hyperlink => "a",
        _ => null // 普通 Span：不产生标签，样式由内部 Run 自带
    };

    private static string RunToHtml(Run run)
    {
        var style = RunStyle(run);
        var text = System.Net.WebUtility.HtmlEncode(run.Text);
        return style.Length == 0 ? text : $"<span{style}>{text}</span>";
    }

    /// <summary>行内元素 → style 属性（color/background-color/font-size/删除线），无样式返回空串。</summary>
    private static string RunStyle(Inline el)
    {
        var parts = new List<string>();
        if (el.Foreground is SolidColorBrush fg && fg.Color != Colors.Black)
            parts.Add($"color:{ToCssColor(fg.Color)}");
        if (el.Background is SolidColorBrush bg && bg.Color != Colors.Transparent)
            parts.Add($"background-color:{ToCssColor(bg.Color)}");
        if (el.FontSize > 0 && Math.Abs(el.FontSize - 14) > 0.6) // 14 = 编辑器正文默认
            parts.Add($"font-size:{Math.Round(el.FontSize)}px");
        if (HasStrikethrough(el))
            parts.Add("text-decoration:line-through");
        return parts.Count == 0 ? "" : " style=\"" + string.Join(";", parts) + "\"";
    }

    /// <summary>WPF 无 Strikethrough 元素类型：删除线保存在 TextDecorations 集合里。</summary>
    private static bool HasStrikethrough(Inline el)
    {
        if (el.TextDecorations == null) return false;
        foreach (var d in el.TextDecorations)
            if (d.Location == TextDecorationLocation.Strikethrough) return true;
        return false;
    }

    private static string ToCssColor(Color c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>从 RTF / HTML / 纯文本提取纯文本（用于列表摘要与搜索）。</summary>
    public static string ToPlain(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        if (content.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            try
            {
                var rtb = new RTB();
                LoadInto(rtb, content);
                var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
                return range.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            }
            catch
            {
                return "";
            }
        }
        if (LooksLikeHtml(content)) return HtmlToPlain(content);
        return content.Replace("<br>", " ").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>工具栏 B/I/U 格式化选中文本（shared：bold/italic/underline）。</summary>
    public static void ApplyFormat(RTB rtb, string mode)
    {
        if (rtb.Selection == null || rtb.Selection.IsEmpty) return;
        switch (mode)
        {
            case "bold":
                var curB = rtb.Selection.GetPropertyValue(TextElement.FontWeightProperty);
                rtb.Selection.ApplyPropertyValue(TextElement.FontWeightProperty,
                    curB is FontWeight fwB && fwB == FontWeights.Bold ? FontWeights.Normal : FontWeights.Bold);
                break;
            case "italic":
                var curI = rtb.Selection.GetPropertyValue(TextElement.FontStyleProperty);
                rtb.Selection.ApplyPropertyValue(TextElement.FontStyleProperty,
                    curI is System.Windows.FontStyle fsI && fsI == FontStyles.Italic ? FontStyles.Normal : FontStyles.Italic);
                break;
            case "underline":
                var curU = rtb.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
                var underlined = curU is TextDecorationCollection tcu && tcu.Count > 0;
                rtb.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty,
                    underlined ? null : TextDecorations.Underline);
                break;
        }
    }

    // ============ HTML 读取支持（对齐原版 wangEditor 数据格式） ============

    private static readonly Regex HtmlTagRegex = new(
        @"</?(p|div|span|ul|ol|li|h[1-6]|b|strong|i|em|u|s|strike|del|br|blockquote|font|a|table|tr|td|th|pre|code|hr)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "pre", "table", "tr", "td", "th", "hr" };

    private static bool LooksLikeHtml(string s) => HtmlTagRegex.IsMatch(s);

    private sealed class HNode
    {
        public string Tag = "";          // 小写；文本节点为 ""
        public bool Closing;
        public string Text = "";         // 文本节点（已反转义）
        public bool WsOnly;              // 纯空白文本节点
        public Dictionary<string, string> Attrs = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>行内格式上下文（进入嵌套标签时克隆叠加）。</summary>
    private sealed class Fmt
    {
        public int Bold, Italic, Underline, Strike;
        public Brush? Foreground, Background;
        public double FontSize;          // 0 = 默认
        public string? FontFamily;
    }

    private static Fmt Clone(Fmt f) => new()
    {
        Bold = f.Bold, Italic = f.Italic, Underline = f.Underline, Strike = f.Strike,
        Foreground = f.Foreground, Background = f.Background, FontSize = f.FontSize, FontFamily = f.FontFamily
    };

    private static List<HNode> Tokenize(string html)
    {
        var nodes = new List<HNode>();
        var pos = 0;
        while (pos < html.Length)
        {
            var lt = html.IndexOf('<', pos);
            if (lt < 0) { AddText(nodes, html[pos..]); break; }
            if (lt > pos) AddText(nodes, html[pos..lt]);
            var gt = html.IndexOf('>', lt);
            if (gt < 0) { AddText(nodes, html[lt..]); break; }
            var tag = html[(lt + 1)..gt].Trim();
            pos = gt + 1;
            if (tag.Length == 0 || tag[0] is '!' or '?') continue; // 注释/声明
            var closing = tag.StartsWith('/');
            if (closing) tag = tag[1..].Trim();
            if (tag.EndsWith('/')) tag = tag[..^1].Trim();
            var name = new string(tag.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
            if (name.Length == 0) continue;
            nodes.Add(new HNode { Tag = name, Closing = closing, Attrs = ParseAttrs(tag) });
        }
        return nodes;
    }

    private static void AddText(List<HNode> nodes, string raw)
    {
        if (raw.Length == 0) return;
        var text = System.Net.WebUtility.HtmlDecode(raw).Replace("\r", "").Replace("\n", "").Replace("\t", " ");
        nodes.Add(new HNode { Text = text, WsOnly = text.Trim().Length == 0 });
    }

    private static Dictionary<string, string> ParseAttrs(string tagInner)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(tagInner, @"([\w-]+)\s*=\s*(""[^""]*""|'[^']*'|[^\s>/]+)"))
        {
            var v = m.Groups[2].Value;
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'')) v = v[1..^1];
            attrs[m.Groups[1].Value.ToLowerInvariant()] = System.Net.WebUtility.HtmlDecode(v);
        }
        return attrs;
    }

    private static void HtmlToFlow(FlowDocument doc, string html)
    {
        var nodes = Tokenize(html);
        var i = 0;
        BuildBlocks(b => doc.Blocks.Add(b), nodes, ref i, null, new Fmt());
    }

    /// <summary>生成块级内容并经 add 输出，直到 endTag 的闭合标签（null = 耗尽全部节点）。</summary>
    private static void BuildBlocks(Action<Block> add, List<HNode> nodes, ref int i, string? endTag, Fmt fmt)
    {
        Paragraph? para = null;

        void Flush()
        {
            if (para != null && para.Inlines.Count > 0) add(para);
            para = null;
        }

        while (i < nodes.Count)
        {
            var n = nodes[i];
            if (n.Closing && n.Tag == endTag) { i++; break; }

            // 同级同名开标签自动闭合（li/td/th/tr 等"可选结束标签"）：
            // 正在收集某 li/td 的内容时，遇到下一个同级 li/td 开标签应结束当前块、不消费它，
            // 交还上层（BuildListItems 等）新建同级项——否则 default 分支会把它当嵌套容器递归，
            // 导致后续同级 li 全部嵌套进第一项、列表项错乱（如 7.17 多项 ul/ol 即此问题）。
            if (!n.Closing && n.Tag.Length > 0 && n.Tag == endTag) break;

            // 文本/行内开标签：懒建段落，连续行内内容并入同一段
            if (n.Text.Length > 0 || (n.Tag.Length > 0 && !BlockTags.Contains(n.Tag)))
            {
                if (para == null && n.WsOnly) { i++; continue; }
                para ??= new Paragraph();
                var sawBlock = false;
                BuildInlines(para.Inlines, nodes, ref i, endTag, fmt, ref sawBlock);
                continue;
            }
            if (n.Text.Length == 0 && n.Tag.Length == 0) { i++; continue; }
            if (n.Closing) { i++; continue; } // 无关闭合标签容错

            switch (n.Tag)
            {
                case "ul" or "ol":
                {
                    Flush();
                    var tag = n.Tag;
                    var list = new List { MarkerStyle = tag == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc };
                    i++;
                    BuildListItems(list, nodes, ref i, tag, Clone(fmt));
                    if (list.ListItems.Count > 0) add(list);
                    break;
                }
                case "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre":
                {
                    Flush();
                    var p = new Paragraph();
                    var f = Clone(fmt);
                    ApplyBlockSemantics(p, f, n);
                    var tag = n.Tag;
                    i++;
                    var sawBlock = false;
                    BuildInlines(p.Inlines, nodes, ref i, tag, f, ref sawBlock);
                    if (p.Inlines.Count > 0) add(p);
                    break;
                }
                case "hr":
                    Flush();
                    i++;
                    break;
                default: // div/table/tr/td/li 等纯容器：递归收集子块后平铺
                {
                    Flush();
                    var tag = n.Tag;
                    var f = Clone(fmt);
                    i++;
                    var sink = new List<Block>();
                    BuildBlocks(b => sink.Add(b), nodes, ref i, tag, f);
                    foreach (var b in sink) add(b);
                    break;
                }
            }
        }
        Flush();
    }

    /// <summary>填充列表项，直到 ul/ol 闭合标签。</summary>
    private static void BuildListItems(List list, List<HNode> nodes, ref int i, string listTag, Fmt fmt)
    {
        while (i < nodes.Count)
        {
            var n = nodes[i];
            if (n.Closing && n.Tag == listTag) { i++; return; }
            if (n.Tag == "li" && !n.Closing && n.Text.Length == 0)
            {
                i++;
                var li = new ListItem();
                var sink = new List<Block>();
                BuildBlocks(b => sink.Add(b), nodes, ref i, "li", Clone(fmt));
                foreach (var b in sink) li.Blocks.Add(b);
                if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph());
                list.ListItems.Add(li);
            }
            else i++;
        }
    }

    /// <summary>向 target 追加行内内容，直到 endTag 闭合或遇到块级节点（块级节点不消费，交还上层）。</summary>
    private static void BuildInlines(InlineCollection target, List<HNode> nodes, ref int i, string? endTag, Fmt fmt, ref bool sawBlock)
    {
        while (i < nodes.Count)
        {
            var n = nodes[i];
            if (n.Closing && n.Tag == endTag) { i++; return; }
            if (n.Text.Length > 0)
            {
                i++;
                target.Add(n.WsOnly ? new Run(" ") : MakeRun(n.Text, fmt));
                continue;
            }
            if (n.Tag.Length == 0) { i++; continue; }
            if (n.Closing)
            {
                if (BlockTags.Contains(n.Tag)) { sawBlock = true; return; } // 上层块闭合，不消费
                i++; // 无关行内闭合标签
                continue;
            }
            if (BlockTags.Contains(n.Tag) && n.Tag != "br") { sawBlock = true; return; }

            switch (n.Tag)
            {
                case "br":
                    target.Add(new LineBreak());
                    i++;
                    break;
                case "b" or "strong":
                {
                    var f = Clone(fmt); f.Bold++;
                    var tag = n.Tag; // 记录真实标签名以匹配对应闭合
                    i++;
                    BuildInlines(target, nodes, ref i, tag, f, ref sawBlock);
                    if (sawBlock) return;
                    break;
                }
                case "i" or "em":
                {
                    var f = Clone(fmt); f.Italic++;
                    var tag = n.Tag;
                    i++;
                    BuildInlines(target, nodes, ref i, tag, f, ref sawBlock);
                    if (sawBlock) return;
                    break;
                }
                case "u":
                {
                    var f = Clone(fmt); f.Underline++;
                    i++;
                    BuildInlines(target, nodes, ref i, "u", f, ref sawBlock);
                    if (sawBlock) return;
                    break;
                }
                case "s" or "strike" or "del":
                {
                    var f = Clone(fmt); f.Strike++;
                    var tag = n.Tag;
                    i++;
                    BuildInlines(target, nodes, ref i, tag, f, ref sawBlock);
                    if (sawBlock) return;
                    break;
                }
                default: // span/font/a/code
                {
                    var f = Clone(fmt);
                    ApplyInlineSemantics(f, n);
                    var tag = n.Tag;
                    i++;
                    BuildInlines(target, nodes, ref i, tag, f, ref sawBlock);
                    if (sawBlock) return;
                    break;
                }
            }
        }
    }

    /// <summary>块级标签语义：标题字号/加粗、引用缩进斜体、pre 等宽字体。</summary>
    private static void ApplyBlockSemantics(Paragraph p, Fmt f, HNode n)
    {
        switch (n.Tag)
        {
            case "h1": f.Bold++; p.FontSize = 24; break;
            case "h2": f.Bold++; p.FontSize = 22; break;
            case "h3": f.Bold++; p.FontSize = 20; break;
            case "h4": f.Bold++; p.FontSize = 18; break;
            case "h5": case "h6": f.Bold++; p.FontSize = 16; break;
            case "blockquote":
                f.Italic++;
                p.Margin = new Thickness(12, 4, 12, 4);
                break;
            case "pre": f.FontFamily = "Consolas"; break;
        }
        if (n.Attrs.TryGetValue("style", out var style)) ApplyStyle(f, style);
    }

    private static void ApplyInlineSemantics(Fmt f, HNode n)
    {
        if (n.Tag == "a") f.Underline++;
        if (n.Tag == "code") f.FontFamily = "Consolas";
        if (n.Tag == "font" && n.Attrs.TryGetValue("color", out var c))
            f.Foreground ??= ParseColor(c);
        if (n.Attrs.TryGetValue("style", out var style)) ApplyStyle(f, style);
    }

    private static void ApplyStyle(Fmt f, string style)
    {
        foreach (var kv in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = kv.IndexOf(':');
            if (idx < 0) continue;
            var key = kv[..idx].Trim().ToLowerInvariant();
            var val = kv[(idx + 1)..].Trim();
            try
            {
                switch (key)
                {
                    case "color":
                        f.Foreground = ParseColor(val);
                        break;
                    case "background-color":
                        f.Background = ParseColor(val);
                        break;
                    case "font-size":
                        if (val.EndsWith("px") && double.TryParse(val[..^2], out var px) && px > 0) f.FontSize = px;
                        break;
                    case "font-weight":
                        if (val is "bold" or "bolder" || (int.TryParse(val, out var w) && w >= 600)) f.Bold++;
                        break;
                    case "font-style":
                        if (val == "italic") f.Italic++;
                        break;
                    case "text-decoration":
                        if (val.Contains("underline")) f.Underline++;
                        if (val.Contains("line-through")) f.Strike++;
                        break;
                }
            }
            catch { /* 忽略非法样式值 */ }
        }
    }

    private static Brush? ParseColor(string val)
    {
        try
        {
            if (val.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                var nums = Regex.Matches(val, @"\d+").Select(m => byte.Parse(m.Value)).ToArray();
                if (nums.Length >= 3)
                {
                    var b = new SolidColorBrush(Color.FromRgb(nums[0], nums[1], nums[2]));
                    b.Freeze();
                    return b;
                }
                return null;
            }
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(val));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private static Run MakeRun(string text, Fmt f)
    {
        var run = new Run(text);
        if (f.Bold > 0) run.FontWeight = FontWeights.Bold;
        if (f.Italic > 0) run.FontStyle = FontStyles.Italic;
        if (f.Underline > 0 || f.Strike > 0)
        {
            var decos = new TextDecorationCollection();
            if (f.Underline > 0) decos.Add(TextDecorations.Underline[0]);
            if (f.Strike > 0) decos.Add(TextDecorations.Strikethrough[0]);
            run.TextDecorations = decos;
        }
        if (f.Foreground != null) run.Foreground = f.Foreground;
        if (f.Background != null) run.Background = f.Background;
        if (f.FontSize > 0) run.FontSize = f.FontSize;
        if (f.FontFamily != null) run.FontFamily = new FontFamily(f.FontFamily);
        return run;
    }

    private static string HtmlToPlain(string html)
    {
        var sb = new StringBuilder();
        foreach (var n in Tokenize(html))
        {
            if (n.Text.Length > 0) sb.Append(n.WsOnly ? " " : n.Text);
            else if (!n.Closing && (n.Tag == "br" || BlockTags.Contains(n.Tag))) sb.Append(' ');
        }
        return sb.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
