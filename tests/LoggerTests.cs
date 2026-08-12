using System;
using System.IO;
using System.Runtime.CompilerServices;
using ClashNavigatorAddin.Services;
using Xunit;

namespace ClashNavigatorAddin.Tests;

/// <summary>Logger 附加與 1MB 輪替邊界的測試。透過 internal WriteTo(dir, msg, maxBytes)
/// 以獨立暫存目錄與極小門檻驗證輪替，不觸及 %AppData% 也不需寫入真的 1MB。</summary>
public sealed class LoggerTests : IDisposable
{
    private readonly string _dir;
    private string LogPath => Path.Combine(_dir, "log.txt");
    private string OldPath => Path.Combine(_dir, "log.old.txt");

    public LoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cn_logger_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* 測試清理失敗不影響結果 */ }
    }

    [Fact]
    public void WriteTo_CreatesDirectoryAndFile()
    {
        Logger.WriteTo(_dir, "hello", maxBytes: 1_000_000);

        Assert.True(File.Exists(LogPath));
        Assert.Contains("hello", File.ReadAllText(LogPath));
    }

    [Fact]
    public void WriteTo_AppendsAcrossCalls_WhenBelowThreshold()
    {
        Logger.WriteTo(_dir, "alpha", maxBytes: 1_000_000);
        Logger.WriteTo(_dir, "bravo", maxBytes: 1_000_000);

        var text = File.ReadAllText(LogPath);
        Assert.Contains("alpha", text);
        Assert.Contains("bravo", text);
        Assert.False(File.Exists(OldPath)); // 未超門檻不應輪替
    }

    [Fact]
    public void WriteTo_RotatesToOldFile_WhenCurrentExceedsMaxBytes()
    {
        // 每筆帶時間戳的紀錄約 30+ bytes，門檻 5 → 第一筆寫完即超過，第二筆前觸發輪替。
        Logger.WriteTo(_dir, "first-entry", maxBytes: 5);
        Logger.WriteTo(_dir, "second-entry", maxBytes: 5);

        Assert.True(File.Exists(OldPath));
        Assert.Contains("first-entry", File.ReadAllText(OldPath));   // 舊內容被搬到 log.old.txt

        var current = File.ReadAllText(LogPath);
        Assert.Contains("second-entry", current);
        Assert.DoesNotContain("first-entry", current);              // 現行檔僅剩最新一筆
    }

    [Fact]
    public void WriteTo_RotationReplacesPreviousOldGeneration()
    {
        Logger.WriteTo(_dir, "gen1", maxBytes: 5);
        Logger.WriteTo(_dir, "gen2", maxBytes: 5); // 輪替：old = gen1
        Logger.WriteTo(_dir, "gen3", maxBytes: 5); // 再輪替：old = gen2（取代 gen1，不應因既有 old 而失敗）

        var old = File.ReadAllText(OldPath);
        Assert.Contains("gen2", old);
        Assert.DoesNotContain("gen1", old);        // 單世代：舊的 old 被覆蓋
        Assert.Contains("gen3", File.ReadAllText(LogPath));
    }

    // ── 例外的記錄文字（Describe）──
    // Log(string) 把去處寫死在 %AppData%，測試不能碰，故格式化抽成純函式後在此驗。
    // 例外**必須真的擲出來**才有堆疊——直接 new 出來的例外其 StackTrace 為 null，
    // 驗不到本項要修的東西。

    /// <summary>取得「真的被擲出過」的例外（帶堆疊）。</summary>
    private static Exception Thrown(Action act)
    {
        try { act(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("測試前提有誤：傳入的動作並未擲出例外");
    }

    /// <summary>NoInlining：本方法若被行內展開，堆疊裡就不會有它的名字，
    /// 下方測試會在 Release 組態下假性轉紅（Debug 不行內，故不加也會過——正因如此才要明寫）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailingOperation()
        => throw new NullReferenceException("未將物件參考設定為物件的執行個體。");

    /// <summary>核心 red/green：記錄必須帶堆疊。修正前是 <c>$"{context}: {型別}: {訊息}"</c>——
    /// NRE 進到 log 就只剩「未將物件參考設定為物件的執行個體」，連哪一行炸的都不知道，
    /// 而 log.txt 是現場的唯一證據。以「堆疊裡有沒有出事的方法名」為判準。</summary>
    [Fact]
    public void Describe_IncludesStackTrace_SoTheFaultingFrameIsIdentifiable()
    {
        var text = Logger.Describe("ClashStatusStore.Load", Thrown(FailingOperation));

        Assert.Contains(nameof(FailingOperation), text);   // ← 修正前只有型別與訊息，無此名
    }

    /// <summary>包裝過的例外只看外層往往毫無資訊，故 InnerException 鏈也要在。</summary>
    [Fact]
    public void Describe_IncludesInnerExceptionChain()
    {
        var text = Logger.Describe("DebouncedJsonStore.Flush", Thrown(
            () => throw new InvalidOperationException("寫檔失敗", new IOException("磁碟空間不足"))));

        Assert.Contains("磁碟空間不足", text);          // ← 修正前只有外層的「寫檔失敗」
        Assert.Contains(nameof(IOException), text);
    }

    /// <summary>context 前綴必須留在最前面：現場判讀正是靠它認出處理者。
    /// 此案在修正前後皆綠，守的是「別把前綴一起改掉」。</summary>
    [Fact]
    public void Describe_KeepsContextPrefixFirst()
    {
        var text = Logger.Describe("ClashStatusStore.Load", Thrown(FailingOperation));

        Assert.StartsWith("ClashStatusStore.Load: ", text, StringComparison.Ordinal);
    }

    /// <summary>輪替的檔案操作失敗時，不得讓例外逸出而吞掉本行訊息——應放棄本次輪替、
    /// 續寫現行檔即可。此處以「log.old.txt 是一個目錄」強制輪替失敗（Replace／Move 皆擲例外），
    /// 決定性地重現「輪替段落丟例外」這條路徑。
    ///
    /// 修正前 <c>File.Delete(oldPath)</c> 對目錄擲例外且無 try/catch → WriteTo 直接拋出（本測試轉紅）。
    /// 真正的跨行程競態（另一個 Revit 搶先移走 logPath、或刪掉他人剛輪替保存的世代）需兩個行程，
    /// 非單元測試所能決定性重現；此測試鎖住的是「輪替失敗不吞訊息」這個可測的一半。</summary>
    [Fact]
    public void WriteTo_RotationOperationFails_DoesNotThrow_AndStillWrites()
    {
        Directory.CreateDirectory(OldPath);            // 讓 log.old.txt 以目錄型態存在 → 輪替操作必失敗

        Logger.WriteTo(_dir, "before", maxBytes: 5);   // logPath 建立（此時尚無、不輪替）
        Logger.WriteTo(_dir, "after", maxBytes: 5);    // 超門檻 → 嘗試輪替（失敗）→ 應吞下並續寫

        Assert.Contains("after", File.ReadAllText(LogPath));   // 訊息未因輪替失敗而遺失
    }
}
