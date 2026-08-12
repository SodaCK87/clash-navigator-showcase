using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using ClashNavigatorAddin.Services;
using Xunit;

namespace ClashNavigatorAddin.Tests;

/// <summary>xlsx 寫入器的結構層測試。
///
/// **這些測試證明的是「我們寫出去的位元組長什麼樣」，不是「Excel 接受它」**——後者只有
/// Excel 本身答得出來，判準在 <c>Tools\XlsxExportProbe</c>（Excel COM）。兩層各守一半：
/// 這裡守得住重構（改了寫入器立刻紅），探針守得住格式（Excel 靜默修復時才看得出來）。</summary>
public class XlsxWorkbookTests
{
    private static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static ZipArchive WriteAndOpen(params XlsxSheet[] sheets)
    {
        var stream = new MemoryStream();
        XlsxWorkbook.Write(stream, sheets);
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static XDocument Read(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        Assert.NotNull(entry);
        using var entryStream = entry!.Open();
        return XDocument.Load(entryStream);
    }

    private static XlsxSheet SingleCellSheet(XlsxCell cell)
    {
        var sheet = new XlsxSheet("測試");
        var row = new XlsxRow();
        row.Cells.Add(cell);
        sheet.Rows.Add(row);
        return sheet;
    }

    [Fact]
    public void Write_ProducesTheRequiredPackageParts()
    {
        using var archive = WriteAndOpen(new XlsxSheet("A"), new XlsxSheet("B"));
        var names = archive.Entries.Select(e => e.FullName).ToList();

        // 少任何一份，Excel 都會判定檔案損毀。
        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("xl/workbook.xml", names);
        Assert.Contains("xl/_rels/workbook.xml.rels", names);
        Assert.Contains("xl/styles.xml", names);
        Assert.Contains("xl/worksheets/sheet1.xml", names);
        Assert.Contains("xl/worksheets/sheet2.xml", names);
    }

    /// <summary>沒有圖片的工作表不該產生 drawing 相關的組件——多餘的 Override 會讓
    /// Content-Type 指向不存在的部分。</summary>
    [Fact]
    public void Write_SheetWithoutImages_HasNoDrawingParts()
    {
        using var archive = WriteAndOpen(new XlsxSheet("A"));
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("drawing"));
    }

    [Fact]
    public void Write_ImageSheet_EmbedsMediaAndDrawingAndRelationship()
    {
        var sheet = new XlsxSheet("圖");
        sheet.Rows.Add(new XlsxRow());
        sheet.Images.Add(new XlsxImage(new byte[] { 1, 2, 3 }, "png", 0, 1, 400, 300));

        using var archive = WriteAndOpen(sheet);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("xl/media/image1_1.png", names);
        Assert.Contains("xl/drawings/drawing1.xml", names);
        Assert.Contains("xl/drawings/_rels/drawing1.xml.rels", names);
        Assert.Contains("xl/worksheets/_rels/sheet1.xml.rels", names);

        // 圖片的副檔名要登記成 Content-Type 的 Default，否則 Excel 不知道那份 media 是什麼。
        var contentTypes = Read(archive, "[Content_Types].xml").ToString();
        Assert.Contains("image/png", contentTypes, StringComparison.Ordinal);
    }

    /// <summary>錨定位置與尺寸要進到 drawing。EMU 換算錯了圖片會變形或跑位，
    /// 而那在 XML 層看不出來、只在 Excel 裡現形——所以這裡釘住數字。</summary>
    [Fact]
    public void Write_Image_AnchorsAtGivenCellWithPixelSizeInEmu()
    {
        var sheet = new XlsxSheet("圖");
        sheet.Rows.Add(new XlsxRow());
        sheet.Images.Add(new XlsxImage(new byte[] { 1 }, "jpg", rowIndex: 3, columnIndex: 2,
            widthPixels: 400, heightPixels: 300));

        using var archive = WriteAndOpen(sheet);
        var drawing = Read(archive, "xl/drawings/drawing1.xml");

        XNamespace xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        var from = drawing.Descendants(xdr + "from").Single();
        Assert.Equal("2", from.Element(xdr + "col")!.Value);
        Assert.Equal("3", from.Element(xdr + "row")!.Value);

        var ext = drawing.Descendants(xdr + "ext").Single();
        Assert.Equal((400 * 9525).ToString(CultureInfo.InvariantCulture), ext.Attribute("cx")!.Value);
        Assert.Equal((300 * 9525).ToString(CultureInfo.InvariantCulture), ext.Attribute("cy")!.Value);
    }

    [Fact]
    public void Write_TextCell_IsInlineString()
    {
        using var archive = WriteAndOpen(SingleCellSheet(XlsxCell.FromText("管道")));
        var cell = Read(archive, "xl/worksheets/sheet1.xml").Descendants(Main + "c").Single();

        Assert.Equal("inlineStr", cell.Attribute("t")!.Value);
        Assert.Equal("管道", cell.Descendants(Main + "t").Single().Value);
    }

