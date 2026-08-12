using System;
using System.IO;
using System.Text;

namespace ClashNavigatorAddin.Services;

/// <summary>極簡的檔案記錄器，將無法對使用者顯示的例外（多半來自被吞掉的 catch）
/// 寫入 %AppData%\ClashNavigatorAddin\log.txt，供事後排查現場問題。
/// 所有寫入皆為 best-effort：記錄失敗本身絕不拋出例外或影響主要功能。
///
/// 刻意「不」記錄逐元素圖形覆寫迴圈中的 catch——那些迴圈可能對數萬個元素各執行一次，
/// 記錄下來只會產生大量重複雜訊、拖慢操作；記錄的價值在於檔案 I/O、視圖建立、
/// 交易層級等「單次且可能真的出問題」的失敗點。</summary>
internal static class Logger
{
    private const long MaxLogBytes = 1_000_000; // 超過約 1MB 即輪替，避免無限成長

    private static readonly object Sync = new();

    /// <summary>覆寫 log.txt 去處的環境變數。**唯一的用途是讓自動化測試不要污染使用者的正式 log。**
    ///
    /// <b>問題</b>：全庫有 **98 個** <see cref="Log(string)"/> 呼叫點
    /// （2026-08-12 實測；2026-08-09 定案時是 74），而它把去處寫死在
    /// <see cref="AppDataPaths.Root"/>。任何測試只要走到其中一個就會寫進使用者的正式 log.txt
    /// ——2026-08-09 實測，990 個測試每輪寫 <b>146 bytes</b>，內容是
    /// 「DocumentChangeLog：C:\models\a.rvt 的變動元素超過 100000…」，
    /// 而 <c>C:\models\a.rvt</c> 是測試 fixture、那個模型根本不存在。
    /// log.txt 是現場排查的唯一證據（見 CONVENTIONS 2-3「現場 log 的判讀」：一次真實排查曾因 log
    /// 誤導而白跑），且上面那道 1 MB 輪替會讓假紀錄把真的擠掉。
    ///
    /// <b>為什麼改在這裡而不是逐個元件</b>：<c>f5c16cc</c> 試過逐個加可選 log 委派
    /// （<c>DebouncedJsonStore</c>／<c>CrossProcessFileLock</c>／<c>MergedJsonWriter</c> 三處），
    /// 而 <c>DocumentChangeLog</c> 正是後來新增、沒有人記得比照辦理的那一個。近百個呼叫點靠自律
    /// 逐一補只會再漏第二次——**那個數字三天內從 74 長到 98，本身就是證據**；
    /// <c>Log</c> 是它們唯一的匯流點，收在這裡一次涵蓋全部。
    ///
    /// <b>為什麼不是覆寫 <see cref="AppDataPaths.Root"/></b>：那會連「路徑長什麼樣」的斷言一起改掉，
    /// 而 <c>StartupBackupTests</c> 刻意寫死真實路徑來釘住「使用者被告知去哪裡找備份」這個對外承諾
    /// （試過，那兩條當場轉紅）。要導開的是**寫入的去處**，不是路徑的計算。
    ///
    /// <b>為什麼是環境變數而不是可寫的靜態屬性</b>：靜態建構當下讀一次、之後不可變，
    /// 不會有「跑到一半換去處」這種狀態；**正式執行時未設定，行為與此前完全相同**。</summary>
    private const string LogDirOverrideVariable = "CLASHNAVIGATOR_LOG_DIR";

    /// <summary>log.txt 實際的去處。見 <see cref="LogDirOverrideVariable"/>。</summary>
    internal static readonly string LogDirectory = ResolveLogDirectory();

    private static string ResolveLogDirectory()
    {
        var custom = Environment.GetEnvironmentVariable(LogDirOverrideVariable);
        return string.IsNullOrWhiteSpace(custom) ? AppDataPaths.Root : custom;
    }

    public static void Log(string message)
    {
        try
        {
            WriteTo(LogDirectory, message, MaxLogBytes);
        }
        catch
        {
            // 記錄失敗絕不影響主要功能
        }
    }

    public static void Log(string context, Exception ex) => Log(Describe(context, ex));

