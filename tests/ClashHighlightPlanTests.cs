using System;
using ClashNavigatorAddin.ViewModels;
using Xunit;

namespace ClashNavigatorAddin.Tests;

/// <summary>標示的裁決規則：四個使用者設定 → 四個實際寫進 <c>OverrideGraphicSettings</c> 的值。
///
/// <b>這組測試釘的是兩條互斥規則的方向</b>，而它們方向相反、很容易被「順手統一」成同一邊：
/// 「淡化」從屬於「其他元素半透明」（主開關關掉時淡化也不生效），
/// 「碰撞元素半透明」從屬於「碰撞元素上色」但**必須歸零而非沿用**——不上色時碰撞元素要維持不透明，
/// 否則「我不要標示」的結果是碰撞元素比周圍更難看見。
///
/// <b>還有一條不是視覺規則的</b>：<see cref="ClashHighlightPlan.ShouldApply"/> 只看兩個主開關，
/// 不看算出來的值。它同時負責「把前一筆的覆寫歸零」，改成看有效值就會留下清不掉的殘留。</summary>
public class ClashHighlightPlanTests
{
    /// <summary>使用者把兩組都開好時的完整狀態（＝這次改動的目標長相）。</summary>
    [Fact]
    public void From_BothGroupsOn_AppliesAllFourDimensions()
    {
        var plan = ClashHighlightPlan.From(
            highlightOthers: true, transparencyPercent: 80, halftoneOthers: true,
            colorClashElements: true, clashTransparencyPercent: 30);

        Assert.Equal(80, plan.OtherTransparencyPercent);
        Assert.True(plan.OtherHalftone);
        Assert.Equal(30, plan.ClashTransparencyPercent);
        Assert.True(plan.ColorClash);
        Assert.True(plan.ShouldApply);
    }

    // ---- 規則 1：淡化從屬於「其他元素半透明」 --------------------------------------

    [Fact]
    public void From_HighlightOthersOff_SuppressesHalftoneEvenWhenChecked()
    {
        var plan = ClashHighlightPlan.From(
            highlightOthers: false, transparencyPercent: 80, halftoneOthers: true,
            colorClashElements: true, clashTransparencyPercent: 30);

        Assert.Equal(0, plan.OtherTransparencyPercent);
        Assert.False(plan.OtherHalftone);
    }

    [Fact]
    public void From_HighlightOthersOnHalftoneOff_KeepsTransparencyOnly()
    {
        var plan = ClashHighlightPlan.From(
            highlightOthers: true, transparencyPercent: 80, halftoneOthers: false,
            colorClashElements: false, clashTransparencyPercent: 30);

        Assert.Equal(80, plan.OtherTransparencyPercent);
        Assert.False(plan.OtherHalftone);
    }

    // ---- 規則 2：碰撞元素半透明從屬於上色，且**方向相反**（不上色時歸零，不是沿用）------

    /// <summary>不上色時碰撞元素必須維持不透明。沿用設定值的話，「關掉標示」會讓碰撞元素
    /// 變成半透明而周圍不透明——把功能關掉反而比開著更難看見那兩個元素。</summary>
    [Fact]
    public void From_ColorClashOff_ForcesClashElementsOpaque()
    {
        var plan = ClashHighlightPlan.From(
            highlightOthers: true, transparencyPercent: 80, halftoneOthers: true,
            colorClashElements: false, clashTransparencyPercent: 60);

        Assert.Equal(0, plan.ClashTransparencyPercent);
        Assert.False(plan.ColorClash);
    }

    /// <summary>上色開著但透明度設 0＝原本的行為（不透明的紅／綠），必須仍然可達。</summary>
    [Fact]
    public void From_ColorClashOnZeroTransparency_KeepsPreviousBehaviour()
    {
        var plan = ClashHighlightPlan.From(
            highlightOthers: false, transparencyPercent: 80, halftoneOthers: true,
            colorClashElements: true, clashTransparencyPercent: 0);

        Assert.Equal(0, plan.ClashTransparencyPercent);
        Assert.True(plan.ColorClash);
    }

    // ---- 夾限 ---------------------------------------------------------------------

