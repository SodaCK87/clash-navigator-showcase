using System.Runtime.InteropServices;

using Xunit;

namespace ClashNavigatorAddin.Tests;

/// <summary>只在 Windows 上執行的測試。
///
/// <b>需要它的原因是「檔案唯讀」在兩個平台語意不同，不是這段程式碼有平台相依。</b>
/// Windows 的 <c>ReadOnly</c> 屬性會讓「取代該檔」失敗；POSIX 管的則是**目錄**的權限位元，
/// 檔案自己沒有寫入權，仍然可以被 rename 蓋過去——於是同一段程式碼在 Linux 上不擲例外。
///
/// 這個增益集的宿主是 Revit，只跑在 Windows，所以斷言的就是 Windows 的行為；
/// Linux 上把這一個案例跳過，**而不是把斷言放寬到兩邊都成立**——那會變成什麼都沒驗到。
/// 其餘 127 個案例兩個平台都跑，這正是「邏輯已經抽離宿主 API」的證明。</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "檔案唯讀屬性的語意是 Windows 專屬的（見 WindowsOnlyFactAttribute 的註解）。";
        }
    }
}
