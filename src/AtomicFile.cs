using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ClashNavigatorAddin.Services;

/// <summary>原子性檔案寫入與損毀檔備份的共用工具。
///
/// 所有持久化 Store（狀態／設定／高亮／檢測結果）都以此寫檔，避免
/// Revit 在寫入中途崩潰時將整份 JSON 截斷損毀——下次載入解析失敗即
/// 靜默丟失使用者累積的檢查狀態與備註。做法：先完整寫入同目錄的暫存檔，
/// 再以 <see cref="File.Replace(string, string, string)"/> 原子替換，
/// 確保磁碟上任一時點都是「上一版」或「新版」的完整內容。</summary>
internal static class AtomicFile
{
    /// <summary>原子性寫入文字：先寫暫存檔再替換目標檔。目錄不存在時自動建立。</summary>
    public static void WriteAllText(string path, string contents) =>
        Write(path, tmp => File.WriteAllText(tmp, contents));

    /// <summary>原子性寫入位元組。文字版的孿生：截圖快取（<see cref="ClashCaptureCache"/>）
    /// 寫的是 JPEG，而**半個 JPEG 比沒有 JPEG 糟**——它會被下一輪匯出當成一張有效的快取圖沿用，
    /// 而 Excel 裡看到的是一格壞掉的圖片。暫存檔命名與文字版相同，
    /// 故 <see cref="SweepSupersededTemps"/> 一樣掃得到殘檔。</summary>
    public static void WriteAllBytes(string path, byte[] contents) =>
        Write(path, tmp => File.WriteAllBytes(tmp, contents));

    private static void Write(string path, Action<string> writeTemp)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 暫存檔名加入 Guid，避免兩個 Revit 執行個體同時 Flush 同一檔案時撞用同一個 .tmp
        // （固定 .tmp 會讓後者覆寫前者半寫入的內容，或在替換階段互相搶檔而失敗）。
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        writeTemp(tmp);

        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
            // 成功後 tmp 已被 Replace 刪除或 Move 改名，磁碟上不留殘檔。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File.Replace 在少數環境（跨磁碟區、防毒即時掃描鎖定、部分網路磁碟）可能失敗；
            // 退回「刪除後改名」。暫存檔已完整寫入，最壞情況僅是替換瞬間的極短空窗，
            // 仍優於直接覆寫目標檔（寫入中途崩潰會留下截斷的檔案）。
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                // 回退亦失敗：tmp 仍保有完整的新內容，刻意「不」刪除它以供人工救援，
                // 並讓例外向上傳遞——呼叫端才不會誤以為已成功寫入而靜默遺失本次變更。
                throw;
            }
        }
    }

    /// <summary>將解析失敗的損毀檔改名為 <c>*.corrupt</c> 備份，供事後救援，
    /// 而非在重建時默默覆寫遺失。盡力而為，備份本身失敗不拋出。
    ///
    /// 回傳備份檔的完整路徑；**null＝沒有備份可救**（原檔不存在，或搬移失敗）。
    /// 呼叫端要拿它去告訴使用者東西被搬到哪了——單世代的 <c>.corrupt</c> 會被下一次損毀覆蓋
    /// （見下方的 Delete），使用者不知道它存在就等於沒有備份。</summary>
    public static string? BackupCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var backup = path + ".corrupt";
            if (File.Exists(backup))
                File.Delete(backup);
            File.Move(path, backup);
            return backup;
        }
        catch
        {
            // 備份失敗不影響主流程（載入端會退回空集合重建）。
            return null;
        }
    }

    /// <summary>清掉「已被取代」的殘留暫存檔（每工作階段開頭一次、best-effort）。
    ///
    /// <b>為什麼需要</b>：成功寫入時 tmp 會被 <see cref="File.Replace(string, string, string)"/> 消耗、
    /// 或 <see cref="File.Move(string, string)"/> 改名，正常運作不留殘檔（見 <see cref="WriteAllText"/>）。
    /// 但寫入「完全失敗」時（磁碟滿／目標檔唯讀／被獨占鎖），<see cref="WriteAllText"/> 最內層 catch
    /// **刻意保留** tmp 以供人工救援（見該處註解，E5 的關閉救援亦倚賴此）。於是在持續失敗的情境下，
    /// 每次編輯／每次關閉重試都留下一個 ~數 MB 的殘檔而無限累積。
    ///
    /// <b>安全刪除的判準＝「已被取代」，不是「所有 tmp」</b>：一個 tmp 只在「它的目標檔尚未被更新地寫過」
    /// 時才可能握有未落地的資料——一旦目標檔的寫入時間比 tmp 新，tmp 的內容就已被磁碟上的正本涵蓋、
    /// 確定是垃圾。**盲刪全部 tmp 會刪掉那份唯一的救援副本，正是要避免的事。** 此判準對並行的另一個
    /// Revit 也安全：它正在寫的 tmp 是秒級新鮮、比目標檔新，不會被視為已取代。
    ///
    /// <b>只認我們自己的 tmp</b>：檔名須符合 <see cref="WriteAllText"/> 的
    /// <c>&lt;目標檔&gt;.&lt;32 位十六進位 GUID&gt;.tmp</c> 命名，其餘 .tmp 一律不碰。
    /// 遞迴掃 <paramref name="root"/>（tmp 與目標檔同目錄，故子目錄如 detection_results 亦涵蓋）。</summary>
    public static void SweepSupersededTemps(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            foreach (var tmp in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
            {
                try
                {
                    var targetName = TargetOfTemp(Path.GetFileName(tmp));
                    if (targetName == null)
                        continue; // 不符我們的命名格式——別碰別人的 .tmp

                    // tmp 與目標檔同目錄（WriteAllText 把 tmp 寫在 Path.GetDirectoryName(path)）。
                    var target = Path.Combine(Path.GetDirectoryName(tmp)!, targetName);
                    var targetExists = File.Exists(target);
                    var targetMtime = targetExists ? File.GetLastWriteTimeUtc(target) : default;

                    if (IsSuperseded(targetExists, targetMtime, File.GetLastWriteTimeUtc(tmp)))
                        File.Delete(tmp);
                }
                catch
                {
                    // 個別檔案判讀／刪除失敗時保守保留（同 DetectionResultStore.PruneOrphansOnce）。
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("AtomicFile.SweepSupersededTemps", ex);
        }
    }

    /// <summary>tmp 檔名 <c>&lt;目標&gt;.&lt;32 hex&gt;.tmp</c> → 目標檔名；不符格式回 null。純函式。</summary>
    internal static string? TargetOfTemp(string tmpFileName)
    {
        var m = TempNamePattern.Match(tmpFileName);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>tmp 是否已被目標正本取代＝可安全刪除。**目標不存在時一律 false**
    /// （此 tmp 可能是唯一副本，不得刪）。純函式。</summary>
    internal static bool IsSuperseded(bool targetExists, DateTime targetLastWriteUtc, DateTime tmpLastWriteUtc)
        => targetExists && targetLastWriteUtc > tmpLastWriteUtc;

    // WriteAllText 的 tmp 命名：path + "." + Guid.ToString("N") + ".tmp"。GUID("N") 為 32 位無分隔十六進位。
    private static readonly Regex TempNamePattern =
        new(@"^(.+)\.[0-9a-fA-F]{32}\.tmp$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
