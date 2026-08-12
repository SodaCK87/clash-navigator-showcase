using System;
using System.IO;
using ClashNavigatorAddin.Services;
using Xunit;

namespace ClashNavigatorAddin.Tests;

/// <summary>AtomicFile 原子寫入與損毀備份的測試。回歸即造成使用者累積的狀態／備註靜默損毀，
/// 且回退分支（File.Replace 失敗）與暫存檔殘留過去從未驗證。以獨立暫存目錄執行，不觸及 %AppData%。</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir;

    public AtomicFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cn_atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* 測試清理失敗不影響結果 */ }
    }

    private string PathIn(string name) => Path.Combine(_dir, name);

    [Fact]
    public void WriteAllText_CreatesFile_WhenTargetDoesNotExist()
    {
        var path = PathIn("new.json");
        AtomicFile.WriteAllText(path, "hello");

        Assert.True(File.Exists(path));
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile_WithLatestContent()
    {
        var path = PathIn("over.json");
        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_CreatesMissingParentDirectory()
    {
        var path = Path.Combine(_dir, "sub", "deep", "file.json");
        AtomicFile.WriteAllText(path, "x");

        Assert.True(File.Exists(path));
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_PreservesExactContent_IncludingUnicodeAndNewlines()
    {
        var path = PathIn("unicode.json");
        var content = "第一行\n第二行\r\n\"引號\" 與 逗號,值 🚧";
        AtomicFile.WriteAllText(path, content);

        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_LeavesNoTempFile_OnSuccess()
    {
        var path = PathIn("clean.json");
        AtomicFile.WriteAllText(path, "data");

        // 成功寫入後不得殘留任何暫存檔（唯一化 tmp 名稱在成功路徑會被 Replace/Move 消耗）。
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void WriteAllText_RepeatedWrites_DoNotAccumulateTempFiles()
    {
        var path = PathIn("repeat.json");
        for (int i = 0; i < 10; i++)
            AtomicFile.WriteAllText(path, "v" + i);

        Assert.Equal("v9", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(Directory.GetFiles(_dir)); // 只剩目標檔本身
    }

    /// <summary>D4：回傳備份路徑（呼叫端要拿它去告訴使用者東西被搬到哪了——
    /// 單世代的 .corrupt 會被下一次損毀覆蓋，不知道它存在就等於沒有備份）。</summary>
    [Fact]
    public void BackupCorrupt_RenamesFileToCorruptBackup_AndReturnsItsPath()
    {
        var path = PathIn("bad.json");
        File.WriteAllText(path, "truncated{");

        var backup = AtomicFile.BackupCorrupt(path);

        Assert.False(File.Exists(path));                       // 原檔已改名
        Assert.True(File.Exists(path + ".corrupt"));           // 備份存在
        Assert.Equal("truncated{", File.ReadAllText(path + ".corrupt"));
        Assert.Equal(path + ".corrupt", backup);               // 回傳的路徑即該備份
    }

    [Fact]
    public void BackupCorrupt_OverwritesPreviousBackup()
    {
        var path = PathIn("bad2.json");
        File.WriteAllText(path + ".corrupt", "old backup");
        File.WriteAllText(path, "new bad content");

        AtomicFile.BackupCorrupt(path);

        Assert.Equal("new bad content", File.ReadAllText(path + ".corrupt")); // 舊備份被取代
    }

    /// <summary>無檔可備份時回 null——D4 據此對使用者說「備份失敗，資料無法復原」
    /// 而非給一個不存在的路徑叫他去找。</summary>
    [Fact]
    public void BackupCorrupt_MissingFile_DoesNotThrowOrCreateBackup_AndReturnsNull()
    {
        var path = PathIn("nonexistent.json");

        var backup = AtomicFile.BackupCorrupt(path); // best-effort，不應拋出

        Assert.False(File.Exists(path + ".corrupt"));
        Assert.Null(backup);
    }

    // ── SweepSupersededTemps：清掉「已被目標正本取代」的殘留暫存檔 ──
    // 寫入持續失敗（唯讀／磁碟滿）時 AtomicFile 刻意保留 tmp 供救援，會累積；此清理只刪已被取代的。

    /// <summary>tmp 檔名：目標檔名 + "." + 32 位十六進位 + ".tmp"（同 WriteAllText 的命名）。</summary>
    private static string TempNameFor(string targetName) => targetName + "." + Guid.NewGuid().ToString("N") + ".tmp";

    /// <summary>目標正本比 tmp 新＝tmp 內容已落地＝可安全刪除。</summary>
    [Fact]
    public void SweepSupersededTemps_DeletesTmp_WhenTargetIsNewer()
    {
        var target = PathIn("clash_status.json");
        var tmp = PathIn(TempNameFor("clash_status.json"));
        File.WriteAllText(target, "current");
        File.WriteAllText(tmp, "stale-unpersisted");

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(tmp, now.AddMinutes(-5));
        File.SetLastWriteTimeUtc(target, now); // 目標較新

        AtomicFile.SweepSupersededTemps(_dir);

        Assert.False(File.Exists(tmp));   // 已取代 → 刪除
        Assert.True(File.Exists(target)); // 正本不受影響
    }

    /// <summary>tmp 比目標新＝可能是並行的另一個 Revit 正在寫、或尚未落地的最新救援副本＝不得刪。</summary>
    [Fact]
    public void SweepSupersededTemps_KeepsTmp_WhenTmpIsNewerThanTarget()
    {
        var target = PathIn("clash_status.json");
        var tmp = PathIn(TempNameFor("clash_status.json"));
        File.WriteAllText(target, "older-on-disk");
        File.WriteAllText(tmp, "fresh-unpersisted");

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(target, now.AddMinutes(-5));
        File.SetLastWriteTimeUtc(tmp, now); // tmp 較新

        AtomicFile.SweepSupersededTemps(_dir);

        Assert.True(File.Exists(tmp)); // 未被取代 → 保留
    }

    /// <summary>目標檔不存在＝tmp 可能是唯一副本（首次寫入即失敗／原檔被損毀備份走）＝不得刪。</summary>
    [Fact]
    public void SweepSupersededTemps_KeepsTmp_WhenTargetMissing()
    {
        var tmp = PathIn(TempNameFor("orphan.json")); // 沒有對應的 orphan.json
        File.WriteAllText(tmp, "only-rescue-copy");

        AtomicFile.SweepSupersededTemps(_dir);

        Assert.True(File.Exists(tmp));
    }

    /// <summary>不符「.&lt;32 hex&gt;.tmp」命名的 .tmp 一律不碰——別刪掉別人程式的暫存檔。</summary>
    [Fact]
    public void SweepSupersededTemps_IgnoresForeignTmp_NotMatchingGuidPattern()
    {
        var foreign = PathIn("something.tmp");            // 根本不是 <目標>.<guid>.tmp
        var shortHex = PathIn("data.json.123.tmp");        // 十六進位段長度不符
        File.WriteAllText(foreign, "not ours");
        File.WriteAllText(shortHex, "not ours either");

        AtomicFile.SweepSupersededTemps(_dir);

        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(shortHex));
    }

    /// <summary>tmp 與目標檔同目錄，故子目錄（如 detection_results\&lt;hash&gt;.json）也要遞迴涵蓋。</summary>
    [Fact]
    public void SweepSupersededTemps_RecursesIntoSubdirectories()
    {
        var sub = Path.Combine(_dir, "detection_results");
        Directory.CreateDirectory(sub);
        var target = Path.Combine(sub, "hash.json");
        var tmp = Path.Combine(sub, TempNameFor("hash.json"));
        File.WriteAllText(target, "current");
        File.WriteAllText(tmp, "stale");

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(tmp, now.AddMinutes(-5));
        File.SetLastWriteTimeUtc(target, now);

        AtomicFile.SweepSupersededTemps(_dir); // 掃根目錄

        Assert.False(File.Exists(tmp)); // 子目錄內也清得到
    }

    /// <summary>目標檔唯讀（使用者實際回報過的失敗成因）：
    /// Replace 被擋、回退的 Delete 同樣被擋，例外要向上傳遞讓呼叫端說得出口。
    ///
    /// **與既有案例的差別、也是本案例存在的理由**：其餘失敗案例都用「拿目錄佔住目標路徑」驅動，
    /// 機制相同但那不是使用者會遇到的形狀，而且**它的例外訊息指向的不是目標檔**。
    /// 唯讀屬性下訊息才會指名目標檔——使用者要拿那個路徑去檔案總管把唯讀取消掉。
    /// 訊息本身的語言隨執行時期而異（2026-08-04 實測：net48 中文、net8 英文），故只斷言它帶得出路徑。
    ///
    /// <b>Windows 限定</b>：POSIX 的唯讀是目錄權限的事，檔案沒有寫入權仍可被 rename 蓋過去，
    /// 所以這個案例在 Linux 上不成立——理由寫在 <see cref="WindowsOnlyFactAttribute"/>。</summary>
    [WindowsOnlyFact]
    public void WriteAllText_ReadOnlyTarget_Throws_AndNamesTargetNotTemp()
    {
        var path = PathIn("readonly.json");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "new"));

            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(".tmp", ex.Message, StringComparison.Ordinal);
            Assert.Equal("old", File.ReadAllText(path));   // 寫不進去，但原檔完好——不得被寫壞

            // 完整的新內容留在 .tmp 供人工救援（WriteAllText 最內層 catch 刻意不刪它）。
            var rescue = Directory.GetFiles(_dir, "readonly.json.*.tmp");
            Assert.Equal("new", File.ReadAllText(Assert.Single(rescue)));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);   // 不還原的話 Dispose 刪不掉暫存目錄
        }
    }

    /// <summary>純函式：命名格式解析——目標名取得、外來格式回 null。</summary>
    [Fact]
    public void TargetOfTemp_ParsesOwnNaming_AndRejectsForeign()
    {
        Assert.Equal("clash_status.json", AtomicFile.TargetOfTemp("clash_status.json." + new string('a', 32) + ".tmp"));
        Assert.Equal("a.b.json", AtomicFile.TargetOfTemp("a.b.json." + new string('f', 32) + ".tmp")); // 目標名含點也對
        Assert.Null(AtomicFile.TargetOfTemp("clash_status.json.tmp"));   // 沒有 guid 段
        Assert.Null(AtomicFile.TargetOfTemp("data.json.123.tmp"));       // guid 段長度不符
        Assert.Null(AtomicFile.TargetOfTemp("something.tmp"));           // 完全不符
    }
}