    /// <summary>碰撞元素的上限比其他元素低（90 vs 100）：100% 會讓整張圖唯一要看的東西消失。</summary>
    [Fact]
    public void From_ClashTransparencyAboveNinety_ClampsToNinety()
    {
        var plan = ClashHighlightPlan.From(true, 100, true, true, 100);

        Assert.Equal(100, plan.OtherTransparencyPercent);
        Assert.Equal(90, plan.ClashTransparencyPercent);
    }

    [Fact]
    public void From_NegativePercents_ClampToZero()
    {
        var plan = ClashHighlightPlan.From(true, -10, false, true, -5);

        Assert.Equal(0, plan.OtherTransparencyPercent);
        Assert.Equal(0, plan.ClashTransparencyPercent);
    }

    [Fact]
    public void From_OtherTransparencyAboveHundred_ClampsToHundred()
    {
        var plan = ClashHighlightPlan.From(true, 250, false, false, 0);

        Assert.Equal(100, plan.OtherTransparencyPercent);
    }

    /// <summary>NaN（設定檔被手改壞）不能一路傳進 Revit API：<c>(int)double.NaN</c> 未定義，
    /// 得到的會是一個「值來自哪裡完全查不出來」的透明度。</summary>
    [Fact]
    public void From_NaNPercents_TreatedAsZero()
    {
        var plan = ClashHighlightPlan.From(true, double.NaN, true, true, double.NaN);

        Assert.Equal(0, plan.OtherTransparencyPercent);
        Assert.Equal(0, plan.ClashTransparencyPercent);
        Assert.True(plan.OtherHalftone);
    }

    /// <summary>小數截斷而非四捨五入——Revit 的透明度是整數，而這裡只要求「別擲例外、別溢位」。</summary>
    [Fact]
    public void From_FractionalPercent_Truncates()
    {
        var plan = ClashHighlightPlan.From(true, 79.9, false, true, 30.9);

        Assert.Equal(79, plan.OtherTransparencyPercent);
        Assert.Equal(30, plan.ClashTransparencyPercent);
    }

    // ---- ShouldApply：只看主開關 ---------------------------------------------------

    /// <summary>透明度調成 0、淡化也關掉時**仍然要跑這一輪**——它同時負責歸零前一筆的覆寫。
    /// 改成看有效值的話，使用者把 80 調成 0 再跳轉會留下上一筆的半透明，
    /// 而除了「清除」鈕之外沒有任何東西會來清它。</summary>
    [Fact]
    public void ShouldApply_TrueWhenMasterSwitchOnEvenWithNoVisibleEffect()
    {
        var plan = ClashHighlightPlan.From(
            highlightOthers: true, transparencyPercent: 0, halftoneOthers: false,
            colorClashElements: false, clashTransparencyPercent: 0);

        Assert.Equal(0, plan.OtherTransparencyPercent);
        Assert.False(plan.OtherHalftone);
        Assert.False(plan.ColorClash);
        Assert.True(plan.ShouldApply);
    }

    [Fact]
    public void ShouldApply_FalseOnlyWhenBothMasterSwitchesOff()
    {
        Assert.False(ClashHighlightPlan.From(false, 80, true, false, 30).ShouldApply);
        Assert.True(ClashHighlightPlan.From(true, 80, true, false, 30).ShouldApply);
        Assert.True(ClashHighlightPlan.From(false, 80, true, true, 30).ShouldApply);
    }

    // ---- Describe：狀態列那句話 ----------------------------------------------------

    /// <summary>原本是固定一句「已將其他元素設為半透明」，而那句在「只開上色」時是錯的
    /// ——那次根本沒有半透明，使用者卻被告知有。</summary>
    [Fact]
    public void Describe_ColorOnly_DoesNotClaimTransparency()
    {
        var text = ClashHighlightPlan.From(false, 80, true, true, 0).Describe();

        Assert.DoesNotContain("其他元素", text);
        Assert.Contains("碰撞元素", text);
        Assert.Contains("A 紅", text);
    }

