using System;
using System.Collections.Generic;

namespace ClashNavigatorAddin.Models;

/// <summary>截圖時要從哪個方向看。
///
/// <b>為什麼是一個型別而不是一個 enum</b>：每個方向要同時帶三樣東西——設定檔用的**穩定鍵**、
/// 給人看的名稱、以及送進 <c>ViewOrientation3D</c> 的兩個向量。enum 只能給第一樣，
/// 另外兩樣就會散成兩張對照表，而它們必須一起改（少改一張＝方向名字對、圖卻是別的角度）。
///
/// <b>刻意不含任何 Revit 型別</b>（向量用 double 而不是 <c>XYZ</c>）：這樣它進得了 <c>Tests\</c>，
/// 而「哪個名字配哪個向量」正是那種**錯了不會擲例外、只會靜默給出錯的圖**的東西
/// ——與 <c>SourceHintUtil.MatchesHint</c>／<c>DocumentKeyRule.KeyOf</c> 同一種形狀。
/// 轉成 <c>XYZ</c> 是呼叫端在 Revit 邊界上做的一行。
///
/// <b>座標系是 Revit 的內部專案座標</b>：+X＝專案東、+Y＝專案北、+Z＝上。
/// 所以這裡的「北」是**專案北不是正北**——模型有旋轉時兩者差很多。
/// 要做正北得另外套 <c>ActiveProjectLocation</c> 的角度，目前刻意不做（沒有需求，
/// 而多一層轉換就多一個「圖跟名字對不起來」的失敗點）。</summary>
public sealed class ClashCaptureDirection
{
    /// <summary>「維持工作視圖目前的視角」的鍵。
    ///
    /// <b>這是預設值，而且是唯一預設值</b>：它代表**完全不呼叫 <c>SetOrientation</c>**，
    /// 也就是 2026-08-09 以前的行為，成本與產出都與現況一字不差。
    /// 加方向是加值不是換掉——把預設換成某個具名方向，等於讓所有既有使用者的圖無聲改變。</summary>
    public const string CurrentViewKey = "current";

    private ClashCaptureDirection(
        string key, string displayName,
        double forwardX, double forwardY, double forwardZ,
        double upX, double upY, double upZ,
        bool keepsCurrentOrientation)
    {
        Key = key;
        DisplayName = displayName;
        ForwardX = forwardX;
        ForwardY = forwardY;
        ForwardZ = forwardZ;
        UpX = upX;
        UpY = upY;
        UpZ = upZ;
        KeepsCurrentOrientation = keepsCurrentOrientation;
    }

    /// <summary>設定檔（<c>settings.json</c>）與 Excel 欄位對應用的穩定鍵。
    /// <b>永遠不要在地化，也不要改字</b>——改了等於把使用者存好的選擇清空。</summary>
    public string Key { get; }

    /// <summary>給人看的名稱：面板的勾選項、Excel 圖片表的欄標題。</summary>
    public string DisplayName { get; }

    /// <summary>視線方向（由眼睛指向模型）。<see cref="KeepsCurrentOrientation"/> 為 true 時無意義。</summary>
    public double ForwardX { get; }
    public double ForwardY { get; }
    public double ForwardZ { get; }

    /// <summary>畫面上方。必須與 Forward 正交，Revit 的 <c>ViewOrientation3D</c> 會拒絕不正交的組合
    /// ——等角方向不能直接塞 (0,0,1)，要取世界 Z 在垂直於 Forward 的平面上的投影。</summary>
    public double UpX { get; }
    public double UpY { get; }
    public double UpZ { get; }

    /// <summary>true＝不要動視角，直接用工作視圖現在的方向（見 <see cref="CurrentViewKey"/>）。</summary>
    public bool KeepsCurrentOrientation { get; }

    /// <summary>可選的全部方向，**順序即 Excel 的欄序**。
    ///
    /// <b>輸出順序刻意取這裡的順序，而不是使用者勾選的先後</b>：不同人勾的順序不同，
    /// 而兩份報告的欄位對不起來時沒有人會發現是順序問題。
    ///
    /// 等角命名取「眼睛在哪個方位」（東北＝從東北上方往西南下方看），與 Navisworks 的講法一致。
    /// 等角的 Up 是 (∓1,∓1,2) 這一族＝世界 Z 投影到垂直於視線的平面上（見 <see cref="UpX"/>）。</summary>
    public static IReadOnlyList<ClashCaptureDirection> All { get; } = new List<ClashCaptureDirection>
    {
        new ClashCaptureDirection(CurrentViewKey, "維持目前視角", 0, 0, 0, 0, 0, 0, true),

        new ClashCaptureDirection("iso-ne", "等角－東北", -1, -1, -1, -1, -1, 2, false),
        new ClashCaptureDirection("iso-se", "等角－東南", -1, 1, -1, -1, 1, 2, false),
        new ClashCaptureDirection("iso-sw", "等角－西南", 1, 1, -1, 1, 1, 2, false),
        new ClashCaptureDirection("iso-nw", "等角－西北", 1, -1, -1, 1, -1, 2, false),

        new ClashCaptureDirection("top", "俯視（上）", 0, 0, -1, 0, 1, 0, false),
        new ClashCaptureDirection("bottom", "仰視（下）", 0, 0, 1, 0, 1, 0, false),
        new ClashCaptureDirection("front", "前（南往北）", 0, 1, 0, 0, 0, 1, false),
        new ClashCaptureDirection("back", "後（北往南）", 0, -1, 0, 0, 0, 1, false),
        new ClashCaptureDirection("left", "左（西往東）", 1, 0, 0, 0, 0, 1, false),
        new ClashCaptureDirection("right", "右（東往西）", -1, 0, 0, 0, 0, 1, false)
    };

    /// <summary>預設選擇：只有「維持目前視角」＝現況。</summary>
    public static IReadOnlyList<string> DefaultKeys { get; } = new List<string> { CurrentViewKey };

    public static ClashCaptureDirection? Find(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        foreach (var direction in All)
        {
            if (string.Equals(direction.Key, key, StringComparison.Ordinal))
                return direction;
        }

        return null;
    }

    /// <summary>把設定檔存的鍵解析成這一輪要擷取的方向。
    ///
    /// <b>三件事一起做，因為三件都會靜默出錯</b>：
    /// ① **認不得的鍵直接丟掉**（設定檔被手改、或是別的版本寫的）——留著會讓下游拿到 null；
    /// ② **去重**——同一個方向勾兩次會產生兩張一模一樣的圖、付兩次錢，而 Excel 上看起來只是「怎麼有兩欄一樣」；
    /// ③ **全空時退回預設**，絕不回空清單——空清單會讓整份匯出一張圖都沒有，
    ///    而那份 xlsx 看起來「成功了」，只是每一列都寫著「（未擷取）」。
    ///
    /// 順序一律取 <see cref="All"/> 的順序（理由見那裡）。</summary>
    public static IReadOnlyList<ClashCaptureDirection> Resolve(IEnumerable<string>? keys)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (keys != null)
        {
            foreach (var key in keys)
            {
                if (key != null)
                    selected.Add(key);
            }
        }

        var result = new List<ClashCaptureDirection>();
        foreach (var direction in All)
        {
            if (selected.Contains(direction.Key))
                result.Add(direction);
        }

        if (result.Count == 0)
            result.Add(Find(CurrentViewKey)!);

        return result;
    }
}
