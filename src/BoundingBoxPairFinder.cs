using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashNavigatorAddin.Services;

/// <summary>軸對齊邊界框（AABB）的配對粗篩（純幾何運算，不依賴 Revit API，供單元測試直接連結）。
///
/// 主動碰撞檢測的候選配對階段：兩組元素各 N、M 個，逐一比對是 O(N×M)；
/// 改以「排序—掃描」（sort-sweep）：兩組皆依掃描軸 Min 排序後同步推進，
/// 僅將掃描軸區間仍重疊的另一組元素保留在活動清單中比對其餘兩軸，
/// 複雜度約 O((N+M)·log(N+M) + 命中數)，可承受上萬元素的組合。
///
/// 掃描軸自動選「兩組聯集範圍最寬」的軸——掃描的效率取決於元素在掃描軸上的分散程度，
/// 固定掃 X 遇到高塔型模型（Z 向範圍遠大於平面向）時活動清單會偏大而退化。</summary>
internal static class BoundingBoxPairFinder
{
    /// <summary>軸對齊邊界框（以主文件座標系表示的 Min/Max 各軸值）。</summary>
    public readonly struct Box
    {
        public Box(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        public readonly double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    /// <summary>找出 setA × setB 中所有邊界框重疊的索引對（IndexA 對應 setA、IndexB 對應 setB）。
    /// <paramref name="expansion"/> 為「視為重疊的最大間隙」：兩框各軸間隙皆 ≤ 此值即視為候選
    /// （預設 0＝僅實際重疊的硬碰撞候選；供未來公差／軟碰撞檢測擴充）。回傳順序不保證。</summary>
    public static List<(int IndexA, int IndexB)> FindOverlappingPairs(
        IReadOnlyList<Box> setA, IReadOnlyList<Box> setB, double expansion = 0)
    {
        var result = new List<(int, int)>();
        if (setA.Count == 0 || setB.Count == 0)
            return result;

        int sweepAxis = ChooseSweepAxis(setA, setB);

        // 掃描軸區間展平為陣列，供排序、掃描與活動清單剔除使用（避免熱迴圈中依軸分派）。
        var (sweepMinA, sweepMaxA) = SweepIntervals(setA, sweepAxis);
        var (sweepMinB, sweepMaxB) = SweepIntervals(setB, sweepAxis);

        // 依掃描軸 Min 排序的索引（不動原始清單，讓回傳索引仍對應呼叫方的原始順序）。
        var orderA = SortedIndices(sweepMinA);
        var orderB = SortedIndices(sweepMinB);

        // 掃描線由小至大：活動清單保存「掃描軸區間可能仍與後續元素重疊」的另一組索引，
        // 每次比對時順帶剔除掃描軸 Max 已落在掃描線之前者（延遲清除）。
        var activeA = new List<int>();
        var activeB = new List<int>();
        int ia = 0, ib = 0;

        while (ia < orderA.Length || ib < orderB.Length)
        {
            double nextA = ia < orderA.Length ? sweepMinA[orderA[ia]] : double.PositiveInfinity;
            double nextB = ib < orderB.Length ? sweepMinB[orderB[ib]] : double.PositiveInfinity;

            if (nextA <= nextB)
            {
                int indexA = orderA[ia++];
                MatchAgainstActive(indexA, setA[indexA], sweepMinA[indexA],
                    activeB, setB, sweepMaxB, sweepAxis, expansion, result, aIsFirst: true);
                activeA.Add(indexA);
            }
            else
            {
                int indexB = orderB[ib++];
                MatchAgainstActive(indexB, setB[indexB], sweepMinB[indexB],
                    activeA, setA, sweepMaxA, sweepAxis, expansion, result, aIsFirst: false);
                activeB.Add(indexB);
            }
        }

        return result;
    }

    /// <summary>將新進入掃描線的邊界框與另一組的活動清單比對；
    /// 活動清單中掃描軸區間已結束（Max 落在掃描線之前）者順帶剔除（swap-remove，順序無關緊要）。
    /// 進入活動清單的時點保證 other.Min ≤ current.Min，加上未被剔除保證 other.Max ≥ current.Min，
    /// 故掃描軸重疊已成立，只需再比對其餘兩軸。</summary>
    private static void MatchAgainstActive(
        int currentIndex, Box current, double currentSweepMin,
        List<int> activeOther, IReadOnlyList<Box> otherSet, double[] otherSweepMax,
        int sweepAxis, double expansion,
        List<(int, int)> result, bool aIsFirst)
    {
        for (int k = activeOther.Count - 1; k >= 0; k--)
        {
            int otherIndex = activeOther[k];

            if (otherSweepMax[otherIndex] + expansion < currentSweepMin)
            {
                activeOther[k] = activeOther[activeOther.Count - 1];
                activeOther.RemoveAt(activeOther.Count - 1);
                continue;
            }

            if (OverlapsOtherAxes(current, otherSet[otherIndex], sweepAxis, expansion))
                result.Add(aIsFirst ? (currentIndex, otherIndex) : (otherIndex, currentIndex));
        }
    }

    /// <summary>比對掃描軸以外的兩軸是否重疊（掃描軸的重疊已由掃描線保證）。</summary>
    private static bool OverlapsOtherAxes(Box a, Box b, int sweepAxis, double expansion)
    {
        if (sweepAxis != 0 && (a.MinX > b.MaxX + expansion || a.MaxX + expansion < b.MinX))
            return false;
        if (sweepAxis != 1 && (a.MinY > b.MaxY + expansion || a.MaxY + expansion < b.MinY))
            return false;
        if (sweepAxis != 2 && (a.MinZ > b.MaxZ + expansion || a.MaxZ + expansion < b.MinZ))
            return false;
        return true;
    }

    /// <summary>選擇掃描軸（0=X、1=Y、2=Z）：取兩組邊界框聯集範圍最寬的軸。
    /// 亦供 <see cref="ClashDetectionEngine"/> 取得同一個軸，將候選配對依空間位置排序
    /// （兩者的理由相同：元素在最寬軸上最分散，沿該軸切分最能區隔出局部鄰域）。</summary>
    internal static int ChooseSweepAxis(IReadOnlyList<Box> setA, IReadOnlyList<Box> setB)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        void Accumulate(IReadOnlyList<Box> set)
        {
            for (int i = 0; i < set.Count; i++)
            {
                var b = set[i];
                if (b.MinX < minX) minX = b.MinX;
                if (b.MinY < minY) minY = b.MinY;
                if (b.MinZ < minZ) minZ = b.MinZ;
                if (b.MaxX > maxX) maxX = b.MaxX;
                if (b.MaxY > maxY) maxY = b.MaxY;
                if (b.MaxZ > maxZ) maxZ = b.MaxZ;
            }
        }

        Accumulate(setA);
        Accumulate(setB);

        double spanX = maxX - minX, spanY = maxY - minY, spanZ = maxZ - minZ;
        if (spanZ > spanX && spanZ > spanY) return 2;
        return spanY > spanX ? 1 : 0;
    }

    /// <summary>依鍵值升序排序的索引（0..n-1）。
    /// 以 <c>Array.Sort(keys, items)</c> 取代 <c>Enumerable.Range(…).OrderBy(…).ToArray()</c>：
    /// 後者需 LINQ 的緩衝與 key selector delegate，前者走 double 的內建比較路徑、零 delegate 配置。
    /// 鍵先複製一份再排序——<c>Array.Sort</c> 會就地重排鍵陣列，而呼叫端後續仍需原始的掃描區間。
    ///
    /// 排序穩定性不影響結果：同組內兩個掃描軸 Min 相同的元素，其先後只決定各自何時進入活動清單，
    /// 而配對一律由「另一組的新元素」對活動清單比對產生，故每組配對仍恰好被找到一次。</summary>
    private static int[] SortedIndices(double[] sweepMin)
    {
        var indices = new int[sweepMin.Length];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = i;

        var keys = (double[])sweepMin.Clone();
        Array.Sort(keys, indices);
        return indices;
    }

    /// <summary>取出各邊界框在指定軸上的 Min/Max 區間（展平為陣列）。</summary>
    private static (double[] Min, double[] Max) SweepIntervals(IReadOnlyList<Box> set, int axis)
    {
        var min = new double[set.Count];
        var max = new double[set.Count];

        for (int i = 0; i < set.Count; i++)
        {
            var b = set[i];
            (min[i], max[i]) = axis switch
            {
                0 => (b.MinX, b.MaxX),
                1 => (b.MinY, b.MaxY),
                _ => (b.MinZ, b.MaxZ)
            };
        }

        return (min, max);
    }
}
