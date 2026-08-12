using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ClashNavigatorAddin.Services;

/// <summary>儲存格的樣式，對應 <c>styles.xml</c> 的 <c>cellXfs</c> 索引（見 <see cref="XlsxWorkbook"/>）。</summary>
internal enum XlsxStyle
{
    Default = 0,
    Header = 1,
    /// <summary>小數（0.##）——數值欄要在 Excel 裡真的是數字才排得動。</summary>
    Decimal = 2,
    /// <summary>日期時間（yyyy/mm/dd hh:mm）。</summary>
    DateTime = 3,
    /// <summary>自動換行、靠上對齊，供描述／備註這類長文字欄使用。</summary>
    WrapTop = 4,
    /// <summary>水平置中、垂直置中，供圖片對照表的編號欄使用。</summary>
    Centered = 5
}

/// <summary>一個儲存格。文字、數字、日期三選一——**數字與日期一律寫成真正的數值型別**，
/// 這正是 Excel 匯出相對於 CSV 的實質差異：CSV 全部是文字，Excel 排序「嚴重度」時
/// 會得到 1 &lt; 10 &lt; 2 的字典序，而這裡不會。</summary>
internal sealed class XlsxCell
{
    private XlsxCell(string? text, double number, bool isNumeric, XlsxStyle style)
    {
        Text = text;
        Number = number;
        IsNumeric = isNumeric;
        Style = style;
    }

    public string? Text { get; }
    public double Number { get; }
    public bool IsNumeric { get; }
    public XlsxStyle Style { get; }

    public static XlsxCell Empty { get; } = new(null, 0, false, XlsxStyle.Default);

    public static XlsxCell FromText(string? text, XlsxStyle style = XlsxStyle.Default) =>
        string.IsNullOrEmpty(text) ? new XlsxCell(null, 0, false, style) : new XlsxCell(text, 0, false, style);

    public static XlsxCell FromNumber(double value, XlsxStyle style = XlsxStyle.Decimal) =>
        new(null, value, true, style);

    /// <summary>日期以 Excel 的序號（1899-12-30 起算的天數）寫入。
    /// 基準日是 1899-12-30 而非 1900-01-01——Excel 沿襲 Lotus 1-2-3 把 1900 誤當閏年，
    /// 差的那兩天就吸收在基準日裡。</summary>
    public static XlsxCell FromDate(DateTime value, XlsxStyle style = XlsxStyle.DateTime) =>
        new(null, (value - new DateTime(1899, 12, 30)).TotalDays, true, style);
}

internal sealed class XlsxRow
{
    public List<XlsxCell> Cells { get; } = new();

    /// <summary>列高（點）。null＝沿用預設。圖片列要靠這個把列撐到圖片的高度。</summary>
    public double? HeightPoints { get; set; }
}

internal sealed class XlsxColumn
{
    /// <summary>欄寬，單位是「字元數」（Excel 的原生單位，非像素）。</summary>
    public double WidthChars { get; set; }

    /// <summary>由像素換算欄寬。Excel 的字元寬度以預設字型的 '0' 為準，
    /// 11pt Calibri 下約為 7px，另加 5px 的儲存格內距。</summary>
    public static XlsxColumn FromPixels(double pixels) =>
        new() { WidthChars = Math.Round((pixels - 5) / 7.0, 2) };
}

/// <summary>錨定在某個儲存格左上角的圖片。
///
/// **xlsx 的圖片是「浮在儲存格上」的繪圖物件，不是儲存格的值**——排序／篩選時圖片不會跟著列走。
/// 這是格式本身的限制，不是這個寫入器的取捨：真正「在儲存格內」的圖片是 Excel 365 才有的
/// richValue，Excel 2019／2021 開起來會變成 #VALUE!，不適合發給同事。
/// 因此呼叫端的對策是把圖片放在**獨立的工作表**，讓可排序的資料表不含圖片。</summary>
internal sealed class XlsxImage
{
    public XlsxImage(byte[] data, string extension, int rowIndex, int columnIndex, int widthPixels, int heightPixels)
    {
        Data = data;
        Extension = extension;
        RowIndex = rowIndex;
        ColumnIndex = columnIndex;
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
    }