    /// <summary>數字要寫成數值（沒有 t 屬性、只有 v）——這是 Excel 匯出相對 CSV 的實質差異：
    /// 文字排序會得到 1 &lt; 10 &lt; 2。</summary>
    [Fact]
    public void Write_NumberCell_HasNoTypeAttributeAndKeepsValue()
    {
        using var archive = WriteAndOpen(SingleCellSheet(XlsxCell.FromNumber(1234.56)));
        var cell = Read(archive, "xl/worksheets/sheet1.xml").Descendants(Main + "c").Single();

        Assert.Null(cell.Attribute("t"));
        Assert.Equal("1234.56", cell.Element(Main + "v")!.Value);
    }

    /// <summary>Excel 的日期序號自 1899-12-30 起算（吸收掉 Lotus 1-2-3 的 1900 閏年錯誤）。
    /// 差一天不會讓檔案打不開，只會讓報告上的日期全錯——正是沒有測試就發現不了的那種。</summary>
    [Fact]
    public void Write_DateCell_UsesExcelSerialFrom1899()
    {
        using var archive = WriteAndOpen(SingleCellSheet(XlsxCell.FromDate(new DateTime(2026, 8, 7))));
        var value = Read(archive, "xl/worksheets/sheet1.xml").Descendants(Main + "v").Single().Value;

        Assert.Equal(
            (new DateTime(2026, 8, 7) - new DateTime(1899, 12, 30)).TotalDays.ToString("0.####", CultureInfo.InvariantCulture),
            value);
    }

    /// <summary>de-DE 的小數點是逗號。XML 裡出現「1234,56」會讓 Excel 判定檔案損毀，
    /// 與 CSV 那條路的欄位錯位是同一族的坑。</summary>
    [Fact]
    public void Write_NumberCell_UsesInvariantDecimalPoint_UnderCommaDecimalCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            using var archive = WriteAndOpen(SingleCellSheet(XlsxCell.FromNumber(1234.56)));
            var value = Read(archive, "xl/worksheets/sheet1.xml").Descendants(Main + "v").Single().Value;

            Assert.Equal("1234.56", value);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    /// <summary>備註是自由輸入、還可能來自別台電腦的工作包。夾帶一個 0x00 就足以讓
    /// XmlWriter 擲例外而使整份匯出失敗——剔除而非中止。</summary>
    [Fact]
    public void Write_TextWithControlCharacters_IsSanitizedInsteadOfThrowing()
    {
        // 以 (char) 組出控制字元，不寫成字面值：把原始位元組寫進 .cs 會讓這個檔被 grep
        // 之類的工具判成 binary（實測過），而逸出序列在部分編輯路徑上會被還原成原始位元組。
        var payload = "好" + (char)0x00 + (char)0x08 + "壞";
        using var archive = WriteAndOpen(SingleCellSheet(XlsxCell.FromText(payload)));
        var text = Read(archive, "xl/worksheets/sheet1.xml").Descendants(Main + "t").Single().Value;

        Assert.Equal("好壞", text);
    }

    /// <summary>Tab 與換行是合法的 XML 字元，不可一起剔掉——備註的換行是使用者的內容。</summary>
    [Fact]
    public void Write_TextWithNewline_KeepsIt()
    {
        using var archive = WriteAndOpen(SingleCellSheet(XlsxCell.FromText("第一行\n第二行")));
        var text = Read(archive, "xl/worksheets/sheet1.xml").Descendants(Main + "t").Single().Value;

        Assert.Equal("第一行\n第二行", text);
    }

    /// <summary>xlsx 的 inlineStr 不可能被當成公式（公式要另外寫 &lt;f&gt;），所以**不該**
    /// 沿用 CSV 那條路的單引號前綴——加了只會讓使用者的資料多一個引號。
    /// 這道測試擋的是「把 CsvEscape 順手抄過來」的過度修正。</summary>
    [Fact]
    public void Write_FormulaLikeText_IsNotPrefixedAndIsNotAFormula()
    {
        using var archive = WriteAndOpen(SingleCellSheet(XlsxCell.FromText("=1+1")));
        var sheet = Read(archive, "xl/worksheets/sheet1.xml");

        Assert.Equal("=1+1", sheet.Descendants(Main + "t").Single().Value);
        Assert.Empty(sheet.Descendants(Main + "f"));
    }

    /// <summary>Excel 對工作表名稱有硬性限制（31 字元、不接受 <c>[ ] : * ? / \</c>），
    /// 違反時是「檔案損毀」而不是溫和忽略。</summary>
    [Theory]
    [InlineData("正常名稱", "正常名稱")]
    [InlineData("有/斜線", "有_斜線")]
    [InlineData("[中括號]:冒號*星號?問號", "_中括號__冒號_星號_問號")]
    public void SheetName_IllegalCharacters_AreReplaced(string input, string expected)
    {
        Assert.Equal(expected, new XlsxSheet(input).Name);
    }

    [Fact]
    public void SheetName_LongerThan31Characters_IsTruncated()
    {
        var name = new XlsxSheet(new string('長', 40)).Name;
        Assert.Equal(31, name.Length);
    }

