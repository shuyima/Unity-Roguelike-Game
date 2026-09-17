/// <summary>
/// 极简回合系统。
///
/// 这是个格子制游戏：玩家走一格算一个"回合"，之后所有敌人行动一次。
/// 用静态事件而不是让 Player 直接持有敌人列表，是为了让 Player 和 Enemy
/// 互不认识 —— 以后加 NPC、机关、毒雾，只要订阅这个事件就行。
/// </summary>
public static class TurnSystem
{
    /// <summary>玩家完成一次行动后触发</summary>
    public static event System.Action OnPlayerTurn;

    public static void PlayerActed()
    {
        if (OnPlayerTurn != null) OnPlayerTurn();
    }

    /// <summary>
    /// 清空订阅者。静态事件在编辑器里关闭"域重载"时会跨场景残留，
    /// 导致重新生成地图后旧敌人仍被调用（对象已销毁 → 报错）。
    /// MapManager 每次生成地图前调用一次。
    /// </summary>
    public static void Clear()
    {
        OnPlayerTurn = null;
    }
}