    [Fact]
    public void Describe_BothGroupsOn_MentionsAllFour()
    {
        var text = ClashHighlightPlan.From(true, 80, true, true, 30).Describe();

        Assert.Contains("80%", text);
        Assert.Contains("淡化", text);
        Assert.Contains("A 紅", text);
        Assert.Contains("30%", text);
    }

    /// <summary>淡化勾著但主開關關掉時，說明不能提到淡化——否則使用者會以為它生效了。</summary>
    [Fact]
    public void Describe_HalftoneSuppressed_DoesNotMentionIt()
    {
        var text = ClashHighlightPlan.From(false, 80, true, true, 30).Describe();

        Assert.DoesNotContain("淡化", text);
    }

    /// <summary>設定摘要在「都沒有效果」時講的是**狀態**（標示關閉），不是動作。</summary>
    [Fact]
    public void Describe_NothingEffective_ReportsStateNotAction()
    {
        var text = ClashHighlightPlan.From(true, 0, false, false, 0).Describe();

        Assert.Equal("標示全部關閉", text);
    }

    /// <summary>狀態列在「都沒有效果」時講的是**動作**（已清除）而不是留白——呼叫端在這種情況下
    /// 仍然跑了整輪歸零（見 <see cref="ClashHighlightPlan.ShouldApply"/>），那是有發生事情的。
    /// 這是 <c>Describe</c> 與 <c>DescribeApplied</c> 分成兩支的唯一理由。</summary>
    [Fact]
    public void DescribeApplied_NothingEffective_SaysCleared()
    {
        var text = ClashHighlightPlan.From(true, 0, false, false, 0).DescribeApplied();

        Assert.Contains("已清除", text);
    }

    [Fact]
    public void DescribeApplied_SomethingEffective_SaysApplied()
    {
        var text = ClashHighlightPlan.From(true, 80, true, true, 30).DescribeApplied();

        Assert.Contains("已套用", text);
        Assert.Contains("淡化", text);
    }

    /// <summary>逐段版與整句版必須同源：<c>Describe</c> 就是把 <c>DescribeParts</c> 接起來。
    /// 兩邊各寫一份的話，同一個狀態在 Expander 標題與它的 ToolTip 裡會講得不一樣。</summary>
    [Fact]
    public void DescribeParts_IsWhatDescribeJoins()
    {
        var plan = ClashHighlightPlan.From(true, 30, true, true, 30);

        var parts = plan.DescribeParts();

        Assert.Equal(2, parts.Count);
        foreach (var part in parts)
            Assert.Contains(part, plan.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeParts_NothingEffective_IsEmpty()
    {
        Assert.Empty(ClashHighlightPlan.From(true, 0, false, false, 0).DescribeParts());
    }

    /// <summary>縮寫版**要短**（面板標題只容得下約 40 個字），但**四個維度該講的仍要講**
    /// ——縮的是詞不是項目。砍掉一個維度，收合狀態下就看不見自己開著什麼。</summary>
    [Fact]
    public void DescribeShort_KeepsEveryDimensionButIsShorterThanDescribe()
    {
        var plan = ClashHighlightPlan.From(true, 30, true, true, 30);

        var text = plan.DescribeShort();

        Assert.Contains("其他", text, StringComparison.Ordinal);
        Assert.Contains("30%", text, StringComparison.Ordinal);
        Assert.Contains("淡化", text, StringComparison.Ordinal);
        Assert.Contains("碰撞", text, StringComparison.Ordinal);
        Assert.Contains("紅綠", text, StringComparison.Ordinal);
        Assert.True(text.Length < plan.Describe().Length, "縮寫版不該比完整版長：" + text);
    }

    /// <summary>只開一半時，縮寫版也只講一半（與 <c>Describe</c> 同樣講有效值）。</summary>
    [Fact]
    public void DescribeShort_OnlyColoring_DoesNotMentionOthers()
    {
        var text = ClashHighlightPlan.From(false, 80, true, true, 0).DescribeShort();

        Assert.DoesNotContain("其他", text, StringComparison.Ordinal);
        Assert.Contains("碰撞 紅綠", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeShort_NothingEffective_SaysAllOff()
    {
        Assert.Equal("標示全關", ClashHighlightPlan.From(true, 0, false, false, 0).DescribeShort());
    }
}