    public byte[] Data { get; }

    /// <summary>不含點號的副檔名（jpg／png），決定 <c>[Content_Types].xml</c> 的 Default 項目。</summary>
    public string Extension { get; }

    /// <summary>錨定的列（0 起算，含標題列）。</summary>
    public int RowIndex { get; }

    /// <summary>錨定的欄（0 起算）。</summary>
    public int ColumnIndex { get; }

    public int WidthPixels { get; }
    public int HeightPixels { get; }
}

internal sealed class XlsxSheet
{
    public XlsxSheet(string name) => Name = SanitizeSheetName(name);

    public string Name { get; }
    public List<XlsxColumn> Columns { get; } = new();
    public List<XlsxRow> Rows { get; } = new();
    public List<XlsxImage> Images { get; } = new();

    /// <summary>凍結第一列（標題列）。</summary>
    public bool FreezeHeaderRow { get; set; }

    /// <summary>對整個資料範圍套用自動篩選。</summary>
    public bool AutoFilter { get; set; }

    /// <summary>工作表名稱的合法化：Excel 限 31 字元且不接受 <c>[ ] : * ? / \</c>。
    /// 違反時 Excel 會判定檔案損毀並要求修復，而不是溫和地忽略。
    ///
    /// **唯一性不在這裡處理**：清理是逐張各自進行的，看不到別張叫什麼，
    /// 而截斷與字元替換本身就可能製造出重複。整份的唯一性由
    /// <see cref="XlsxWorkbook.Write(Stream, IReadOnlyList{XlsxSheet})"/> 把關。</summary>
    private static string SanitizeSheetName(string name)
    {
        var text = new StringBuilder();
        foreach (char c in name)
            text.Append(c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? '_' : c);

        var result = text.ToString().Trim();
        if (result.Length == 0)
            result = "Sheet";

        return result.Length > 31 ? result.Substring(0, 31) : result;
    }
}

