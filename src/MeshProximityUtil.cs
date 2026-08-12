using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClashNavigatorAddin.Services;

/// <summary>三角網格的鄰近（間距）判定（純幾何運算，不依賴 Revit API，供單元測試直接連結）。
///
/// 間距（軟碰撞）檢測的精算階段：兩實體「未相交」時，判斷其表面最短距離是否小於指定間距。
/// Revit API 沒有 Solid 對 Solid 的距離運算，改以曲面三角化（tessellation）後的網格計算：
/// 兩個互不相交的三角形，其最短距離必落在「頂點對三角形」或「邊對邊」之上，
/// 逐對三角形依此判定，配合三角形邊界框預篩與「找到即返回」早退。
///
/// 近似誤差：曲面（圓管等）三角化為弦，計算距離會比真實曲面「略遠估」
/// （誤差約為三角化的弦高，遠小於常用間距值），對間距檢討屬可接受範圍。</summary>
internal static class MeshProximityUtil
{
    /// <summary>三維向量／點（自帶型別以維持不依賴 Revit API）。</summary>
    public readonly struct Vec3
    {
        public Vec3(double x, double y, double z)
        {
            X = x; Y = y; Z = z;
        }

        public readonly double X, Y, Z;

        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public double LengthSquared => X * X + Y * Y + Z * Z;
    }

    /// <summary>三角網格：頂點與三角形索引，建構時預先計算各三角形與整體的邊界框，
    /// 供 <see cref="AreWithinDistance"/> 以邊界框間隙快速略過不可能的組合。</summary>
    public sealed class TriangleMesh
    {
        internal readonly Vec3[] Vertices;
        internal readonly (int I0, int I1, int I2)[] Triangles;

        // 各三角形的邊界框（展平為陣列，避免每次判定重算）
        internal readonly double[] TriMinX, TriMinY, TriMinZ, TriMaxX, TriMaxY, TriMaxZ;
        internal readonly double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public int TriangleCount => Triangles.Length;

        public TriangleMesh(IReadOnlyList<Vec3> vertices, IReadOnlyList<(int I0, int I1, int I2)> triangles)
        {
            Vertices = vertices is Vec3[] va ? va : CopyVertices(vertices);
            Triangles = triangles is (int, int, int)[] ta ? ta : CopyTriangles(triangles);

            int n = Triangles.Length;
            TriMinX = new double[n]; TriMinY = new double[n]; TriMinZ = new double[n];
            TriMaxX = new double[n]; TriMaxY = new double[n]; TriMaxZ = new double[n];

            MinX = MinY = MinZ = double.MaxValue;
            MaxX = MaxY = MaxZ = double.MinValue;

            for (int i = 0; i < n; i++)
            {
                var (i0, i1, i2) = Triangles[i];
                var a = Vertices[i0];
                var b = Vertices[i1];
                var c = Vertices[i2];

                TriMinX[i] = Math.Min(a.X, Math.Min(b.X, c.X));
                TriMinY[i] = Math.Min(a.Y, Math.Min(b.Y, c.Y));
                TriMinZ[i] = Math.Min(a.Z, Math.Min(b.Z, c.Z));
                TriMaxX[i] = Math.Max(a.X, Math.Max(b.X, c.X));
                TriMaxY[i] = Math.Max(a.Y, Math.Max(b.Y, c.Y));
                TriMaxZ[i] = Math.Max(a.Z, Math.Max(b.Z, c.Z));

                MinX = Math.Min(MinX, TriMinX[i]); MinY = Math.Min(MinY, TriMinY[i]); MinZ = Math.Min(MinZ, TriMinZ[i]);
                MaxX = Math.Max(MaxX, TriMaxX[i]); MaxY = Math.Max(MaxY, TriMaxY[i]); MaxZ = Math.Max(MaxZ, TriMaxZ[i]);
            }
        }

        private static Vec3[] CopyVertices(IReadOnlyList<Vec3> source)
        {
            var array = new Vec3[source.Count];
            for (int i = 0; i < source.Count; i++) array[i] = source[i];
            return array;
        }

        private static (int, int, int)[] CopyTriangles(IReadOnlyList<(int, int, int)> source)
        {
            var array = new (int, int, int)[source.Count];
            for (int i = 0; i < source.Count; i++) array[i] = source[i];
            return array;
        }
    }

