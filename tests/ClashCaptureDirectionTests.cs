using System;
using System.Collections.Generic;
using System.Linq;
using ClashNavigatorAddin.Models;
using Xunit;

namespace ClashNavigatorAddin.Tests;

/// <summary>截圖視角方向的清單本身。
///
/// <b>為什麼這些值得測</b>：這一組資料錯了**不會擲例外**，只會靜默產出「名字寫著俯視、
/// 圖卻是仰視」的報告，或是讓使用者存好的選擇在下次啟動時無聲消失。
/// 兩件事離線就驗得掉：向量的幾何性質（正交、非零、不重複），以及鍵的解析規則。</summary>
public class ClashCaptureDirectionTests
{
    [Fact]
    public void All_ContainsCurrentViewFirst_SoTheDefaultIsAlsoTheFirstThingUsersSee()
    {
        Assert.Equal(ClashCaptureDirection.CurrentViewKey, ClashCaptureDirection.All[0].Key);
        Assert.True(ClashCaptureDirection.All[0].KeepsCurrentOrientation);
    }

    [Fact]
    public void All_KeysAreUnique_BecauseTheyIndexSettingsAndExcelColumns()
    {
        var keys = ClashCaptureDirection.All.Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void All_DisplayNamesAreUnique_BecauseTheyBecomeExcelColumnHeaders()
    {
        var names = ClashCaptureDirection.All.Select(d => d.DisplayName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Revit 的 <c>ViewOrientation3D</c> 會拒絕「Up 與 Forward 不正交」的組合，
    /// 而那個拒絕發生在執行期、在使用者的機器上。等角方向最容易寫錯——直覺會塞 (0,0,1)。</summary>
    [Fact]
    public void OrientedDirections_HaveNonZeroPerpendicularVectors()
    {
        foreach (var direction in ClashCaptureDirection.All.Where(d => !d.KeepsCurrentOrientation))
        {
            double forwardLength = Length(direction.ForwardX, direction.ForwardY, direction.ForwardZ);
            double upLength = Length(direction.UpX, direction.UpY, direction.UpZ);

            Assert.True(forwardLength > 0, direction.Key + " 的 Forward 是零向量");
            Assert.True(upLength > 0, direction.Key + " 的 Up 是零向量");

            double dot = (direction.ForwardX * direction.UpX
                          + direction.ForwardY * direction.UpY
                          + direction.ForwardZ * direction.UpZ)
                         / (forwardLength * upLength);

            Assert.True(Math.Abs(dot) < 1e-9,
                $"{direction.Key} 的 Up 與 Forward 不正交（正規化內積 {dot}）");
        }
    }

    /// <summary>兩個方向的視線一樣＝兩張一模一樣的圖、付兩次錢，而 Excel 上只看得出「有兩欄很像」。</summary>
    [Fact]
    public void OrientedDirections_PointDifferentWays()
    {
        var seen = new List<ClashCaptureDirection>();
        foreach (var direction in ClashCaptureDirection.All.Where(d => !d.KeepsCurrentOrientation))
        {
            foreach (var other in seen)
            {
                double dot = Dot(direction, other);
                Assert.True(dot < 1 - 1e-9,
                    $"{direction.Key} 與 {other.Key} 的視線方向相同");
            }

            seen.Add(direction);
        }
    }

    /// <summary>使用者選定的組合：四個等角＋六面正視，加上「維持目前視角」共十一項。
    /// 釘住數量與鍵，免得日後有人「順手」刪掉一個而沒有人發現。</summary>
    [Fact]
    public void All_IsTheAgreedElevenEntries()
    {
        Assert.Equal(11, ClashCaptureDirection.All.Count);
        Assert.Equal(
            new[] { "current", "iso-ne", "iso-se", "iso-sw", "iso-nw", "top", "bottom", "front", "back", "left", "right" },
            ClashCaptureDirection.All.Select(d => d.Key).ToArray());
    }

    [Fact]
    public void Resolve_KeepsAllOrder_NotTheOrderTheUserTickedThem()
    {
        var resolved = ClashCaptureDirection.Resolve(new[] { "right", "iso-ne", "top" });
        Assert.Equal(new[] { "iso-ne", "top", "right" }, resolved.Select(d => d.Key).ToArray());
    }

    [Fact]
    public void Resolve_DropsUnknownKeys_SoAHandEditedSettingsFileCannotProduceNulls()
    {
        var resolved = ClashCaptureDirection.Resolve(new[] { "iso-ne", "no-such-direction", "" });
        Assert.Equal(new[] { "iso-ne" }, resolved.Select(d => d.Key).ToArray());
    }

    [Fact]
    public void Resolve_Deduplicates_SoTheSameAngleIsNeverCapturedTwice()
    {
        var resolved = ClashCaptureDirection.Resolve(new[] { "top", "top", "top" });
        Assert.Single(resolved);
        Assert.Equal("top", resolved[0].Key);
    }

    /// <summary>空清單會讓整份匯出一張圖都沒有，而那份 xlsx 看起來是「成功」的
    /// ——每一列只是寫著「（未擷取）」。所以絕不回空。</summary>
    [Fact]
    public void Resolve_EmptySelectionFallsBackToCurrentView_RatherThanReturningNothing()
        => AssertFallsBackToCurrentView(Array.Empty<string>());

    [Fact]
    public void Resolve_AllKeysUnknownFallsBackToCurrentView_RatherThanReturningNothing()
        => AssertFallsBackToCurrentView(new[] { "no-such-direction" });

    private static void AssertFallsBackToCurrentView(string[] keys)
    {
        var resolved = ClashCaptureDirection.Resolve(keys);
        Assert.Single(resolved);
        Assert.Equal(ClashCaptureDirection.CurrentViewKey, resolved[0].Key);
    }

    [Fact]
    public void Resolve_NullBehavesLikeEmpty()
    {
        var resolved = ClashCaptureDirection.Resolve(null);
        Assert.Single(resolved);
        Assert.Equal(ClashCaptureDirection.CurrentViewKey, resolved[0].Key);
    }

    [Fact]
    public void DefaultKeys_IsCurrentViewOnly_SoExistingUsersSeeNoChange()
    {
        Assert.Equal(new[] { ClashCaptureDirection.CurrentViewKey }, ClashCaptureDirection.DefaultKeys.ToArray());
    }

    private static double Length(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);

    private static double Dot(ClashCaptureDirection a, ClashCaptureDirection b)
    {
        double la = Length(a.ForwardX, a.ForwardY, a.ForwardZ);
        double lb = Length(b.ForwardX, b.ForwardY, b.ForwardZ);
        return (a.ForwardX * b.ForwardX + a.ForwardY * b.ForwardY + a.ForwardZ * b.ForwardZ) / (la * lb);
    }
}
