using UnityEngine;

/// <summary>
/// 出口传送门：玩家走上去就进入下一层。
///
/// Exit.prefab 原本只有一个 SpriteRenderer，没有碰撞体，
/// 所以走到出口不会有任何事情发生。触发碰撞体由 MapManager 动态补上。
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class ExitPortal : MonoBehaviour
{
    public float pulseSpeed = 2.5f;
    public float pulseAmount = 0.12f;

    private Vector3 baseScale;
    private float phase;

    void Start()
    {
        baseScale = transform.localScale;
        phase = Random.value * Mathf.PI * 2f;
    }

    void Update()
    {
        float s = 1f + Mathf.Sin(Time.time * pulseSpeed + phase) * pulseAmount;
        transform.localScale = baseScale * s;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GetComponent<Player>() == null) return;

        MapManager map = MapManager.Instance;
        if (map != null) map.GoToNextLevel();
    }
}