    [Fact]
    public void SheetName_Blank_FallsBackToDefault()
    {
        Assert.Equal("Sheet", new XlsxSheet("   ").Name);
    }

    /// <summary>凍結窗格與自動篩選是「被 Excel 修復就會消失」的東西，故各自釘住。</summary>
    [Fact]
    public void Write_FreezeAndAutoFilter_AreEmitted()
    {
        var sheet = new XlsxSheet("A") { FreezeHeaderRow = true, AutoFilter = true };
        var row = new XlsxRow();
        row.Cells.Add(XlsxCell.FromText("標題", XlsxStyle.Header));
        sheet.Rows.Add(row);

        using var archive = WriteAndOpen(sheet);
        var worksheet = Read(archive, "xl/worksheets/sheet1.xml");

        Assert.Equal("frozen", worksheet.Descendants(Main + "pane").Single().Attribute("state")!.Value);
        Assert.Equal("A1:A1", worksheet.Descendants(Main + "autoFilter").Single().Attribute("ref")!.Value);
    }

    /// <summary>worksheet 的子元素順序由 schema 規定，錯了 Excel 會要求修復。
    /// 這是最容易在重構時打破、又最不容易發現的一條。</summary>
    [Fact]
    public void Write_WorksheetChildren_FollowSchemaOrder()
    {
        var sheet = new XlsxSheet("A") { FreezeHeaderRow = true, AutoFilter = true };
        sheet.Columns.Add(new XlsxColumn { WidthChars = 10 });
        var row = new XlsxRow();
        row.Cells.Add(XlsxCell.FromText("標題"));
        sheet.Rows.Add(row);
        sheet.Images.Add(new XlsxImage(new byte[] { 1 }, "png", 0, 0, 10, 10));

        using var archive = WriteAndOpen(sheet);
        var order = Read(archive, "xl/worksheets/sheet1.xml").Root!
            .Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal(
            new[] { "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "autoFilter", "drawing" },
            order);
    }

    [Theory]
    [InlineData(0, 0, "A1")]
    [InlineData(0, 25, "Z1")]
    [InlineData(1, 26, "AA2")]
    [InlineData(9, 27, "AB10")]
    [InlineData(0, 701, "ZZ1")]
    [InlineData(0, 702, "AAA1")]
    public void CellReference_MapsIndicesToA1Notation(int row, int column, string expected)
    {
        Assert.Equal(expected, XlsxWorkbook.CellReference(row, column));
    }

    [Fact]
    public void Write_NoSheets_Throws()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => XlsxWorkbook.Write(stream, Array.Empty<XlsxSheet>()));
    }

    /// <summary>名稱重複的工作表會讓 Excel 判定檔案損毀，而修復的結果是樣式與繪圖整批被丟掉
    /// ——**寫得出來、開得起來、內容不對**，正是這個寫入器最該擋在門口的那一類。
    /// 比對不分大小寫，Excel 的工作表名稱唯一性就是不分大小寫的。</summary>
    [Theory]
    [InlineData("資料", "資料")]
    [InlineData("Data", "DATA")]
    public void Write_DuplicateSheetNames_Throws(string first, string second)
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() =>
            XlsxWorkbook.Write(stream, new[] { new XlsxSheet(first), new XlsxSheet(second) }));
    }

    /// <summary>衝突可能是**清理造出來的**：兩個不同的原字串各自被截成 31 字元後撞在一起。
    /// 所以唯一性要比對清理後的 <c>Name</c>，不是呼叫端給的原字串。</summary>
    [Fact]
    public void Write_NamesCollidingOnlyAfterSanitizing_Throws()
    {
        var prefix = new string('長', 31);
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentException>(() =>
            XlsxWorkbook.Write(stream, new[] { new XlsxSheet(prefix + "A"), new XlsxSheet(prefix + "B") }));
    }

    /// <summary>styles.xml 的 fills 前兩項是 OOXML 的硬性規定（none、gray125）；
    /// 少了會讓 Excel 判定需要修復，而修復的結果是所有樣式被丟掉。</summary>
    [Fact]
    public void Write_Styles_StartWithNoneAndGray125Fills()
    {
        using var archive = WriteAndOpen(new XlsxSheet("A"));
        var patterns = Read(archive, "xl/styles.xml")
            .Descendants(Main + "patternFill")
            .Select(e => e.Attribute("patternType")!.Value)
            .ToList();

        Assert.Equal("none", patterns[0]);
        Assert.Equal("gray125", patterns[1]);
    }

    /// <summary>UTF-8 且**不得有 BOM**：OOXML 的 XML 組件加了 BOM 部分解析器會拒收
    /// （與 CSV 匯出刻意加 BOM 的理由正好相反，那是為了讓 Excel 認得編碼）。</summary>
    [Fact]
    public void Write_XmlParts_HaveNoByteOrderMark()
    {
        using var archive = WriteAndOpen(new XlsxSheet("A"));
        using var entryStream = archive.GetEntry("xl/workbook.xml")!.Open();
        using var reader = new BinaryReader(entryStream, Encoding.UTF8);

        var head = reader.ReadBytes(3);
        Assert.False(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF);
    }
}