    /// <summary>兩個網格的表面最短距離是否 ≤ <paramref name="distance"/>（找到任一組合即返回，不求精確最小值）。
    /// 假設兩網格互不相交（呼叫方應先以實體布林運算排除相交情況）。任一網格無三角形時回傳 false。</summary>
    public static bool AreWithinDistance(TriangleMesh a, TriangleMesh b, double distance)
    {
        if (a.TriangleCount == 0 || b.TriangleCount == 0)
            return false;

        double d2 = distance * distance;

        // 網格層級邊界框間隙預篩：整體就已超過間距即可直接排除。
        if (BoxGapSquared(a.MinX, a.MinY, a.MinZ, a.MaxX, a.MaxY, a.MaxZ,
                b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ) > d2)
            return false;

        // 交互區域 = (A 框各向外擴 distance) ∩ (B 框各向外擴 distance)。
        // 可證明：若兩三角形的最短距離 ≤ distance，其最近點必同時落於兩個外擴框內，
        // 因而落於此交集區域；故兩三角形的邊界框都必與此區域相交。
        // 據此同時裁切 A、B 兩側三角形，只保留邊界框與交互區域相交者——
        // 對「兩者都大且交錯」（樓板 × 牆）情境，交互區域是靠近交線的薄盒，
        // 兩側候選都大幅縮小，避免退化為 O(nA×nB) 全配對；此裁切較「對整個對側網格框」更緊。
        double rMinX = Math.Max(a.MinX, b.MinX) - distance;
        double rMinY = Math.Max(a.MinY, b.MinY) - distance;
        double rMinZ = Math.Max(a.MinZ, b.MinZ) - distance;
        double rMaxX = Math.Min(a.MaxX, b.MaxX) + distance;
        double rMaxY = Math.Min(a.MaxY, b.MaxY) + distance;
        double rMaxZ = Math.Min(a.MaxZ, b.MaxZ) + distance;

        var candidatesA = TrianglesOverlappingRegion(a, rMinX, rMinY, rMinZ, rMaxX, rMaxY, rMaxZ);
        if (candidatesA.Count == 0)
            return false;

        var candidatesB = TrianglesOverlappingRegion(b, rMinX, rMinY, rMinZ, rMaxX, rMaxY, rMaxZ);
        if (candidatesB.Count == 0)
            return false;

        // 依候選規模分三層，讓「排序／平行」的固定成本只在划算時才付出：
        //   小：維持原始簡單巢狀迴圈（避免排序與執行緒的固定開銷）。
        //   中：沿「交互區域最大延伸軸」升序排序 B 候選，內層一旦越過該軸即可提前 break
        //       （第 9 項；把「兩大面平行貼近」由 O(nA×nB) 降到接近線性掃描）。
        //   大：同上再以 Parallel.For 分攤純數學運算，任一執行緒命中即 Stop 早退（第 10 項）。
        // 三層皆只跳過「沿軸間隙已 > distance」的組合——這些逐對 3D 預篩本就會濾除，
        // 故結果（是否存在任一組合 ≤ distance 的布林）與原始全配對完全一致，僅更快。
        long pairWork = (long)candidatesA.Count * candidatesB.Count;

        if (pairWork < SpatialPairThreshold)
            return AnyPairWithinDistanceSimple(a, b, candidatesA, candidatesB, d2);

        int axis = LargestExtentAxis(rMinX, rMinY, rMinZ, rMaxX, rMaxY, rMaxZ);
        var (aMinAxis, aMaxAxis) = AxisArrays(a, axis);
        var (bMinAxis, bMaxAxis) = AxisArrays(b, axis);
        var sortedB = SortByAxisMin(candidatesB, bMinAxis);

        if (pairWork >= ParallelPairThreshold && candidatesA.Count >= ParallelMinOuter)
        {
            bool found = false;
            Parallel.For(0, candidatesA.Count, (idx, state) =>
            {
                if (found) { state.Stop(); return; }
                if (TriangleHasNearbyInSorted(a, b, candidatesA[idx], sortedB, d2, distance,
                        aMinAxis, aMaxAxis, bMinAxis, bMaxAxis))
                {
                    found = true;      // 布林旗標；Parallel.For 完成時的屏障保證主執行緒看得到最終值
                    state.Stop();      // 已找到 → 請求其餘執行緒儘早收工
                }
            });
            return found;
        }

        foreach (int i in candidatesA)
        {
            if (TriangleHasNearbyInSorted(a, b, i, sortedB, d2, distance,
                    aMinAxis, aMaxAxis, bMinAxis, bMaxAxis))
                return true;
        }

        return false;
    }

