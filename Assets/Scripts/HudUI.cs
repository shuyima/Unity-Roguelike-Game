using UnityEngine;

/// <summary>
/// 极简 HUD：左上角显示层数、生命、分数。
///
/// 用 OnGUI 而不是 Canvas，是为了不依赖任何预制体或场景配置 ——
/// MapManager 会在 Awake 时自动把它挂到自己身上，打开工程直接就能看到数值。
/// 正式项目当然应该换成 UGUI / TextMeshPro，这里只求"立刻能验证状态"。
/// </summary>
public class HudUI : MonoBehaviour
{
    public int fontSize = 22;
    public Color textColor = Color.white;
    public Color shadowColor = new Color(0f, 0f, 0f, 0.75f);

    private GUIStyle style;
    private GUIStyle shadow;
    private int lastFontSize = -1;

    void BuildStyles()
    {
        if (style != null && lastFontSize == fontSize) return;

        style = new GUIStyle(GUI.skin.label);
        style.fontSize = fontSize;
        style.normal.textColor = textColor;
        style.fontStyle = FontStyle.Bold;

        shadow = new GUIStyle(style);
        shadow.normal.textColor = shadowColor;

        lastFontSize = fontSize;
    }

    void OnGUI()
    {
        GameManage g = GameManage.Instance;
        if (g == null) return;

        BuildStyles();

        string text = string.Format("第 {0} 层    生命 {1}/{2}    分数 {3}",
                                    g.level, g.health, g.maxHealth, g.score);

        Rect rect = new Rect(18f, 14f, 900f, fontSize + 12f);
        Rect shadowRect = new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height);

        GUI.Label(shadowRect, text, shadow);
        GUI.Label(rect, text, style);

        // 操作提示放在左下角
        GUIStyle hint = new GUIStyle(style);
        hint.fontSize = Mathf.Max(12, fontSize - 6);
        hint.fontStyle = FontStyle.Normal;
        hint.normal.textColor = new Color(1f, 1f, 1f, 0.65f);

        GUI.Label(new Rect(18f, Screen.height - 34f, 900f, 26f),
                  "方向键移动    撞墙 / 撞敌人 = 攻击    R = 重新生成地图", hint);
    }
}
