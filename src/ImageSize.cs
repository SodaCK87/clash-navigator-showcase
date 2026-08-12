using System.IO;

namespace ClashNavigatorAddin.Services;

/// <summary>從 JPEG／PNG 的檔頭讀出像素尺寸。
///
/// **刻意不用 <c>System.Drawing</c>**：net8 上 <c>System.Drawing.Common</c> 只支援 Windows 且需另外
/// 引入套件，而本專案的相依是刻意釘死且最小化的（見 csproj）。這裡要的只是「寬高各幾像素」，
/// 用來把圖片錨進 xlsx 時算出等比的儲存格尺寸——解析兩種格式的檔頭比背一整個影像函式庫便宜得多。
///
/// 純函式、可離線測試。</summary>
internal static class ImageSize
{
    /// <summary>讀出圖檔的像素尺寸。無法辨識（非 JPEG／PNG、檔案損毀、被截斷）時回傳 false，
    /// 呼叫端應退回一組預設尺寸而非中止——匯出一份「圖片比例怪怪的」報告，
    /// 仍遠優於因為一張圖讀不到寬高就整份失敗。</summary>
    public static bool TryRead(string filePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);
            return TryReadPng(reader, ref width, ref height)
                || TryReadJpeg(reader, ref width, ref height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 涵蓋 EndOfStreamException（它繼承自 IOException）：檔案被截斷（例如 Revit 匯出
            // 中途失敗）與「格式不認得」同樣處置——回 false 讓呼叫端退回預設尺寸。
            //
            // **另外兩個不是 IOException 的子類**，漏掉就會逃出去、讓整份匯出因為一張圖而失敗，
            // 與上面那句承諾相反：`UnauthorizedAccessException`（權限不足，或路徑其實是個目錄）
            // 繼承的是 `SystemException`；`ArgumentException` 則來自空字串／含非法字元的路徑
            // ——截圖的路徑是程式組出來的，但快取目錄可以被使用者手動動過。
            return false;
        }
    }

    private static bool TryReadPng(BinaryReader reader, ref int width, ref int height)
    {
        reader.BaseStream.Position = 0;
        if (reader.BaseStream.Length < 24)
            return false;

        // 8 bytes 簽章 + 4 bytes 區塊長度 + "IHDR"，其後即為大端序的寬與高。
        var signature = reader.ReadBytes(8);
        if (signature[0] != 0x89 || signature[1] != 'P' || signature[2] != 'N' || signature[3] != 'G')
            return false;

        reader.BaseStream.Position = 16;
        width = ReadBigEndianInt32(reader);
        height = ReadBigEndianInt32(reader);
        return width > 0 && height > 0;
    }

    private static bool TryReadJpeg(BinaryReader reader, ref int width, ref int height)
    {
        reader.BaseStream.Position = 0;
        if (reader.ReadByte() != 0xFF || reader.ReadByte() != 0xD8)
            return false;

        long length = reader.BaseStream.Length;
        while (reader.BaseStream.Position < length - 1)
        {
            // 段落一律以 0xFF 起頭，其間可能夾雜填充的 0xFF，逐一略過直到讀到真正的標記碼。
            if (reader.ReadByte() != 0xFF)
                continue;

            byte marker = reader.ReadByte();
            while (marker == 0xFF && reader.BaseStream.Position < length)
                marker = reader.ReadByte();

            // 無資料段（RSTn／TEM）沒有長度欄位，不能照一般段落跳過。
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
                continue;

            if (reader.BaseStream.Position + 2 > length)
                return false;

            int segmentLength = (reader.ReadByte() << 8) | reader.ReadByte();
            if (segmentLength < 2)
                return false;

            // SOF0~SOF15 帶有影像尺寸，但 0xC4（DHT）／0xC8（JPG）／0xCC（DAC）不是 SOF。
            bool isStartOfFrame = marker >= 0xC0 && marker <= 0xCF
                                  && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

            if (isStartOfFrame)
            {
                if (reader.BaseStream.Position + 5 > length)
                    return false;

                reader.ReadByte();  // 取樣精度，不需要
                height = (reader.ReadByte() << 8) | reader.ReadByte();
                width = (reader.ReadByte() << 8) | reader.ReadByte();
                return width > 0 && height > 0;
            }

            reader.BaseStream.Position += segmentLength - 2;
        }

        return false;
    }

    private static int ReadBigEndianInt32(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }
}
