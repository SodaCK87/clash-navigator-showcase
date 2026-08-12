using System;
using System.IO;
using ClashNavigatorAddin.Services;
using Xunit;

namespace ClashNavigatorAddin.Tests;

/// <summary>圖檔尺寸解析。**尺寸錯了不會有任何錯誤訊息**——只會讓 Excel 裡的圖片變形或
/// 蓋到下一列，而那要有人打開檔案才看得出來。故連「截斷的檔案」這種路徑也一併釘住。</summary>
public class ImageSizeTests
{
    private static void WithTempFile(byte[] content, Action<string> use)
    {
        var path = Path.Combine(Path.GetTempPath(), "imagesize_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, content);
            use(path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>最小的合法 PNG 檔頭：簽章 + IHDR 區塊長度 + "IHDR" + 寬高（大端序）。</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[24];
        byte[] signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        Buffer.BlockCopy(signature, 0, bytes, 0, 8);

        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;   // IHDR 長度
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';

        WriteBigEndian(bytes, 16, width);
        WriteBigEndian(bytes, 20, height);
        return bytes;
    }

    private static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    /// <summary>最小的合法 JPEG：SOI + 一個 APP0 段（要被跳過）+ SOF0（帶尺寸）。
    /// JPEG 的高度**寫在寬度前面**，順序寫反是這類解析器最典型的錯。</summary>
    private static byte[] JpegHeader(int width, int height)
    {
        return new byte[]
        {
            0xFF, 0xD8,                                     // SOI
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,             // APP0，長度 4（含長度欄自身 2）
            0xFF, 0xC0, 0x00, 0x11, 0x08,                   // SOF0，長度 17，取樣精度 8
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width
        };
    }

    [Theory]
    [InlineData(800, 600)]
    [InlineData(1, 1)]
    [InlineData(4096, 2160)]
    public void TryRead_Png_ReturnsDimensions(int width, int height)
    {
        WithTempFile(PngHeader(width, height), path =>
        {
            Assert.True(ImageSize.TryRead(path, out int w, out int h));
            Assert.Equal(width, w);
            Assert.Equal(height, h);
        });
    }

    /// <summary>寬高不同的樣本才驗得出「有沒有寫反」——正方形永遠會過。</summary>
    [Theory]
    [InlineData(800, 600)]
    [InlineData(640, 480)]
    [InlineData(300, 900)]
    public void TryRead_Jpeg_ReturnsDimensions_WidthAndHeightNotSwapped(int width, int height)
    {
        WithTempFile(JpegHeader(width, height), path =>
        {
            Assert.True(ImageSize.TryRead(path, out int w, out int h));
            Assert.Equal(width, w);
            Assert.Equal(height, h);
        });
    }

    /// <summary>JPEG 的 DHT（0xC4）落在 SOF 的數值區間內但**不是** SOF，
    /// 誤判它會讀到霍夫曼表的位元組當尺寸。</summary>
    [Fact]
    public void TryRead_Jpeg_SkipsDefineHuffmanTableSegment()
    {
        var bytes = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xC4, 0x00, 0x06, 0x11, 0x22, 0x33, 0x44,   // DHT，長度 6
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x58, 0x03, 0x20   // SOF0：600×800
        };

        WithTempFile(bytes, path =>
        {
            Assert.True(ImageSize.TryRead(path, out int w, out int h));
            Assert.Equal(800, w);
            Assert.Equal(600, h);
        });
    }

    [Fact]
    public void TryRead_UnknownFormat_ReturnsFalse()
    {
        WithTempFile(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, path =>
            Assert.False(ImageSize.TryRead(path, out _, out _)));
    }

    /// <summary>被截斷的檔案（例如 Revit 匯出中途失敗）不得擲例外——呼叫端要能退回預設尺寸。</summary>
    [Fact]
    public void TryRead_TruncatedJpeg_ReturnsFalseInsteadOfThrowing()
    {
        WithTempFile(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00 }, path =>
            Assert.False(ImageSize.TryRead(path, out _, out _)));
    }

    [Fact]
    public void TryRead_EmptyFile_ReturnsFalse()
    {
        WithTempFile(Array.Empty<byte>(), path =>
            Assert.False(ImageSize.TryRead(path, out _, out _)));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), "imagesize_missing_" + Guid.NewGuid().ToString("N"));
        Assert.False(ImageSize.TryRead(path, out _, out _));
    }
}
