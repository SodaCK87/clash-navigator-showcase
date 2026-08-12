using System.Collections.Generic;
using System.Globalization;

namespace ClashNavigatorAddin.ViewModels;

/// <summary>「這一次要把視圖塗成什麼樣子」的裁決：由四個使用者設定算出四個實際要寫進
/// <c>OverrideGraphicSettings</c> 的值。**純函式、不碰 Revit API**，故進得了 <c>Tests\</c>。
///
/// <b>為什麼要抽出來</b>：這些值有兩個入口——跳轉（<c>Views\NavigateEventHandler</c>）與截圖
/// （<c>Services\ClashCaptureSettings.CaptureOne</c>，批次匯出與單筆預覽都走它）。四個設定之間
/// 有兩條互斥規則（見下），各寫一次遲早會有一邊漏掉，而症狀是「畫面上這樣、匯出的圖不一樣」
/// ——那正是預覽這個功能最不能出的錯。同 <c>CaptureOne</c> 的理由：收在一支，兩邊都只能走它。
///
/// <b>兩條規則都不是任意的</b>：
/// <list type="number">
/// <item>「其他元素淡化」（半色調）從屬於「其他元素半透明」。它是同一件事的第二個維度
/// ——都在處置「不是碰撞元素的那些東西」。主開關關掉卻留著淡化，會得到一個
/// 「我明明關了淡化，畫面還是灰的」的狀態，而使用者找不到是誰做的。</item>
/// <item>「碰撞元素半透明」從屬於「碰撞元素上色」。**方向相反才對**：不上色時碰撞元素必須
/// 維持不透明，否則「我不要標示」的結果是碰撞元素變得比周圍更難看見——把功能關掉反而更糟。</item>
/// </list></summary>
internal sealed class ClashHighlightPlan
{
    /// <summary>透明度百分比的下限。</summary>
    public const double MinTransparencyPercent = 0;

    /// <summary>「其他元素」透明度的上限。100 是合理的選擇（上下文全不見）。</summary>
    public const double MaxTransparencyPercent = 100;

    /// <summary>**碰撞元素自身**透明度的上限，刻意比 <see cref="MaxTransparencyPercent"/> 低。
    /// 100% 會讓整張圖唯一要看的東西消失，那是個沒有正當用途的值，
    /// 而且症狀會被誤判成擷取失敗。</summary>
    public const double MaxClashTransparencyPercent = 90;

    // ── 三個界線為什麼定義在這裡、而不是 AppSettings ───────────────────────────────
    // 它們原本住在 AppSettings，但 `Services\SettingsService.cs` 依賴 Revit 型別
    // （DisplayStyle／ViewDetailLevel／ViewDiscipline），**進不了 Tests\**。夾限規則是這支
    // 純函式的核心行為之一，把界線留在那邊就等於「測得到夾限的程式碼、測不到夾限的數字」。
    // 故改由本類別持有，AppSettings 的三個同名常數轉指過來（編譯期常數，零成本），
    // 既有的 UI 參照點（面板兩個輸入框）不受影響。

    private ClashHighlightPlan(
        int otherTransparencyPercent, bool otherHalftone,
        int clashTransparencyPercent, bool colorClash, bool shouldApply)
    {
        OtherTransparencyPercent = otherTransparencyPercent;
        OtherHalftone = otherHalftone;
        ClashTransparencyPercent = clashTransparencyPercent;
        ColorClash = colorClash;
        ShouldApply = shouldApply;
    }

    /// <summary>「非碰撞元素」的表面透明度（0~100）。</summary>
    public int OtherTransparencyPercent { get; }

    /// <summary>「非碰撞元素」是否套用半色調（Revit 的 Halftone，即UI 上叫「淡化」）。</summary>
    public bool OtherHalftone { get; }

    /// <summary>碰撞元素 A／B 的表面透明度（0~<see cref="AppSettings.MaxClashTransparencyPercent"/>）。
    /// 讓前面那個透出後面那個的**輪廓**，答出「誰在前誰在後」——**不是**靠重疊區混色
    /// （2026-08-08 實測更正，混色像素反而是減少的；數字見
    /// <c>Services\AppSettings.ClashTransparencyPercent</c>）。</summary>
    public int ClashTransparencyPercent { get; }

    /// <summary>碰撞元素 A／B 是否上紅／綠。</summary>
    public bool ColorClash { get; }

    /// <summary>這一次該不該跑覆寫這一輪。
    ///
    /// <b>刻意由兩個「主開關」決定，不看算出來的四個值</b>：把透明度調成 0、淡化也關掉時，
    /// 這一輪仍然必須跑——它同時負責「把前一筆的覆寫歸零」。改成看有效值的話，
    /// 使用者把透明度從 80 調成 0 再跳轉會得到「上一筆的半透明留在視圖上、而且沒有東西會來清它」
    /// （清除鈕之外沒有別的出口）。</summary>
    public bool ShouldApply { get; }