    /// <summary>依 <paramref name="axisMin"/> 升序排序候選三角形索引。
    /// 以 <c>Array.Sort(keys, items)</c> 而非 <c>List.Sort(lambda)</c>：後者每次比較都經 delegate
    /// 間接呼叫並兩次索引 axisMin，且 lambda 捕捉 axisMin 會產生閉包配置；
    /// 而此處位於間距模式的熱路徑（對「每一組配對」執行）。鍵先一次取出成陣列，
    /// 排序即走 double 的內建比較路徑、零 delegate、零閉包。</summary>
    private static int[] SortByAxisMin(List<int> candidates, double[] axisMin)
    {
        var sorted = candidates.ToArray();

        var keys = new double[sorted.Length];
        for (int i = 0; i < keys.Length; i++)
            keys[i] = axisMin[sorted[i]];

        Array.Sort(keys, sorted);
        return sorted;
    }

    // 分層門檻（配對量 = |candidatesA| × |candidatesB|）：小於則簡單迴圈；達平行門檻且外層夠大才平行。
    // 門檻為經驗值，僅影響何時付排序／執行緒的固定成本，不影響結果正確性。
    private const int SpatialPairThreshold = 4096;
    private const long ParallelPairThreshold = 200_000;
    private const int ParallelMinOuter = 16;

    /// <summary>小候選集：原始簡單巢狀迴圈（逐對 3D 邊界框預篩 + 三角形距離），不付排序／平行的固定成本。</summary>
    private static bool AnyPairWithinDistanceSimple(TriangleMesh a, TriangleMesh b,
        List<int> candidatesA, List<int> candidatesB, double d2)
    {
        foreach (int i in candidatesA)
        {
            var (a0, a1, a2) = a.Triangles[i];
            var ta0 = a.Vertices[a0];
            var ta1 = a.Vertices[a1];
            var ta2 = a.Vertices[a2];

            foreach (int j in candidatesB)
            {
                // 逐對三角形邊界框間隙預篩，跳過明顯分離的組合。
                if (BoxGapSquared(a.TriMinX[i], a.TriMinY[i], a.TriMinZ[i], a.TriMaxX[i], a.TriMaxY[i], a.TriMaxZ[i],
                        b.TriMinX[j], b.TriMinY[j], b.TriMinZ[j], b.TriMaxX[j], b.TriMaxY[j], b.TriMaxZ[j]) > d2)
                    continue;

                var (b0, b1, b2) = b.Triangles[j];
                if (TriangleTriangleDistanceSquared(ta0, ta1, ta2,
                        b.Vertices[b0], b.Vertices[b1], b.Vertices[b2]) <= d2)
                    return true;
            }
        }
        return false;
    }

    /// <summary>單一 A 三角形（索引 <paramref name="i"/>）對「沿排序軸升序排列」的 B 候選做鄰近測試：
    /// 沿軸越過 <c>aMax+distance</c> 即 break（升序 → 後續必更遠）、完全落於 A 另一側則 continue，
    /// 其餘走逐對 3D 邊界框預篩 + 三角形距離。找到任一 ≤ distance 即回 true。</summary>
    private static bool TriangleHasNearbyInSorted(TriangleMesh a, TriangleMesh b, int i, int[] sortedB,
        double d2, double distance, double[] aMinAxis, double[] aMaxAxis, double[] bMinAxis, double[] bMaxAxis)
    {
        var (a0, a1, a2) = a.Triangles[i];
        var ta0 = a.Vertices[a0];
        var ta1 = a.Vertices[a1];
        var ta2 = a.Vertices[a2];

        double aMaxPlus = aMaxAxis[i] + distance;
        double aMinMinus = aMinAxis[i] - distance;

        for (int k = 0; k < sortedB.Length; k++)
        {
            int j = sortedB[k];
            if (bMinAxis[j] > aMaxPlus)
                break;                       // 排序軸升序 → 此後皆更遠，早退
            if (bMaxAxis[j] < aMinMinus)
                continue;                    // 沿排序軸完全落於 A 的另一側

            if (BoxGapSquared(a.TriMinX[i], a.TriMinY[i], a.TriMinZ[i], a.TriMaxX[i], a.TriMaxY[i], a.TriMaxZ[i],
                    b.TriMinX[j], b.TriMinY[j], b.TriMinZ[j], b.TriMaxX[j], b.TriMaxY[j], b.TriMaxZ[j]) > d2)
                continue;

            var (b0, b1, b2) = b.Triangles[j];
            if (TriangleTriangleDistanceSquared(ta0, ta1, ta2,
                    b.Vertices[b0], b.Vertices[b1], b.Vertices[b2]) <= d2)
                return true;
        }
        return false;
    }

