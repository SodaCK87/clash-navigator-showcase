using ClashNavigatorAddin.Services;
using Xunit;

namespace ClashNavigatorAddin.Tests;

/// <summary>取景焦點區域。
///
/// <b>這組測試存在的理由是一個實際看到的缺陷</b>：2026-08-07 在 Revit 內匯出的截圖，
/// 樓板 × 天花板那幾張整片都是線圖，看不出哪裡撞到。成因是取景框取的是**兩個元素邊界框的聯集**
/// ——一整片樓板把框撐到 20 m，使用者把縮放調到 0.01 也沒用。同一份 1550 筆的清單裡
/// **92% 是間距不足**，它們全都走這條路。
///
/// 下面 <c>LongBeamCrossingSmallPipe_FocusesOnTheCrossing_NotTheBeamLength</c> 就是那個情境。</summary>
public class ClashFocusRegionTests
{
    // 單位是英尺（同 IntersectionBoxFeet）。為了讀起來直覺，測資用整數。
    private static double[] Box(double x0, double y0, double z0, double x1, double y1, double z1)
        => new[] { x0, y0, z0, x1, y1, z1 };

    // ---- 這一組是修正的動機 ----------------------------------------------------

    [Fact]
    public void LongBeamCrossingSmallPipe_FocusesOnTheCrossing_NotTheBeamLength()
    {
        // 一根 60 英尺長的梁（X 向），與一根 1 英尺見方的管在 X=30 附近交叉。
        var beam = Box(0, 0, 0, 60, 1, 1);
        var pipe = Box(30, -5, 0.5, 31, 5, 1.5);

        var region = ClashFocusRegion.NearContact(beam, pipe);

        Assert.NotNull(region);
        // X 只取交叉的那一段（30~31），**不是梁的全長 0~60**。這正是修正前後的差別。
        Assert.Equal(30, region![0]);
        Assert.Equal(31, region[3]);
        // Y／Z 同樣收到重疊區間。
        Assert.Equal(0, region[1]);
        Assert.Equal(1, region[4]);
        Assert.Equal(0.5, region[2]);
        Assert.Equal(1, region[5]);

        // 決定性的一條：X 向的尺寸從 60 收成 1，取景框才可能被使用者的縮放控制住。
        Assert.Equal(1, region[3] - region[0]);
    }

    [Fact]
    public void ClearanceClash_FocusesOnTheGap_NotTheTwoElements()
    {
        // 兩根平行管（X 向各 40 英尺長），Z 向相距 0.5 英尺——典型的「間距不足」。
        var lower = Box(0, 0, 0, 40, 1, 1);
        var upper = Box(0, 0, 1.5, 40, 1, 2.5);

        var region = ClashFocusRegion.NearContact(lower, upper);

        Assert.NotNull(region);
        // Z 是那道縫本身（1.0~1.5），不是兩根管的總高（0~2.5）。
        Assert.Equal(1, region![2]);
        Assert.Equal(1.5, region[5]);
        // X／Y 完全重疊，照常取重疊區間。
        Assert.Equal(0, region[0]);
        Assert.Equal(40, region[3]);
    }

    // ---- 逐軸的三種情形 --------------------------------------------------------

    [Fact]
    public void FullyOverlapping_ReturnsTheIntersection()
    {
        var a = Box(0, 0, 0, 10, 10, 10);
        var b = Box(4, 4, 4, 20, 20, 20);

        var region = ClashFocusRegion.NearContact(a, b);

        Assert.Equal(new double[] { 4, 4, 4, 10, 10, 10 }, region);
    }

    [Fact]
    public void SeparatedOnEveryAxis_ReturnsTheDiagonalGap()
    {
        var a = Box(0, 0, 0, 1, 1, 1);
        var b = Box(5, 5, 5, 6, 6, 6);

        var region = ClashFocusRegion.NearContact(a, b);

        Assert.Equal(new double[] { 1, 1, 1, 5, 5, 5 }, region);
    }

    [Fact]
    public void CoincidentBoxes_ReturnTheSameBox()
    {
        // TempModelA 裡真的有一疊完全重合的「一般模型: 1pick」（2026-08-07 匯出時，
        // 55 筆不同配對得到位元組相同的截圖，就是它們）。這種情形不該退化成空區間或負尺寸。
        var a = Box(1, 2, 3, 4, 5, 6);

        var region = ClashFocusRegion.NearContact(a, Box(1, 2, 3, 4, 5, 6));

        Assert.Equal(a, region);
    }

