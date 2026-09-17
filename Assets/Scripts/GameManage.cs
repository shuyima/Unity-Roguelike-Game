using UnityEngine;

/// <summary>
/// 全局游戏状态：层数、分数、生命值。
/// 挂在场景的 GameManager 物体上，MapManager 直接读取和修改。
/// </summary>
public class GameManage : MonoBehaviour
{
    public static GameManage Instance { get; private set; }

    [Header("关卡")]
    public int level = 1;

    [Header("生命值")]
    public int maxHealth = 5;
    public int health = 5;

    [Header("分数")]
    public int score = 0;

    void Awake()
    {
        Instance = this;
        if (health <= 0 || health > maxHealth) health = maxHealth;
    }

    public void AddScore(int value)
    {
        score += value;
    }

    /// <summary>扣血，返回 true 表示这次伤害导致了死亡</summary>
    public bool TakeDamage(int amount)
    {
        health = Mathf.Max(0, health - amount);
        return health <= 0;
    }

    public void Heal(int amount)
    {
        health = Mathf.Min(maxHealth, health + amount);
    }

    /// <summary>死亡重来：回到第 1 层、清空分数、恢复满血</summary>
    public void ResetRun()
    {
        level = 1;
        score = 0;
        health = maxHealth;
    }
}
