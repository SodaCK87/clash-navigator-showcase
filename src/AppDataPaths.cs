using System;
using System.IO;

namespace ClashNavigatorAddin.Services;

/// <summary>集中本外掛在 %AppData% 下的儲存路徑，避免各 Store／Logger 各自重複組合
/// <c>Environment.GetFolderPath(ApplicationData)</c> + "ClashNavigatorAddin"（原散落 5 處）。</summary>
internal static class AppDataPaths
{
    /// <summary>%AppData%\ClashNavigatorAddin（所有設定／狀態／記錄檔的根目錄）。</summary>
    internal static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClashNavigatorAddin");

    /// <summary>根目錄下的檔案完整路徑（例如 <c>File("settings.json")</c>）。</summary>
    internal static string File(string fileName) => Path.Combine(Root, fileName);

    /// <summary>根目錄下的子目錄完整路徑（例如 <c>SubDir("detection_results")</c>）。</summary>
    internal static string SubDir(string subDirName) => Path.Combine(Root, subDirName);
}
