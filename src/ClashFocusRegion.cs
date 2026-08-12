using System;

namespace ClashNavigatorAddin.Services;

/// <summary>兩個元素之間「真正該看的那一塊」——重疊的部分，或（沒有重疊時）它們最接近的那道縫。
///
/// <b>為什麼需要這個</b>：截圖與跳轉的取景框是
/// <c>max(使用者設定的範圍 ÷ 2, 焦點框半徑)</c>，也就是**焦點框是個下限**。
/// 焦點框原本有兩種來源：
///
/// <list type="number">
/// <item>硬碰撞的 <see cref="Models.ClashItem.IntersectionBoxFeet"/>（交集實體的邊界框）——很小，
///       使用者把縮放調小就真的會放大。</item>
/// <item>**其餘一律退回「兩個元素邊界框的聯集」**——一根 20 m 的梁或一整片樓板會把框撐到 20 m，
///       縮放調到 0.01 也沒用。</item>
/// </list>
///
/// 而第 2 種不是少數情形：2026-08-07 在 TempModelA 上實測的一份 1550 筆清單裡，
/// **1431 筆（92%）是間距不足**，它們一律沒有交集資訊（`IntersectionBoxFeet` 為 null）；
/// 匯入的 Navisworks／Revit 報告也一樣。症狀就是匯出的截圖是一張整片樓板的線圖，
/// 看不出哪裡撞到。
///
/// 本類別提供第 2 種情形的替代焦點：**逐軸取「重疊區間」，沒有重疊就取「兩者之間那一段」**。
/// 對典型的間距碰撞（兩根管在兩個軸上重疊、第三個軸上差幾公分），得到的正是那道縫的所在，
/// 而不是兩根管的全長。
///
/// <b>刻意不碰 Revit API</b>（只吃 <c>double[6]</c>，與 <c>IntersectionBoxFeet</c> 同格式）：
/// 這是純幾何，放進 <c>Tests\</c> 才驗得到——`Services\ClashNavigator` 整支依賴 Revit。
///
/// <b>它不取代交集框</b>：有 <c>IntersectionBoxFeet</c> 時仍以那個為準。交集框是真的算出來的
/// 相交實體，本類別只是邊界框層級的近似（邊界框重疊不代表實體重疊）。</summary>
internal static class ClashFocusRegion
{
    /// <summary>兩個邊界框「重疊或最接近」的那一塊，格式同
    /// <see cref="Models.ClashItem.IntersectionBoxFeet"/>：[minX,minY,minZ,maxX,maxY,maxZ]。
    ///
    /// 任一輸入不合法（長度不是 6、含 NaN／無限大）時回傳 null，呼叫端應退回原本的聯集，
    /// **不要自己編一個框**——取景錯了只會拍到別的地方，比退回舊行為更難察覺。</summary>
    public static double[]? NearContact(double[]? boxA, double[]? boxB)
    {
        if (!IsUsable(boxA) || !IsUsable(boxB))
            return null;

        var result = new double[6];
        for (int axis = 0; axis < 3; axis++)
        {
            // 輸入的框不保證 min<=max（來源是 Revit 的 BoundingBoxXYZ 經座標轉換，
            // 負向的 Transform 會把兩端對調）。先正規化，否則整段判斷都會反過來。
            var (loA, hiA) = Ordered(boxA![axis], boxA[axis + 3]);
            var (loB, hiB) = Ordered(boxB![axis], boxB[axis + 3]);

            var (lo, hi) = AxisNearContact(loA, hiA, loB, hiB);
            result[axis] = lo;
            result[axis + 3] = hi;
        }

        return result;
    }

    /// <summary>單一軸上的「重疊區間」或「兩者之間那一段」。
    ///
    /// 兩種情形都回傳一個 lo ≤ hi 的區間：
    /// <list type="bullet">
    /// <item>有重疊 → 重疊的那一段（<c>[max(loA,loB), min(hiA,hiB)]</c>）。</item>
    /// <item>沒重疊 → 兩者之間的空隙（<c>[較低者的 hi, 較高者的 lo]</c>），
    ///       也就是那道縫本身。間距不足的碰撞恰好落在這裡。</item>
    /// </list>
    ///
    /// 兩種情形的公式其實是同一個，只是端點對調，故不分支寫：
    /// <c>lo = min(重疊 lo, 重疊 hi)</c>、<c>hi = max(...)</c>。</summary>
    private static (double Lo, double Hi) AxisNearContact(double loA, double hiA, double loB, double hiB)
    {
        double overlapLo = Math.Max(loA, loB);
        double overlapHi = Math.Min(hiA, hiB);

        return overlapLo <= overlapHi
            ? (overlapLo, overlapHi)
            : (overlapHi, overlapLo);
    }

    private static (double Lo, double Hi) Ordered(double a, double b) => a <= b ? (a, b) : (b, a);

    private static bool IsUsable(double[]? box)
    {
        if (box == null || box.Length != 6)
            return false;

        foreach (var value in box)
        {
            // NaN／無限大會讓後面的 Math.Max／Min 靜靜傳播下去，最後變成一個
            // Revit 收下但畫不出來的 Section Box。擋在這裡才看得出是資料的問題。
            if (double.IsNaN(value) || double.IsInfinity(value))
                return false;
        }

        return true;
    }
}