/// <summary>零相依的最小 xlsx（SpreadsheetML）寫入器：一個 zip 加上幾份 XML。
///
/// **不引 ClosedXML／OpenXML SDK 的理由**：那條路要往 bundle 塞入六到八個 DLL，而外掛與其他
/// 廠商的外掛共用同一個 Revit 行程與 AppDomain，版本不一致時的衝突症狀極難查；本專案的
/// csproj 也是刻意釘死版本、相依最小化的。這裡需要的功能面很窄（兩張表、幾種樣式、錨定圖片），
/// 自己寫的代價低於背一整個函式庫的風險。
///
/// 產出的檔案已用 **Excel COM** 實測開啟（不是 LibreOffice——消費端是 Excel）。</summary>
internal static class XlsxWorkbook
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string SpreadsheetDrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    /// <summary>1 像素（96 dpi）等於多少 EMU（English Metric Unit）——繪圖物件的座標單位。</summary>
    private const int EmuPerPixel = 9525;

    public static void Write(string filePath, IReadOnlyList<XlsxSheet> sheets)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(stream, sheets);
    }

    /// <summary>寫入任意串流——測試以 MemoryStream 產出後直接檢查 zip 內容，不必落地。</summary>
    public static void Write(Stream stream, IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 0)
            throw new ArgumentException("活頁簿至少要有一張工作表。", nameof(sheets));

        // 名稱重複的工作表會讓 Excel 判定檔案損毀（**修復的結果是整份樣式與繪圖被丟掉**，
        // 而它不會說是哪裡壞的）。**擋在這裡而不是自動改名**：改名等於默默給出一張
        // 「名字跟呼叫端以為的不一樣」的表，而呼叫端往往就是靠名字去對欄位的。
        // 比對不分大小寫——Excel 的工作表名稱唯一性就是不分大小寫的。
        // 注意衝突可能是 SanitizeSheetName 造出來的（31 字元截斷、非法字元換成底線），
        // 所以要比對**清理後**的 Name，不是呼叫端給的原字串。
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            if (!names.Add(sheet.Name))
                throw new ArgumentException($"工作表名稱重複：「{sheet.Name}」。", nameof(sheets));
        }

        // leaveOpen: 串流的生命週期歸呼叫端管（測試要在寫完後繼續讀）。
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(archive, "[Content_Types].xml", w => WriteContentTypes(w, sheets));
        WriteEntry(archive, "_rels/.rels", WriteRootRelationships);
        WriteEntry(archive, "xl/workbook.xml", w => WriteWorkbook(w, sheets));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", w => WriteWorkbookRelationships(w, sheets));
        WriteEntry(archive, "xl/styles.xml", WriteStyles);

        for (int i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            WriteEntry(archive, $"xl/worksheets/sheet{i + 1}.xml", w => WriteSheet(w, sheet));

            if (sheet.Images.Count == 0)
                continue;

            int drawingNumber = i + 1;
            WriteEntry(archive, $"xl/worksheets/_rels/sheet{i + 1}.xml.rels",
                w => WriteSheetRelationships(w, drawingNumber));
            WriteEntry(archive, $"xl/drawings/drawing{drawingNumber}.xml", w => WriteDrawing(w, sheet));
            WriteEntry(archive, $"xl/drawings/_rels/drawing{drawingNumber}.xml.rels",
                w => WriteDrawingRelationships(w, sheet, drawingNumber));

            for (int j = 0; j < sheet.Images.Count; j++)
            {
                var image = sheet.Images[j];
                var entry = archive.CreateEntry($"xl/media/image{drawingNumber}_{j + 1}.{image.Extension}",
                    CompressionLevel.NoCompression);   // JPEG／PNG 已壓縮，再壓一次只是白花時間
                using var entryStream = entry.Open();
                entryStream.Write(image.Data, 0, image.Data.Length);
            }
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = XmlWriter.Create(entryStream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = false
        });

        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private static void WriteContentTypes(XmlWriter w, IReadOnlyList<XlsxSheet> sheets)
    {
        w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");

        WriteDefault("rels", "application/vnd.openxmlformats-package.relationships+xml");
        WriteDefault("xml", "application/xml");

        // 用到的圖片副檔名各登記一次（重複的 Default 會讓 Excel 判定檔案損毀）。
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            foreach (var image in sheet.Images)
            {
                if (extensions.Add(image.Extension))
                    WriteDefault(image.Extension, ContentTypeOf(image.Extension));
            }
        }

        WriteOverride("/xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        WriteOverride("/xl/styles.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");

        for (int i = 0; i < sheets.Count; i++)
        {
            WriteOverride($"/xl/worksheets/sheet{i + 1}.xml",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");

            if (sheets[i].Images.Count > 0)
            {
                WriteOverride($"/xl/drawings/drawing{i + 1}.xml",
                    "application/vnd.openxmlformats-officedocument.drawing+xml");
            }
        }

        w.WriteEndElement();

        void WriteDefault(string extension, string contentType)
        {
            w.WriteStartElement("Default");
            w.WriteAttributeString("Extension", extension);
            w.WriteAttributeString("ContentType", contentType);
            w.WriteEndElement();
        }

        void WriteOverride(string partName, string contentType)
        {
            w.WriteStartElement("Override");
            w.WriteAttributeString("PartName", partName);
            w.WriteAttributeString("ContentType", contentType);
            w.WriteEndElement();
        }
    }

    private static string ContentTypeOf(string extension) =>
        extension.Equals("png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

    private static void WriteRootRelationships(XmlWriter w)
    {
        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        WriteRelationship(w, "rId1", RelationshipNs + "/officeDocument", "xl/workbook.xml");
        w.WriteEndElement();
    }

    private static void WriteWorkbook(XmlWriter w, IReadOnlyList<XlsxSheet> sheets)
    {
        w.WriteStartElement("workbook", SpreadsheetNs);
        w.WriteAttributeString("xmlns", "r", null, RelationshipNs);
        w.WriteStartElement("sheets", SpreadsheetNs);

        for (int i = 0; i < sheets.Count; i++)
        {
            w.WriteStartElement("sheet", SpreadsheetNs);
            w.WriteAttributeString("name", sheets[i].Name);
            w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("id", RelationshipNs, "rId" + (i + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteWorkbookRelationships(XmlWriter w, IReadOnlyList<XlsxSheet> sheets)
    {
        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");

        for (int i = 0; i < sheets.Count; i++)
        {
            WriteRelationship(w, "rId" + (i + 1).ToString(CultureInfo.InvariantCulture),
                RelationshipNs + "/worksheet", $"worksheets/sheet{i + 1}.xml");
        }

        WriteRelationship(w, "rId" + (sheets.Count + 1).ToString(CultureInfo.InvariantCulture),
            RelationshipNs + "/styles", "styles.xml");
        w.WriteEndElement();
    }

    private static void WriteRelationship(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship");
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    /// <summary>樣式表。索引與 <see cref="XlsxStyle"/> 一一對應。
    ///
    /// **fills 的前兩項不可省**：OOXML 規定索引 0 必須是 <c>none</c>、索引 1 必須是 <c>gray125</c>，
    /// 少了會讓 Excel 判定檔案需要修復（而修復的結果是所有樣式被丟掉）。</summary>
    private static void WriteStyles(XmlWriter w)
    {
        w.WriteStartElement("styleSheet", SpreadsheetNs);

        w.WriteStartElement("numFmts", SpreadsheetNs);
        w.WriteAttributeString("count", "2");
        WriteNumFmt(164, "0.##");
        WriteNumFmt(165, "yyyy/mm/dd hh:mm");
        w.WriteEndElement();

        w.WriteStartElement("fonts", SpreadsheetNs);
        w.WriteAttributeString("count", "2");
        WriteFont(bold: false);
        WriteFont(bold: true);
        w.WriteEndElement();

        w.WriteStartElement("fills", SpreadsheetNs);
        w.WriteAttributeString("count", "3");
        WritePatternFill("none", null);
        WritePatternFill("gray125", null);
        WritePatternFill("solid", "FFD9E2F3");   // 標題列的淡藍底
        w.WriteEndElement();

        w.WriteStartElement("borders", SpreadsheetNs);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("border", SpreadsheetNs);
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" })
            w.WriteElementString(side, SpreadsheetNs, null);
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("cellStyleXfs", SpreadsheetNs);
        w.WriteAttributeString("count", "1");
        WriteXf(0, 0, 0, null, null, false);
        w.WriteEndElement();

        w.WriteStartElement("cellXfs", SpreadsheetNs);
        w.WriteAttributeString("count", "6");
        WriteXf(0, 0, 0, null, null, false);                      // 0 Default
        WriteXf(0, 1, 2, "center", "center", true);               // 1 Header
        WriteXf(164, 0, 0, null, null, false);                    // 2 Decimal
        WriteXf(165, 0, 0, null, null, false);                    // 3 DateTime
        WriteXf(0, 0, 0, null, "top", true);                      // 4 WrapTop
        WriteXf(0, 0, 0, "center", "center", false);              // 5 Centered
        w.WriteEndElement();

        w.WriteStartElement("cellStyles", SpreadsheetNs);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("cellStyle", SpreadsheetNs);
        w.WriteAttributeString("name", "Normal");
        w.WriteAttributeString("xfId", "0");
        w.WriteAttributeString("builtinId", "0");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteEndElement();

        void WriteNumFmt(int id, string code)
        {
            w.WriteStartElement("numFmt", SpreadsheetNs);
            w.WriteAttributeString("numFmtId", id.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("formatCode", code);
            w.WriteEndElement();
        }

        void WriteFont(bool bold)
        {
            w.WriteStartElement("font", SpreadsheetNs);
            if (bold)
                w.WriteElementString("b", SpreadsheetNs, null);
            WriteValueElement("sz", "11");
            WriteValueElement("name", "Calibri");
            w.WriteEndElement();
        }

        void WriteValueElement(string name, string value)
        {
            w.WriteStartElement(name, SpreadsheetNs);
            w.WriteAttributeString("val", value);
            w.WriteEndElement();
        }

        void WritePatternFill(string pattern, string? argb)
        {
            w.WriteStartElement("fill", SpreadsheetNs);
            w.WriteStartElement("patternFill", SpreadsheetNs);
            w.WriteAttributeString("patternType", pattern);
            if (argb != null)
            {
                w.WriteStartElement("fgColor", SpreadsheetNs);
                w.WriteAttributeString("rgb", argb);
                w.WriteEndElement();
                w.WriteStartElement("bgColor", SpreadsheetNs);
                w.WriteAttributeString("indexed", "64");
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }

        void WriteXf(int numFmtId, int fontId, int fillId, string? horizontal, string? vertical, bool wrap)
        {
            w.WriteStartElement("xf", SpreadsheetNs);
            w.WriteAttributeString("numFmtId", numFmtId.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fontId", fontId.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fillId", fillId.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("borderId", "0");
            if (numFmtId != 0) w.WriteAttributeString("applyNumberFormat", "1");
            if (fontId != 0) w.WriteAttributeString("applyFont", "1");
            if (fillId != 0) w.WriteAttributeString("applyFill", "1");

            if (horizontal != null || vertical != null || wrap)
            {
                w.WriteAttributeString("applyAlignment", "1");
                w.WriteStartElement("alignment", SpreadsheetNs);
                if (horizontal != null) w.WriteAttributeString("horizontal", horizontal);
                if (vertical != null) w.WriteAttributeString("vertical", vertical);
                if (wrap) w.WriteAttributeString("wrapText", "1");
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }
    }

    /// <summary>單一工作表。**子元素的順序由 schema 規定、不可調換**：
    /// <c>dimension → sheetViews → sheetFormatPr → cols → sheetData → autoFilter → drawing</c>。
    /// 順序錯了 Excel 會要求修復檔案，而不是忽略該元素。</summary>
    private static void WriteSheet(XmlWriter w, XlsxSheet sheet)
    {
        int maxColumns = Math.Max(sheet.Columns.Count, MaxCellCount(sheet));

        w.WriteStartElement("worksheet", SpreadsheetNs);
        w.WriteAttributeString("xmlns", "r", null, RelationshipNs);

        if (sheet.Rows.Count > 0 && maxColumns > 0)
        {
            w.WriteStartElement("dimension", SpreadsheetNs);
            w.WriteAttributeString("ref", "A1:" + CellReference(sheet.Rows.Count - 1, maxColumns - 1));
            w.WriteEndElement();
        }

        w.WriteStartElement("sheetViews", SpreadsheetNs);
        w.WriteStartElement("sheetView", SpreadsheetNs);
        w.WriteAttributeString("workbookViewId", "0");
        if (sheet.FreezeHeaderRow)
        {
            w.WriteStartElement("pane", SpreadsheetNs);
            w.WriteAttributeString("ySplit", "1");
            w.WriteAttributeString("topLeftCell", "A2");
            w.WriteAttributeString("activePane", "bottomLeft");
            w.WriteAttributeString("state", "frozen");
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("sheetFormatPr", SpreadsheetNs);
        w.WriteAttributeString("defaultRowHeight", "15");
        w.WriteEndElement();

        if (sheet.Columns.Count > 0)
        {
            w.WriteStartElement("cols", SpreadsheetNs);
            for (int i = 0; i < sheet.Columns.Count; i++)
            {
                w.WriteStartElement("col", SpreadsheetNs);
                w.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("width", Number(sheet.Columns[i].WidthChars));
                w.WriteAttributeString("customWidth", "1");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("sheetData", SpreadsheetNs);
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            w.WriteStartElement("row", SpreadsheetNs);
            w.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
            if (row.HeightPoints != null)
            {
                w.WriteAttributeString("ht", Number(row.HeightPoints.Value));
                w.WriteAttributeString("customHeight", "1");
            }

            for (int c = 0; c < row.Cells.Count; c++)
                WriteCell(w, row.Cells[c], r, c);

            w.WriteEndElement();
        }
        w.WriteEndElement();

        if (sheet.AutoFilter && sheet.Rows.Count > 0 && maxColumns > 0)
        {
            w.WriteStartElement("autoFilter", SpreadsheetNs);
            w.WriteAttributeString("ref", "A1:" + CellReference(sheet.Rows.Count - 1, maxColumns - 1));
            w.WriteEndElement();
        }

        if (sheet.Images.Count > 0)
        {
            w.WriteStartElement("drawing", SpreadsheetNs);
            w.WriteAttributeString("id", RelationshipNs, "rId1");
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static int MaxCellCount(XlsxSheet sheet)
    {
        int max = 0;
        foreach (var row in sheet.Rows)
            max = Math.Max(max, row.Cells.Count);
        return max;
    }

    private static void WriteCell(XmlWriter w, XlsxCell cell, int rowIndex, int columnIndex)
    {
        // 沒有值也沒有樣式的儲存格整個省略——空白列在圖片對照表裡很常見。
        if (!cell.IsNumeric && string.IsNullOrEmpty(cell.Text) && cell.Style == XlsxStyle.Default)
            return;

        w.WriteStartElement("c", SpreadsheetNs);
        w.WriteAttributeString("r", CellReference(rowIndex, columnIndex));
        if (cell.Style != XlsxStyle.Default)
            w.WriteAttributeString("s", ((int)cell.Style).ToString(CultureInfo.InvariantCulture));

        if (cell.IsNumeric)
        {
            w.WriteElementString("v", SpreadsheetNs, Number(cell.Number));
        }
        else if (!string.IsNullOrEmpty(cell.Text))
        {
            // inlineStr（而非 sharedStrings）：省掉一份字典與兩次掃描，而碰撞清單的重複字串
            // 本來就少（描述幾乎筆筆不同）。
            //
            // **順帶消掉 CSV 那條路必須處理的公式注入**（見 ClashExportService.CsvEscape）：
            // inlineStr 在 OOXML 裡就是字串，公式要另外寫 <f> 元素才成立，所以 `=HYPERLINK(...)`
            // 這種備註在 Excel 裡就是一串字。因此這裡**刻意不加**那個單引號前綴——
            // 加了反而會讓使用者看到資料裡多出一個引號。
            //
            // t 屬性必須寫在子元素之前：XmlWriter 一旦寫過子節點就不接受屬性（擲
            // InvalidOperationException），而那是執行期才會現形的錯。
            w.WriteAttributeString("t", "inlineStr");
            w.WriteStartElement("is", SpreadsheetNs);
            w.WriteStartElement("t", SpreadsheetNs);
            var text = SanitizeXmlText(cell.Text!);
            if (text.Length != text.Trim().Length)
                w.WriteAttributeString("xml", "space", null, "preserve");
            w.WriteString(text);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    /// <summary>剔除 XML 1.0 不允許的控制字元。備註是使用者自由輸入、還可能來自別台電腦的工作包，
    /// 夾帶一個 0x00 就足以讓 <see cref="XmlWriter"/> 擲例外而使整份匯出失敗。</summary>
    private static string SanitizeXmlText(string value)
    {
        StringBuilder? cleaned = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool allowed = c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF);
            if (allowed)
            {
                cleaned?.Append(c);
                continue;
            }

            cleaned ??= new StringBuilder(value.Length).Append(value, 0, i);
        }

        return cleaned?.ToString() ?? value;
    }

    private static void WriteSheetRelationships(XmlWriter w, int drawingNumber)
    {
        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        WriteRelationship(w, "rId1", RelationshipNs + "/drawing", $"../drawings/drawing{drawingNumber}.xml");
        w.WriteEndElement();
    }

    /// <summary>圖片的錨定資訊。採 <c>oneCellAnchor</c>（錨一角＋明確尺寸）而非 <c>twoCellAnchor</c>：
    /// 呼叫端已依圖片比例把列高與欄寬設好，此時「固定尺寸」比「跟著兩格拉伸」更不會變形。</summary>
    private static void WriteDrawing(XmlWriter w, XlsxSheet sheet)
    {
        // **前綴一律明寫**（xdr:／a:）而不是靠 XmlWriter 自己挑：根元素若落成預設命名空間，
        // 子元素會寫成無前綴形式——語意相同、但與所有既有工具產出的樣貌不一致，
        // 出問題時很難用「和 Excel 自己存的檔比對」來排除。明寫是零成本的保險。
        w.WriteStartElement("xdr", "wsDr", SpreadsheetDrawingNs);
        w.WriteAttributeString("xmlns", "a", null, DrawingNs);
        w.WriteAttributeString("xmlns", "r", null, RelationshipNs);

        for (int i = 0; i < sheet.Images.Count; i++)
        {
            var image = sheet.Images[i];
            long cx = (long)image.WidthPixels * EmuPerPixel;
            long cy = (long)image.HeightPixels * EmuPerPixel;

            w.WriteStartElement("xdr", "oneCellAnchor", SpreadsheetDrawingNs);

            w.WriteStartElement("xdr", "from", SpreadsheetDrawingNs);
            WriteDrawingValue("col", image.ColumnIndex);
            WriteDrawingValue("colOff", 2 * EmuPerPixel);
            WriteDrawingValue("row", image.RowIndex);
            WriteDrawingValue("rowOff", 2 * EmuPerPixel);
            w.WriteEndElement();

            w.WriteStartElement("xdr", "ext", SpreadsheetDrawingNs);
            w.WriteAttributeString("cx", cx.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("cy", cy.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();

            w.WriteStartElement("xdr", "pic", SpreadsheetDrawingNs);

            w.WriteStartElement("xdr", "nvPicPr", SpreadsheetDrawingNs);
            w.WriteStartElement("xdr", "cNvPr", SpreadsheetDrawingNs);
            // id 必須在整份繪圖中唯一且大於 0；1 慣例上保留給圖層，故自 2 起算。
            w.WriteAttributeString("id", (i + 2).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("name", "Picture " + (i + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteStartElement("xdr", "cNvPicPr", SpreadsheetDrawingNs);
            w.WriteStartElement("a", "picLocks", DrawingNs);
            w.WriteAttributeString("noChangeAspect", "1");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("xdr", "blipFill", SpreadsheetDrawingNs);
            w.WriteStartElement("a", "blip", DrawingNs);
            w.WriteAttributeString("r", "embed", RelationshipNs,
                "rId" + (i + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteStartElement("a", "stretch", DrawingNs);
            w.WriteStartElement("a", "fillRect", DrawingNs);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("xdr", "spPr", SpreadsheetDrawingNs);
            w.WriteStartElement("a", "xfrm", DrawingNs);
            w.WriteStartElement("a", "off", DrawingNs);
            w.WriteAttributeString("x", "0");
            w.WriteAttributeString("y", "0");
            w.WriteEndElement();
            w.WriteStartElement("a", "ext", DrawingNs);
            w.WriteAttributeString("cx", cx.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("cy", cy.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("a", "prstGeom", DrawingNs);
            w.WriteAttributeString("prst", "rect");
            w.WriteStartElement("a", "avLst", DrawingNs);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteEndElement();

            w.WriteStartElement("xdr", "clientData", SpreadsheetDrawingNs);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        w.WriteEndElement();

        void WriteDrawingValue(string localName, int value)
        {
            w.WriteStartElement("xdr", localName, SpreadsheetDrawingNs);
            w.WriteString(value.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }
    }

    private static void WriteDrawingRelationships(XmlWriter w, XlsxSheet sheet, int drawingNumber)
    {
        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");

        for (int i = 0; i < sheet.Images.Count; i++)
        {
            WriteRelationship(w, "rId" + (i + 1).ToString(CultureInfo.InvariantCulture),
                RelationshipNs + "/image",
                $"../media/image{drawingNumber}_{i + 1}.{sheet.Images[i].Extension}");
        }

        w.WriteEndElement();
    }

    /// <summary>A1 形式的儲存格位址（列與欄皆 0 起算）。</summary>
    internal static string CellReference(int rowIndex, int columnIndex)
    {
        var column = new StringBuilder();
        int n = columnIndex;
        do
        {
            column.Insert(0, (char)('A' + n % 26));
            n = n / 26 - 1;
        }
        while (n >= 0);

        return column.Append((rowIndex + 1).ToString(CultureInfo.InvariantCulture)).ToString();
    }

    /// <summary>數值一律 InvariantCulture：XML 的小數點必須是句點，
    /// 歐系語系（de-DE）的 CurrentCulture 會寫出「12,34」而讓 Excel 判定檔案損毀。</summary>
    private static string Number(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