    /// <summary>取三軸中「延伸最大」者（0=X，1=Y，2=Z）作為排序／掃描軸——延伸最大代表候選在該軸上最分散，
    /// early-break 最有效（例如兩大面平行貼近時，分離軸延伸小、面內兩軸延伸大，取面內軸掃描）。</summary>
    private static int LargestExtentAxis(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        double ex = maxX - minX, ey = maxY - minY, ez = maxZ - minZ;
        if (ex >= ey && ex >= ez) return 0;
        return ey >= ez ? 1 : 2;
    }

    /// <summary>取某網格沿指定軸的三角形邊界框 min／max 陣列。</summary>
    private static (double[] Min, double[] Max) AxisArrays(TriangleMesh m, int axis) => axis switch
    {
        0 => (m.TriMinX, m.TriMaxX),
        1 => (m.TriMinY, m.TriMaxY),
        _ => (m.TriMinZ, m.TriMaxZ)
    };

    /// <summary>選出邊界框與指定區域相交的三角形索引（軸對齊分離測試）。</summary>
    private static List<int> TrianglesOverlappingRegion(TriangleMesh m,
        double rMinX, double rMinY, double rMinZ, double rMaxX, double rMaxY, double rMaxZ)
    {
        var result = new List<int>();
        for (int i = 0; i < m.TriangleCount; i++)
        {
            if (m.TriMaxX[i] < rMinX || m.TriMinX[i] > rMaxX ||
                m.TriMaxY[i] < rMinY || m.TriMinY[i] > rMaxY ||
                m.TriMaxZ[i] < rMinZ || m.TriMinZ[i] > rMaxZ)
                continue;
            result.Add(i);
        }
        return result;
    }

    /// <summary>兩個邊界框的最短間隙平方（重疊時為 0）。</summary>
    private static double BoxGapSquared(
        double aMinX, double aMinY, double aMinZ, double aMaxX, double aMaxY, double aMaxZ,
        double bMinX, double bMinY, double bMinZ, double bMaxX, double bMaxY, double bMaxZ)
    {
        double gx = Math.Max(0, Math.Max(aMinX - bMaxX, bMinX - aMaxX));
        double gy = Math.Max(0, Math.Max(aMinY - bMaxY, bMinY - aMaxY));
        double gz = Math.Max(0, Math.Max(aMinZ - bMaxZ, bMinZ - aMaxZ));
        return gx * gx + gy * gy + gz * gz;
    }

    /// <summary>兩個「互不相交」三角形的最短距離平方：
    /// 最短距離必落在頂點對三角形（6 種）或邊對邊（9 種）之上，取其最小值。
    /// （相交的三角形不在此適用範圍——見 <see cref="AreWithinDistance"/> 的前提。）</summary>
    public static double TriangleTriangleDistanceSquared(
        Vec3 a0, Vec3 a1, Vec3 a2, Vec3 b0, Vec3 b1, Vec3 b2)
    {
        double min = double.MaxValue;

        // 頂點對三角形（雙向）
        min = Math.Min(min, PointTriangleDistanceSquared(a0, b0, b1, b2));
        min = Math.Min(min, PointTriangleDistanceSquared(a1, b0, b1, b2));
        min = Math.Min(min, PointTriangleDistanceSquared(a2, b0, b1, b2));
        min = Math.Min(min, PointTriangleDistanceSquared(b0, a0, a1, a2));
        min = Math.Min(min, PointTriangleDistanceSquared(b1, a0, a1, a2));
        min = Math.Min(min, PointTriangleDistanceSquared(b2, a0, a1, a2));

        // 邊對邊（3×3；手動展開，避免每次呼叫配置陣列——此函式位於雙重迴圈熱路徑）
        min = Math.Min(min, SegmentSegmentDistanceSquared(a0, a1, b0, b1));
        min = Math.Min(min, SegmentSegmentDistanceSquared(a0, a1, b1, b2));
        min = Math.Min(min, SegmentSegmentDistanceSquared(a0, a1, b2, b0));
        min = Math.Min(min, SegmentSegmentDistanceSquared(a1, a2, b0, b1));
        min = Math.Min(min, SegmentSegmentDistanceSquared(a1, a2, b1, b2));
        min = Math.Min(min, SegmentSegmentDistanceSquared(a1, a2, b2, b0));
        min = Math.Min(min, SegmentSegmentDistanceSquared(a2, a0, b0, b1));
        min = Math.Min(min, SegmentSegmentDistanceSquared(a2, a0, b1, b2));
        min = Math.Min(min, SegmentSegmentDistanceSquared(a2, a0, b2, b0));

        return min;
    }

