using System;
using System.Collections.Generic;
using ClashNavigatorAddin.Services;
using Xunit;
using Vec3 = ClashNavigatorAddin.Services.MeshProximityUtil.Vec3;
using TriangleMesh = ClashNavigatorAddin.Services.MeshProximityUtil.TriangleMesh;

namespace ClashNavigatorAddin.Tests;

public class MeshProximityUtilTests
{
    private const int Precision = 10;

    // ── 點對三角形 ─────────────────────────────────────────────────────────────

    [Fact]
    public void PointTriangle_PointAboveInterior_ReturnsHeightSquared()
    {
        // 單位直角三角形上方 高度 2
        var d2 = MeshProximityUtil.PointTriangleDistanceSquared(
            new Vec3(0.2, 0.2, 2), new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));

        Assert.Equal(4.0, d2, Precision);
    }

    [Fact]
    public void PointTriangle_PointBeyondVertex_ReturnsVertexDistance()
    {
        var d2 = MeshProximityUtil.PointTriangleDistanceSquared(
            new Vec3(-3, -4, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));

        Assert.Equal(25.0, d2, Precision);   // 距頂點 (0,0,0) 為 5
    }

    [Fact]
    public void PointTriangle_PointNearestEdgeInterior_ReturnsEdgeDistance()
    {
        // 點位於邊 (0,0,0)-(1,0,0) 中段的下方 y=-2
        var d2 = MeshProximityUtil.PointTriangleDistanceSquared(
            new Vec3(0.5, -2, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));

        Assert.Equal(4.0, d2, Precision);
    }

    // ── 線段對線段 ─────────────────────────────────────────────────────────────

    [Fact]
    public void SegmentSegment_SkewPerpendicular_ReturnsGap()
    {
        // 兩條互相垂直的線段，最近點皆在中段，Z 相距 0.3
        var d2 = MeshProximityUtil.SegmentSegmentDistanceSquared(
            new Vec3(-5, 0, 0), new Vec3(5, 0, 0),
            new Vec3(0, -5, 0.3), new Vec3(0, 5, 0.3));

        Assert.Equal(0.09, d2, Precision);
    }

    [Fact]
    public void SegmentSegment_ParallelOffset_ReturnsOffset()
    {
        var d2 = MeshProximityUtil.SegmentSegmentDistanceSquared(
            new Vec3(0, 0, 0), new Vec3(10, 0, 0),
            new Vec3(0, 2, 0), new Vec3(10, 2, 0));

        Assert.Equal(4.0, d2, Precision);
    }

    [Fact]
    public void SegmentSegment_EndpointToEndpoint_WhenDisjointColinear()
    {
        var d2 = MeshProximityUtil.SegmentSegmentDistanceSquared(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0),
            new Vec3(3, 0, 0), new Vec3(5, 0, 0));

        Assert.Equal(4.0, d2, Precision);
    }

    // ── 三角形對三角形 ─────────────────────────────────────────────────────────

    [Fact]
    public void TriangleTriangle_ParallelPlanes_ReturnsPlaneGap()
    {
        var d2 = MeshProximityUtil.TriangleTriangleDistanceSquared(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0),
            new Vec3(0, 0, 0.5), new Vec3(1, 0, 0.5), new Vec3(0, 1, 0.5));

        Assert.Equal(0.25, d2, Precision);
    }

    [Fact]
    public void TriangleTriangle_EdgeEdgeClosest_IsDetected()
    {
        // 交叉（X 形）情境：最短距離發生在兩「邊的中段」之間，
        // 任何頂點對三角形的距離都遠大於 0.2——驗證邊對邊項有正確納入。
        var d2 = MeshProximityUtil.TriangleTriangleDistanceSquared(
            new Vec3(-5, -0.5, 0), new Vec3(5, -0.5, 0), new Vec3(0, 0.5, 0),
            new Vec3(-0.5, -5, 0.2), new Vec3(-0.5, 5, 0.2), new Vec3(0.5, 0, 0.2));

        Assert.Equal(0.04, d2, 6);
    }

    // ── 網格層級 ───────────────────────────────────────────────────────────────

    /// <summary>以兩個三角形組成的單位正方形（z=z0 平面，(x0,y0) 為角）建立網格。</summary>
    private static TriangleMesh Quad(double x0, double y0, double z0)
    {
        var v = new List<Vec3>
        {
            new(x0, y0, z0), new(x0 + 1, y0, z0), new(x0 + 1, y0 + 1, z0), new(x0, y0 + 1, z0)
        };
        var t = new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) };
        return new TriangleMesh(v, t);
    }

    [Fact]
    public void AreWithinDistance_ParallelQuadsGap_RespectsThreshold()
    {
        var lower = Quad(0, 0, 0);
        var upper = Quad(0, 0, 0.5);

        Assert.True(MeshProximityUtil.AreWithinDistance(lower, upper, 0.5));
        Assert.True(MeshProximityUtil.AreWithinDistance(lower, upper, 0.51));
        Assert.False(MeshProximityUtil.AreWithinDistance(lower, upper, 0.49));
    }

    [Fact]
    public void AreWithinDistance_CrossingDucts_EdgeEdgeCase()
    {
        // 模擬兩根垂直交叉的「風管底/頂面」：大矩形面頂點都離交叉點很遠，
        // 最短距離只出現在面內部——網格判定必須靠邊對邊/頂點對面組合捕捉。
        var alongX = new TriangleMesh(
            new List<Vec3> { new(-10, -1, 0), new(10, -1, 0), new(10, 1, 0), new(-10, 1, 0) },
            new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) });
        var alongY = new TriangleMesh(
            new List<Vec3> { new(-1, -10, 0.3), new(1, -10, 0.3), new(1, 10, 0.3), new(-1, 10, 0.3) },
            new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) });

        Assert.True(MeshProximityUtil.AreWithinDistance(alongX, alongY, 0.31));
        Assert.False(MeshProximityUtil.AreWithinDistance(alongX, alongY, 0.29));
    }

    [Fact]
    public void AreWithinDistance_EmptyMesh_ReturnsFalse()
    {
        var empty = new TriangleMesh(new List<Vec3>(), new List<(int, int, int)>());
        var quad = Quad(0, 0, 0);

        Assert.False(MeshProximityUtil.AreWithinDistance(empty, quad, 100));
        Assert.False(MeshProximityUtil.AreWithinDistance(quad, empty, 100));
    }

    [Fact]
    public void AreWithinDistance_FarApartMeshes_PrunedByBounds()
    {
        var a = Quad(0, 0, 0);
        var b = Quad(100, 100, 100);

        Assert.False(MeshProximityUtil.AreWithinDistance(a, b, 1));
        Assert.True(MeshProximityUtil.AreWithinDistance(a, b, 1000));
    }

    /// <summary>以 cells×cells 個小方格（每格兩三角形、頂點不共用，與實際三角化一致）
    /// 鋪成一片位於 z 平面、左下角為 (x0,y0)、邊長 size 的大網格。</summary>
    private static TriangleMesh Grid(double x0, double y0, double z, double size, int cells)
    {
        var verts = new List<Vec3>();
        var tris = new List<(int, int, int)>();
        double step = size / cells;

        for (int i = 0; i < cells; i++)
            for (int j = 0; j < cells; j++)
            {
                double x = x0 + i * step, y = y0 + j * step;
                int b = verts.Count;
                verts.Add(new Vec3(x, y, z));
                verts.Add(new Vec3(x + step, y, z));
                verts.Add(new Vec3(x + step, y + step, z));
                verts.Add(new Vec3(x, y + step, z));
                tris.Add((b, b + 1, b + 2));
                tris.Add((b, b + 2, b + 3));
            }

        return new TriangleMesh(verts, tris);
    }

    [Fact]
    public void AreWithinDistance_LargeMeshesOverlappingOnlyAtCorner_StillDetected()
    {
        // 兩片大網格（各 20×20 格，800 三角形），XY 僅在一角落 [95,100]² 重疊，垂直相距 0.3。
        // 「交互區域」雙向裁切會把候選縮到該角落——此測試確保裁切不會漏掉該處的接近，
        // 同時涵蓋「兩者都大且交錯」這個先前會退化為全配對的情境。
        var lower = Grid(0, 0, 0, 100, 20);       // x,y ∈ [0,100]
        var upper = Grid(95, 95, 0.3, 100, 20);   // x,y ∈ [95,195]

        Assert.True(MeshProximityUtil.AreWithinDistance(lower, upper, 0.31));
        Assert.False(MeshProximityUtil.AreWithinDistance(lower, upper, 0.29));
    }

    /// <summary>暴力全配對參考解：是否存在任一三角形對的最短距離 ≤ distance
    /// （<see cref="MeshProximityUtil.TriangleTriangleDistanceSquared"/> 未改動，作為最佳化路徑的黃金對照）。</summary>
    private static bool BruteForceWithin(TriangleMesh a, TriangleMesh b, double distance)
    {
        double d2 = distance * distance;
        foreach (var (a0, a1, a2) in a.Triangles)
            foreach (var (b0, b1, b2) in b.Triangles)
                if (MeshProximityUtil.TriangleTriangleDistanceSquared(
                        a.Vertices[a0], a.Vertices[a1], a.Vertices[a2],
                        b.Vertices[b0], b.Vertices[b1], b.Vertices[b2]) <= d2)
                    return true;
        return false;
    }

    private static void AssertMatchesBruteForce(TriangleMesh a, TriangleMesh b, params double[] distances)
    {
        foreach (var d in distances)
            Assert.Equal(BruteForceWithin(a, b, d), MeshProximityUtil.AreWithinDistance(a, b, d));
    }

    [Fact]
    public void AreWithinDistance_ParallelFaces_MatchesBruteForce_AcrossOptimizationTiers()
    {
        // 兩片「完全重疊的平行網格」（Z 相距 0.3）＝「兩大面平行貼近」——裁切後候選仍近 O(nA×nB)，
        // 正是第 9／10 項最佳化鎖定的退化情境。不同格數讓配對量落入不同層級：
        //   4×4 格（32 三角形，配對量 ~1k）→ 簡單巢狀迴圈；
        //   10×10（200，~40k）→ 排序 early-break（sequential）；
        //   16×16（512，~262k）→ 排序 early-break + Parallel.For。
        // 三個層級對多個距離都必須與暴力全配對得到「完全一致」的布林——
        // 任何 sort/break/平行導致的「漏掉合格對」都會在此被抓出（false negative）。
        foreach (var cells in new[] { 4, 10, 16 })
        {
            var lower = Grid(0, 0, 0, 100, cells);
            var upper = Grid(0, 0, 0.3, 100, cells);
            AssertMatchesBruteForce(lower, upper, 0.2, 0.29, 0.31, 0.5, 5.0);
        }
    }

    [Fact]
    public void AreWithinDistance_LargeParallelFaces_KnownBoundary()
    {
        // 明確邊界（間距 0.3）：完全重疊的大網格，落在 Parallel.For 層級。
        var lower = Grid(0, 0, 0, 100, 16);
        var upper = Grid(0, 0, 0.3, 100, 16);

        Assert.True(MeshProximityUtil.AreWithinDistance(lower, upper, 0.31));
        Assert.False(MeshProximityUtil.AreWithinDistance(lower, upper, 0.29));
    }
}
