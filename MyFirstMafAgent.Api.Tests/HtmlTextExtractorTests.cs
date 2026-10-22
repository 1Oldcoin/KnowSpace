using System.Text;
using MyFirstMafAgent.Api.Documents;

namespace MyFirstMafAgent.Api.Tests;

public sealed class HtmlTextExtractorTests
{
    [Fact]
    public void ExtractText_RemovesScriptStyleAndOtherNonContentBlocks()
    {
        const string html = """
            <html><head><title>页面标题</title><style>body { color: red; }</style>
            <script>var a = 1 < 2;</script></head>
            <body><!-- 隐藏注释 -->
            <p>正文内容</p>
            <noscript>无脚本提示</noscript><iframe src="frame.html"></iframe><svg><text>图形文本</text></svg>
            </body></html>
            """;

        var text = HtmlTextExtractor.ExtractText(html);

        Assert.Equal("正文内容", text);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("var a", text);
        Assert.DoesNotContain("页面标题", text);
        Assert.DoesNotContain("隐藏注释", text);
        Assert.DoesNotContain("无脚本提示", text);
        Assert.DoesNotContain("图形文本", text);
    }

    [Fact]
    public void ExtractText_RemovesUnterminatedScriptBlock()
    {
        var text = HtmlTextExtractor.ExtractText("<p>正文</p><script>var leaked = 'x';");

        Assert.Equal("正文", text);
        Assert.DoesNotContain("leaked", text);
    }

    [Fact]
    public void ExtractText_DecodesNamedNumericAndHexEntities()
    {
        const string html = "<p>&amp; &lt;b&gt; &quot;双引号&quot; &#39;单引号&#39; &#20013;&#x6587; &nbsp;尾</p>";

        var text = HtmlTextExtractor.ExtractText(html);

        Assert.Equal("& <b> \"双引号\" '单引号' 中文 尾", text);
    }

    [Fact]
    public void ExtractText_RendersHeadingLevelsAndListItems()
    {
        const string html = """
            <h1>一级标题</h1><h2>二级标题</h2><h3>三级标题</h3>
            <ul><li>条目一</li><li>条目 <strong>二</strong></li></ul>
            <ol><li>数字一</li></ol>
            """;

        var text = HtmlTextExtractor.ExtractText(html);

        Assert.Equal(
            "# 一级标题\n\n## 二级标题\n\n### 三级标题\n\n- 条目一\n\n- 条目 二\n\n- 数字一",
            text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ExtractText_JoinsTableCellsWithPipeAndSeparatesRows()
    {
        const string html = """
            <table>
              <tr><th>姓名</th><th>年龄</th></tr>
              <tr><td>张三</td><td>30</td></tr>
            </table>
            """;

        var text = HtmlTextExtractor.ExtractText(html);

        Assert.Equal("姓名 | 年龄\n\n张三 | 30", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ExtractText_SeparatesBlockElementsAndBreaks()
    {
        const string html = "<div>块一</div><div>块二</div><p>第一行<br>第二行</p>";

        var text = HtmlTextExtractor.ExtractText(html);

        Assert.Equal("块一\n\n块二\n\n第一行\n第二行", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ExtractText_CollapsesWhitespaceAndBlankLines()
    {
        var html = "<p>a    b\u00a0\u00a0\u00a0c</p><p> </p><p> </p><div> </div><p>d<br><br><br><br>e</p>";

        var text = HtmlTextExtractor.ExtractText(html).Replace("\r\n", "\n");

        Assert.Equal("a b c\n\nd\n\ne", text);
        Assert.DoesNotContain("\n\n\n", text);
        Assert.DoesNotContain(" \n", text);
    }

    [Fact]
    public void ExtractText_ReturnsEmptyStringForEmptyOrTextFreeInput()
    {
        Assert.Equal(string.Empty, HtmlTextExtractor.ExtractText(string.Empty));
        Assert.Equal(string.Empty, HtmlTextExtractor.ExtractText(null!));
        Assert.Equal(string.Empty, HtmlTextExtractor.ExtractText("  \r\n\t  "));
        Assert.Equal(string.Empty, HtmlTextExtractor.ExtractText("<html><body><script>x</script><style>y</style></body></html>"));
        Assert.Equal(string.Empty, HtmlTextExtractor.ExtractText("<p> </p><div> </div>"));
    }

    [Fact]
    public void DecodeHtml_FallsBackToGb18030WhenBytesAreNotValidUtf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var html = "<html><body><h1>知识库</h1><p>这是 GB18030 编码的页面。</p></body></html>";
        var bytes = Encoding.GetEncoding("GB18030").GetBytes(html);
        // 先确认这串字节确实不是合法 UTF-8，回退分支才会被真正覆盖。
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(bytes));

        var decoded = HtmlTextExtractor.DecodeHtml(bytes);

        Assert.Equal(html, decoded);
        Assert.Equal("# 知识库\n\n这是 GB18030 编码的页面。", HtmlTextExtractor.ExtractText(decoded).Replace("\r\n", "\n"));
    }

    [Fact]
    public void DecodeHtml_HonoursMetaCharsetBeforeUtf8Fallback()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string html = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=gbk\"></head>"
                            + "<body><p>元信息声明 GBK 的页面</p></body></html>";

        var decoded = HtmlTextExtractor.DecodeHtml(Encoding.GetEncoding("GB18030").GetBytes(html));

        Assert.Equal(html, decoded);
        Assert.Equal("元信息声明 GBK 的页面", HtmlTextExtractor.ExtractText(decoded));
    }

    [Fact]
    public void DecodeHtml_PrefersByteOrderMarkAndDecodesUtf8ByDefault()
    {
        const string html = "<html><body><p>中文正文</p></body></html>";
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(html)).ToArray();

        Assert.Equal(html, HtmlTextExtractor.DecodeHtml(withBom));
        Assert.Equal(html, HtmlTextExtractor.DecodeHtml(Encoding.UTF8.GetBytes(html)));
        Assert.Equal(string.Empty, HtmlTextExtractor.DecodeHtml([]));
    }

    [Fact]
    public async Task ExtractTextFromFileAsync_ReadsGb18030HtmlFile()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var directory = Path.Combine(Path.GetTempPath(), "MyFirstMafAgent.Api.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "page.html");
        var bytes = Encoding.GetEncoding("GB18030").GetBytes(
            "<html><body><h2>本地知识库</h2><p>支持 HTML 上传。</p></body></html>");
        await File.WriteAllBytesAsync(path, bytes);

        try
        {
            var text = await HtmlTextExtractor.ExtractTextFromFileAsync(path, CancellationToken.None);

            Assert.Equal("## 本地知识库\n\n支持 HTML 上传。", text.Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExtractText_FlattensNestedMarkupInsideCellsAndParagraphs()
    {
        var text = HtmlTextExtractor.ExtractText(
            "<article><section><p>段落 <a href=\"/x\">链接</a> 文字</p></section></article>");

        Assert.Equal("段落 链接 文字", text);
        Assert.DoesNotContain("<", text);
    }
}