    /// <summary>由使用者設定算出這一次的目標狀態。百分比一律夾限，
    /// NaN（設定檔被手改壞）視為 0 而不是讓它一路傳進 Revit API。</summary>
    public static ClashHighlightPlan From(
        bool highlightOthers, double transparencyPercent, bool halftoneOthers,
        bool colorClashElements, double clashTransparencyPercent)
    {
        int other = highlightOthers
            ? Clamp(transparencyPercent, MinTransparencyPercent, MaxTransparencyPercent)
            : 0;

        int clash = colorClashElements
            ? Clamp(clashTransparencyPercent, MinTransparencyPercent, MaxClashTransparencyPercent)
            : 0;

        return new ClashHighlightPlan(
            other,
            highlightOthers && halftoneOthers,
            clash,
            colorClashElements,
            highlightOthers || colorClashElements);
    }

    /// <summary>「目前的標示長什麼樣」的一句話，**中性語氣**，供設定摘要
    /// （面板的 Expander 標題、「檢測紀錄」視窗）。
    ///
    /// <b>不用固定文字</b>：現在有四個維度可開關，而原本那句「已將其他元素設為半透明」在
    /// 「只開上色」時是錯的——那次根本沒有半透明，使用者卻被告知有。
    ///
    /// <b>講的是有效值不是原始設定</b>：淡化勾著但主開關關掉時不能提到淡化
    /// ——那會讓人以為它生效了，而畫面上找不到證據。</summary>
    public string Describe()
    {
        var parts = DescribeParts();
        return parts.Count == 0 ? "標示全部關閉" : string.Join("　·　", parts);
    }

    /// <summary>逐段的說法，供「要換行列出」的地方（面板 Expander 標題的 ToolTip）。
    /// <see cref="Describe"/> 就是把它接起來——**分成兩支只是接法不同，內容必須同源**，
    /// 否則同一個狀態在標題與提示裡會講得不一樣。空清單＝四個維度都沒有效果。</summary>
    public IReadOnlyList<string> DescribeParts()
    {
        var parts = new List<string>();

        if (OtherTransparencyPercent > 0 || OtherHalftone)
        {
            var how = new List<string>();
            if (OtherTransparencyPercent > 0)
                how.Add("半透明 " + OtherTransparencyPercent.ToString(CultureInfo.CurrentCulture) + "%");
            if (OtherHalftone)
                how.Add("淡化");
            parts.Add("其他元素：" + string.Join("＋", how));
        }

        if (ColorClash)
        {
            var how = new List<string> { "上色 A 紅／B 綠" };
            if (ClashTransparencyPercent > 0)
                how.Add("半透明 " + ClashTransparencyPercent.ToString(CultureInfo.CurrentCulture) + "%");
            parts.Add("碰撞元素：" + string.Join("＋", how));
        }

        return parts;
    }

    /// <summary>同一件事的**縮寫版**，供寬度有限的面板 Expander 標題（2026-08-10 使用者回報
    /// 那一行被截成「…」）。完整版由同一個標題的 ToolTip 逐行列出，所以縮寫不會造成資訊遺失。
    ///
    /// <b>縮的是詞不是項目</b>：四個維度該出現的還是都出現（「其他」「碰撞」各一段），
    /// 只是「其他元素：」→「其他」、「半透明」→「半透」、「上色 A 紅／B 綠」→「紅綠」。
    /// 砍掉整個維度的話，收合狀態下就會看不見自己開著什麼——而那正是這行摘要存在的理由。</summary>
    public string DescribeShort()
    {
        var parts = new List<string>();

        if (OtherTransparencyPercent > 0 || OtherHalftone)
        {
            var how = new List<string>();
            if (OtherTransparencyPercent > 0)
                how.Add("半透" + OtherTransparencyPercent.ToString(CultureInfo.CurrentCulture) + "%");
            if (OtherHalftone)
                how.Add("淡化");
            parts.Add("其他 " + string.Join("＋", how));
        }

        if (ColorClash)
        {
            var how = new List<string> { "紅綠" };
            if (ClashTransparencyPercent > 0)
                how.Add(ClashTransparencyPercent.ToString(CultureInfo.CurrentCulture) + "%");
            parts.Add("碰撞 " + string.Join("＋", how));
        }

        return parts.Count == 0 ? "標示全關" : string.Join("・", parts);
    }

    /// <summary>「剛剛套用了什麼」的一句話，供跳轉／擷取後的狀態列。
    ///
    /// <b>與 <see cref="Describe"/> 分成兩支而不是加一個布林參數</b>：兩者在「四個維度都沒有效果」
    /// 那個分支下要講的是**不同的事**——設定摘要講的是狀態（「標示全部關閉」），
    /// 狀態列講的是剛發生的動作（「已清除標示」，因為呼叫端確實跑了整輪歸零，見
    /// <see cref="ShouldApply"/>）。用參數的話，那個分支得在方法裡分岔，讀起來反而更難確認哪句配哪個。</summary>
    public string DescribeApplied() =>
        OtherTransparencyPercent == 0 && !OtherHalftone && !ColorClash
            ? "已清除標示（設定全部關閉）"
            : "已套用標示　" + Describe();

    /// <summary>夾限並取整。NaN 回傳 <paramref name="min"/> 的整數值：
    /// <c>Math.Min</c>／<c>Math.Max</c> 遇 NaN 會把 NaN 一路傳下去，而 <c>(int)double.NaN</c>
    /// 的結果未定義——那會變成一個「值來自哪裡完全查不出來」的透明度。</summary>
    private static int Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value))
            return (int)min;

        if (value < min) return (int)min;
        if (value > max) return (int)max;
        return (int)value;
    }
}