    /// <summary>點對三角形的最短距離平方（Ericson《Real-Time Collision Detection》5.1.5，
    /// 以重心座標分區求三角形上最近點）。退化（近零面積）三角形退回三邊線段距離。</summary>
    public static double PointTriangleDistanceSquared(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;

        double d1 = Vec3.Dot(ab, ap);
        double d2 = Vec3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
            return (p - a).LengthSquared;                      // 區域：頂點 a

        var bp = p - b;
        double d3 = Vec3.Dot(ab, bp);
        double d4 = Vec3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return (p - b).LengthSquared;                      // 區域：頂點 b

        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            double v = d1 / (d1 - d3);                          // 區域：邊 ab
            return (p - (a + ab * v)).LengthSquared;
        }

        var cp = p - c;
        double d5 = Vec3.Dot(ab, cp);
        double d6 = Vec3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return (p - c).LengthSquared;                      // 區域：頂點 c

        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            double w = d2 / (d2 - d6);                          // 區域：邊 ac
            return (p - (a + ac * w)).LengthSquared;
        }

        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));     // 區域：邊 bc
            return (p - (b + (c - b) * w)).LengthSquared;
        }

        double denom = va + vb + vc;
        if (Math.Abs(denom) < 1e-30)
        {
            // 退化三角形（近零面積）：取三邊線段距離的最小值
            return Math.Min(PointSegmentDistanceSquared(p, a, b),
                Math.Min(PointSegmentDistanceSquared(p, b, c), PointSegmentDistanceSquared(p, a, c)));
        }

        double invDenom = 1.0 / denom;
        double vv = vb * invDenom;
        double ww = vc * invDenom;
        return (p - (a + ab * vv + ac * ww)).LengthSquared;    // 區域：三角形內部
    }

    /// <summary>點對線段的最短距離平方。</summary>
    public static double PointSegmentDistanceSquared(Vec3 p, Vec3 a, Vec3 b)
    {
        var ab = b - a;
        double lenSq = ab.LengthSquared;
        if (lenSq < 1e-30)
            return (p - a).LengthSquared;

        double t = Vec3.Dot(p - a, ab) / lenSq;
        t = Math.Max(0, Math.Min(1, t));
        return (p - (a + ab * t)).LengthSquared;
    }

    /// <summary>線段對線段的最短距離平方（Ericson《Real-Time Collision Detection》5.1.9，
    /// 求兩線段上最近點的參數 s、t 並夾限於 [0,1]）。</summary>
    public static double SegmentSegmentDistanceSquared(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;

        double a = d1.LengthSquared;
        double e = d2.LengthSquared;
        double f = Vec3.Dot(d2, r);

        double s, t;

        if (a < 1e-30 && e < 1e-30)
            return r.LengthSquared;                            // 兩線段皆退化為點

        if (a < 1e-30)
        {
            s = 0;
            t = Clamp01(f / e);                                // 線段 1 為點
        }
        else
        {
            double c = Vec3.Dot(d1, r);
            if (e < 1e-30)
            {
                t = 0;
                s = Clamp01(-c / a);                           // 線段 2 為點
            }
            else
            {
                double b = Vec3.Dot(d1, d2);
                double denom = a * e - b * b;

                s = denom > 1e-30 ? Clamp01((b * f - c * e) / denom) : 0;

                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Clamp01(-c / a);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Clamp01((b - c) / a);
                }
            }
        }

        var closest1 = p1 + d1 * s;
        var closest2 = p2 + d2 * t;
        return (closest1 - closest2).LengthSquared;
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
