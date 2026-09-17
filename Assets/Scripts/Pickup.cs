using UnityEngine;

/// <summary>
/// 可拾取物：食物回血，苏打加分。挂在触发碰撞体上。
///
/// 注意：Food.prefab / Soda.prefab 原本连 Collider2D 都没有，
/// 触发碰撞体由 MapManager 在生成时用 AddComponent 补上（不改动预制体资源）。
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class Pickup : MonoBehaviour
{
    public enum PickupKind { Food, Soda }

    public PickupKind kind = PickupKind.Food;

    [Tooltip("食物 = 回复的生命值；苏打 = 增加的分数")]
    public int value = 1;

    /// <summary>原地做一个小幅上下浮动，让道具更显眼（纯表现，不影响逻辑）</summary>
    public float bobAmplitude = 0.06f;
    public float bobSpeed = 3f;

    private Vector3 basePos;

    void Start()
    {
        basePos = transform.position;
    }

    void Update()
    {
        if (bobAmplitude <= 0f) return;
        float y = Mathf.Sin(Time.time * bobSpeed + basePos.x) * bobAmplitude;
        transform.position = basePos + new Vector3(0f, y, 0f);
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        Player player = other.GetComponent<Player>();
        if (player == null) return;

        if (kind == PickupKind.Food)
        {
            player.Heal(value);
        }
        else if (GameManage.Instance != null)
        {
            GameManage.Instance.AddScore(value);
        }

        Destroy(gameObject);
    }
}