    [Fact]
    public void TouchingExactly_ReturnsAZeroThicknessSlice()
    {
        // 恰好相接：重疊區間退化成一個面。合法（lo == hi），呼叫端的 Math.Max 會用設定的範圍撐開。
        var a = Box(0, 0, 0, 1, 1, 1);
        var b = Box(1, 0, 0, 2, 1, 1);

        var region = ClashFocusRegion.NearContact(a, b);

        Assert.NotNull(region);
        Assert.Equal(1, region![0]);
        Assert.Equal(1, region[3]);
    }

    [Fact]
    public void ResultIsAlwaysWellOrdered()
    {
        // 不論重疊或分離，min 一定不大於 max——負尺寸的框餵給 Revit 會得到一個畫不出來的
        // Section Box，而症狀是「圖是空的」，很難聯想到取景。
        var cases = new[]
        {
            (Box(0, 0, 0, 1, 1, 1), Box(0, 0, 0, 1, 1, 1)),
            (Box(0, 0, 0, 10, 10, 10), Box(5, -5, 20, 6, -4, 21)),
            (Box(-10, -10, -10, -1, -1, -1), Box(1, 1, 1, 2, 2, 2)),
        };

        foreach (var (a, b) in cases)
        {
            var region = ClashFocusRegion.NearContact(a, b);
            Assert.NotNull(region);
            for (int axis = 0; axis < 3; axis++)
                Assert.True(region![axis] <= region[axis + 3], $"軸 {axis} 的 min 大於 max");
        }
    }

    // ---- 輸入正規化與防呆 ------------------------------------------------------

    [Fact]
    public void ReversedMinMax_IsNormalisedFirst()
    {
        // 來源是 Revit 的 BoundingBoxXYZ 經座標轉換，負向的 Transform 會把兩端對調。
        // 不先正規化，整段重疊判斷都會反過來。
        var normal = Box(0, 0, 0, 10, 10, 10);
        var reversed = Box(20, 20, 20, 4, 4, 4);   // 與 (4,4,4)-(20,20,20) 是同一個框

        Assert.Equal(
            ClashFocusRegion.NearContact(normal, Box(4, 4, 4, 20, 20, 20)),
            ClashFocusRegion.NearContact(normal, reversed));
    }

    [Fact]
    public void IsSymmetric()
    {
        var a = Box(0, 0, 0, 10, 2, 2);
        var b = Box(3, 5, 1, 4, 6, 3);

        Assert.Equal(ClashFocusRegion.NearContact(a, b), ClashFocusRegion.NearContact(b, a));
    }

    [Theory]
    [InlineData(null)]
    public void NullInput_ReturnsNull(double[]? bad)
        => Assert.Null(ClashFocusRegion.NearContact(bad, Box(0, 0, 0, 1, 1, 1)));

    [Fact]
    public void MalformedInput_ReturnsNull_SoCallerFallsBackInsteadOfGuessing()
    {
        var good = Box(0, 0, 0, 1, 1, 1);

        Assert.Null(ClashFocusRegion.NearContact(new double[] { 1, 2, 3 }, good));
        Assert.Null(ClashFocusRegion.NearContact(new double[] { 0, 0, 0, 1, 1, 1, 9 }, good));
        Assert.Null(ClashFocusRegion.NearContact(new[] { double.NaN, 0, 0, 1, 1, 1 }, good));
        Assert.Null(ClashFocusRegion.NearContact(new[] { 0, 0, 0, double.PositiveInfinity, 1, 1 }, good));
        Assert.Null(ClashFocusRegion.NearContact(good, new[] { 0, 0, 0, 1, double.NegativeInfinity, 1 }));
    }

    [Fact]
    public void DoesNotMutateItsInputs()
    {
        var a = Box(20, 20, 20, 4, 4, 4);   // 故意給倒過來的
        var b = Box(0, 0, 0, 10, 10, 10);
        var aCopy = (double[])a.Clone();
        var bCopy = (double[])b.Clone();

        ClashFocusRegion.NearContact(a, b);

        Assert.Equal(aCopy, a);
        Assert.Equal(bCopy, b);
    }
}