    /// <summary>例外的記錄文字：<paramref name="context"/> 後接 <see cref="Exception.ToString"/> 的
    /// 完整內容（型別全名、訊息、InnerException 鏈、堆疊）。
    ///
    /// <b>為什麼記整份而非「型別: 訊息」</b>：原本記的是 <c>$"{context}: {ex.GetType().Name}: {ex.Message}"</c>，
    /// 而全專案帶記錄的 catch 多為 <c>catch (Exception)</c>——來的若是 NRE，那行就只剩
    /// 「未將物件參考設定為物件的執行個體」，連是哪一行炸的都不知道。log.txt 是現場的唯一證據
    /// （一次真實排查曾因 log 少了堆疊而誤導、整輪白跑），沒有堆疊的紀錄寫了等於沒寫。
    /// InnerException 同理：包裝過的例外只看外層訊息往往毫無資訊（「發生一或多個錯誤」）。
    ///
    /// <b>體積</b>：一則從約 80 bytes 變成**實測約 600 bytes**（三則典型紀錄量得，且含 PDB 的
    /// 完整檔案路徑；實際部署不隨附 PDB 故更小），1MB 的輪替額度仍可容納約 1600 則。本專案的記錄
    /// 刻意稀疏（見類別說明：逐元素迴圈一律不記），且 <c>f5c16cc</c> 後測試的假錯誤不再佔用額度，
    /// 故不調高 <see cref="MaxLogBytes"/>。
    ///
    /// <b>不截斷堆疊</b>：Revit 外掛最有用的往往正是深處的框架（炸在哪個 API 呼叫），
    /// 截斷要訂「留幾行」而那是無依據的魔術值（同兩個解析器不加容量提示的理由）；
    /// 檔案大小本就有輪替把關，不需要在這裡再設一道。
    ///
    /// <b>已知上限</b>：<c>Build-Package.ps1</c> 打包時刻意排除 <c>.pdb</c>，故現場的堆疊**只有
    /// 方法名與呼叫鏈、沒有檔案行號**（已以 <c>DebugType=none</c> 實測確認）。仍足以定位到
    /// 「哪個方法、經由哪條路徑炸的」，本項要的正是這個；要行號得改成打包隨附 PDB，
    /// 那是獨立的部署決策，不在此處順手決定。
    ///
    /// 多行不影響既有的判讀方式：<paramref name="context"/> 仍在帶時間戳的首行、可照常 grep
    /// （2-3 的指紋 <c>Dictionary`2</c> 亦然），堆疊的續行以空白起頭，與新一則的起頭天然可辨。
    ///
    /// 抽成純函式而非寫在 <see cref="Log(string, Exception)"/> 內：<see cref="Log(string)"/> 把去處寫死在
    /// %AppData%，測試不能碰，這段格式化因此從來沒有測試守著——抽出後才驗得到。</summary>
    internal static string Describe(string context, Exception ex) => $"{context}: {ex}";

    /// <summary>實際寫入邏輯（輪替＋附加），路徑與輪替門檻參數化以便單元測試（勿在正式碼直接呼叫，
    /// 改用會吞例外的 <see cref="Log(string)"/>）。此方法「不」吞例外，讓測試能觀察到失敗。</summary>
    internal static void WriteTo(string logDir, string message, long maxBytes)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(logDir, "log.txt");

            // 輪替為 log.old.txt（單世代）而非直接清空——重置當下往往正是最需要近期紀錄的時候。
            if (File.Exists(logPath) && new FileInfo(logPath).Length > maxBytes)
            {
                var oldPath = Path.Combine(logDir, "log.old.txt");
                try
                {
                    // 跨行程競態防護：兩個 Revit 可能同時越過大小檢查。原本「先 Delete(oldPath) 再
                    // Move」會讓後到的行程刪掉前者剛輪替保存的那一代 log.old.txt；改用 File.Replace
                    // 原子替換（oldPath ← logPath 的內容、logPath 隨即刪除），就不再刪除他人的成果。
                    // 整段包進 try：若來源已被另一行程搶先移走，Replace／Move 會擲例外——此時放棄本次
                    // 輪替、直接續寫現行檔即可，不讓一次輪替競態把這行訊息吞掉（跨行程互斥的完整驗證
                    // 需兩個行程，非單元測試所能決定性重現）。
                    if (File.Exists(oldPath))
                        File.Replace(logPath, oldPath, null);
                    else
                        File.Move(logPath, oldPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 另一行程已搶先輪替（logPath 被移走）或檔案短暫被鎖：放棄本次輪替，續寫即可。
                }
            }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line, new UTF8Encoding(true));
        }
    }
}
