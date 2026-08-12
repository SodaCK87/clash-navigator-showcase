using System;
using System.Collections.Generic;
using System.Linq;
using ClashNavigatorAddin.Services;
using Xunit;
using Box = ClashNavigatorAddin.Services.BoundingBoxPairFinder.Box;

namespace ClashNavigatorAddin.Tests;

public class BoundingBoxPairFinderTests
{
    private static Box Cube(double x, double y, double z, double size = 1) =>
        new(x, y, z, x + size, y + size, z + size);

    private static HashSet<(int, int)> Find(IReadOnlyList<Box> a, IReadOnlyList<Box> b, double expansion = 0) =>
        BoundingBoxPairFinder.FindOverlappingPairs(a, b, expansion).ToHashSet();

    [Fact]
    public void EmptySets_ReturnNoPairs()
    {
        Assert.Empty(Find(new List<Box>(), new List<Box> { Cube(0, 0, 0) }));
        Assert.Empty(Find(new List<Box> { Cube(0, 0, 0) }, new List<Box>()));
    }

    [Fact]
    public void OverlappingBoxes_AreFound()
    {
        var pairs = Find(
            new List<Box> { Cube(0, 0, 0) },
            new List<Box> { Cube(0.5, 0.5, 0.5) });

        Assert.Equal(new HashSet<(int, int)> { (0, 0) }, pairs);
    }

    [Theory]
    [InlineData(2, 0, 0)]   // X 軸分離
    [InlineData(0, 2, 0)]   // Y 軸分離
    [InlineData(0, 0, 2)]   // Z 軸分離
    public void SeparatedBoxes_AreNotFound(double dx, double dy, double dz)
    {
        var pairs = Find(
            new List<Box> { Cube(0, 0, 0) },
            new List<Box> { Cube(dx, dy, dz) });

        Assert.Empty(pairs);
    }

    [Fact]
    public void TouchingBoxes_CountAsOverlap()
    {
        // 恰好貼齊（間隙 0）視為重疊候選——精算階段的布林交集自會判定是否真的相交。
        var pairs = Find(
            new List<Box> { Cube(0, 0, 0) },
            new List<Box> { Cube(1, 0, 0) });

        Assert.Single(pairs);
    }

    [Fact]
    public void Expansion_TreatsGapWithinToleranceAsOverlap()
    {
        var a = new List<Box> { Cube(0, 0, 0) };
        var b = new List<Box> { Cube(1.5, 0, 0) };   // X 間隙 0.5

        Assert.Empty(Find(a, b));
        Assert.Single(Find(a, b, expansion: 0.5));
        Assert.Empty(Find(a, b, expansion: 0.4));
    }

    [Fact]
    public void ManyBoxes_MatchesBruteForce()
    {
        // 與暴力法對照的隨機測試（固定種子，可重現）。
        var random = new Random(42);
        Box RandomBox()
        {
            double x = random.NextDouble() * 20, y = random.NextDouble() * 20, z = random.NextDouble() * 20;
            return new Box(x, y, z,
                x + random.NextDouble() * 3, y + random.NextDouble() * 3, z + random.NextDouble() * 3);
        }

        var setA = Enumerable.Range(0, 150).Select(_ => RandomBox()).ToList();
        var setB = Enumerable.Range(0, 200).Select(_ => RandomBox()).ToList();

        var expected = new HashSet<(int, int)>();
        for (int i = 0; i < setA.Count; i++)
        for (int j = 0; j < setB.Count; j++)
        {
            var a = setA[i];
            var b = setB[j];
            bool overlap = a.MinX <= b.MaxX && a.MaxX >= b.MinX
                && a.MinY <= b.MaxY && a.MaxY >= b.MinY
                && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
            if (overlap)
                expected.Add((i, j));
        }

        Assert.Equal(expected, Find(setA, setB));
    }

    [Fact]
    public void TallModel_MatchesBruteForce()
    {
        // 高塔型分佈（Z 向範圍遠大於平面向）：掃描軸會自動選 Z，
        // 以含公差的暴力法對照驗證此路徑的結果一致（固定種子，可重現）。
        const double expansion = 0.5;
        var random = new Random(7);
        Box RandomBox()
        {
            double x = random.NextDouble() * 5, y = random.NextDouble() * 5, z = random.NextDouble() * 100;
            return new Box(x, y, z,
                x + random.NextDouble() * 2, y + random.NextDouble() * 2, z + random.NextDouble() * 2);
        }

        var setA = Enumerable.Range(0, 150).Select(_ => RandomBox()).ToList();
        var setB = Enumerable.Range(0, 200).Select(_ => RandomBox()).ToList();

        var expected = new HashSet<(int, int)>();
        for (int i = 0; i < setA.Count; i++)
        for (int j = 0; j < setB.Count; j++)
        {
            var a = setA[i];
            var b = setB[j];
            bool overlap = a.MinX <= b.MaxX + expansion && a.MaxX + expansion >= b.MinX
                && a.MinY <= b.MaxY + expansion && a.MaxY + expansion >= b.MinY
                && a.MinZ <= b.MaxZ + expansion && a.MaxZ + expansion >= b.MinZ;
            if (overlap)
                expected.Add((i, j));
        }

        Assert.Equal(expected, Find(setA, setB, expansion));
    }

    [Fact]
    public void WideModel_MatchesBruteForce()
    {
        // Y 向為主的分佈：掃描軸會自動選 Y，與暴力法對照驗證（固定種子，可重現）。
        var random = new Random(11);
        Box RandomBox()
        {
            double x = random.NextDouble() * 5, y = random.NextDouble() * 100, z = random.NextDouble() * 5;
            return new Box(x, y, z,
                x + random.NextDouble() * 2, y + random.NextDouble() * 2, z + random.NextDouble() * 2);
        }

        var setA = Enumerable.Range(0, 150).Select(_ => RandomBox()).ToList();
        var setB = Enumerable.Range(0, 200).Select(_ => RandomBox()).ToList();

        var expected = new HashSet<(int, int)>();
        for (int i = 0; i < setA.Count; i++)
        for (int j = 0; j < setB.Count; j++)
        {
            var a = setA[i];
            var b = setB[j];
            bool overlap = a.MinX <= b.MaxX && a.MaxX >= b.MinX
                && a.MinY <= b.MaxY && a.MaxY >= b.MinY
                && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
            if (overlap)
                expected.Add((i, j));
        }

        Assert.Equal(expected, Find(setA, setB));
    }

    [Fact]
    public void ReturnedIndices_MapToOriginalListOrder()
    {
        // setA[1] 只與 setB[0] 重疊；確認回傳索引對應「原始清單」而非內部排序後的順序。
        var setA = new List<Box> { Cube(10, 0, 0), Cube(0, 0, 0) };
        var setB = new List<Box> { Cube(0.5, 0, 0), Cube(20, 20, 20) };

        var pairs = Find(setA, setB);

        Assert.Equal(new HashSet<(int, int)> { (1, 0) }, pairs);
    }
}
