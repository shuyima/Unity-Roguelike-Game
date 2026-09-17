using UnityEngine;

/// <summary>
/// 可破坏墙体。玩家撞它一次掉一点耐久，耐久归零就碎掉，让出一条新路。
///
/// 原工程里 Player.cs 用 SendMessage("TakeDamage") 调用，
/// 但没有任何脚本实现过这个方法，所以撞墙其实毫无反应。
/// 现在改成直接调用组件方法（比 SendMessage 更快，也不会因为改名而静默失效）。
/// </summary>
public class DestructibleWall : MonoBehaviour
{
    public int health = 2;

    [Tooltip("被击中时的闪白时长")]
    public float flashTime = 0.08f;

    private SpriteRenderer sr;
    private Color originalColor;
    private float flashTimer;

    void Awake()
    {
        sr = GetComponent<SpriteRenderer>();
        if (sr != null) originalColor = sr.color;
    }

    void Update()
    {
        if (flashTimer <= 0f) return;

        flashTimer -= Time.deltaTime;
        if (sr == null) return;

        if (flashTimer <= 0f) sr.color = originalColor;
        else sr.color = Color.Lerp(originalColor, Color.white, flashTimer / flashTime);
    }

    /// <summary>返回 true 表示这一击把墙打碎了</summary>
    public bool TakeDamage(int amount = 1)
    {
        health -= amount;

        if (sr != null)
        {
            sr.color = Color.white;
            flashTimer = flashTime;
        }

        if (health <= 0)
        {
            // 让地图知道这格空出来了，敌人才能走过去
            MapManager map = MapManager.Instance;
            if (map != null)
                map.NotifyCellFreed(new Vector2Int(
                    Mathf.RoundToInt(transform.position.x),
                    Mathf.RoundToInt(transform.position.y)));

            Destroy(gameObject);
            return true;
        }
        return false;
    }
}
