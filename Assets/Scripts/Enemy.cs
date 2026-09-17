using UnityEngine;

/// <summary>
/// 敌人：每回合朝玩家走一格，贴身就攻击。
///
/// 原本 Enemy1.prefab / Enemy2.prefab 只有 SpriteRenderer + Animator，
/// 既没有碰撞体也没有任何脚本，所以"路过敌人没反应"是必然的。
/// 碰撞体和本脚本都由 MapManager 在生成时动态补上。
///
/// 移动策略是贪心走法（先走差值大的那个轴，走不通再换另一个轴），
/// 对地牢这种窄走廊环境足够用，也比完整寻路便宜得多。
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class Enemy : MonoBehaviour
{
    [Header("属性")]
    public int health = 2;
    public int damage = 1;
    [Tooltip("超过这个格数就不会主动追玩家")]
    public int aggroRange = 12;

    [Header("表现")]
    public float moveDuration = 0.12f;

    /// <summary>当前所在格子</summary>
    public Vector2Int Cell { get; private set; }

    private MapManager map;
    private Transform player;
    private Animator anim;
    private bool hasAttackParam;

    private Vector2 fromPos;
    private Vector2 toPos;
    private float moveTimer = 1f;   // >= moveDuration 表示已经站定

    /// <summary>
    /// 已经被退役（地图正在重新生成）。
    /// Destroy() 要到帧末才真正执行，而玩家在一回合内可能被打死并触发重新生成，
    /// 此时回合事件仍在遍历旧的订阅者列表。没有这个标记的话，
    /// 已"死"的敌人还会再动一次，甚至对着已经被销毁的玩家继续攻击。
    /// </summary>
    private bool retired;

    public void Retire()
    {
        retired = true;
    }

    public void Setup(MapManager owner, Transform playerTransform, Vector2Int cell)
    {
        map = owner;
        player = playerTransform;
        Cell = cell;
        fromPos = toPos = new Vector2(cell.x, cell.y);
        transform.position = toPos;

        anim = GetComponent<Animator>();
        if (anim != null)
        {
            foreach (AnimatorControllerParameter p in anim.parameters)
                if (p.name == "Attack" && p.type == AnimatorControllerParameterType.Bool)
                    hasAttackParam = true;
        }
    }

    void OnEnable()
    {
        TurnSystem.OnPlayerTurn += TakeTurn;
    }

    void OnDisable()
    {
        TurnSystem.OnPlayerTurn -= TakeTurn;
    }

    void Update()
    {
        if (moveTimer >= moveDuration) return;

        moveTimer += Time.deltaTime;
        float t = Mathf.Clamp01(moveTimer / moveDuration);
        transform.position = Vector2.Lerp(fromPos, toPos, t);
    }

    // ------------------------------------------------------------------
    // 回合行动
    // ------------------------------------------------------------------
    void TakeTurn()
    {
        if (retired || map == null || player == null) return;

        Vector2Int playerCell = map.WorldToCell(player.position);
        Vector2Int delta = playerCell - Cell;
        int distance = Mathf.Abs(delta.x) + Mathf.Abs(delta.y);

        // 贴身：攻击
        if (distance <= 1)
        {
            SetAttacking(true);
            Player target = player.GetComponent<Player>();
            if (target != null) target.TakeDamage(damage);
            return;
        }

        SetAttacking(false);

        if (distance > aggroRange) return;

        // 贪心：优先走差值大的轴，走不通再试另一个轴。
        // 这样在 L 形走廊里也能绕得过去，不会卡在墙角。
        Vector2Int primary, secondary;
        if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
        {
            primary = new Vector2Int((int)Mathf.Sign(delta.x), 0);
            secondary = new Vector2Int(0, (int)Mathf.Sign(delta.y));
        }
        else
        {
            primary = new Vector2Int(0, (int)Mathf.Sign(delta.y));
            secondary = new Vector2Int((int)Mathf.Sign(delta.x), 0);
        }

        if (TryMove(primary)) return;
        TryMove(secondary);
    }

    bool TryMove(Vector2Int step)
    {
        if (step == Vector2Int.zero) return false;

        Vector2Int next = Cell + step;
        if (!map.IsCellFree(next, this)) return false;

        map.VacateCell(Cell, this);
        Cell = next;
        map.OccupyCell(Cell, this);

        fromPos = transform.position;
        toPos = new Vector2(Cell.x, Cell.y);
        moveTimer = 0f;
        return true;
    }

    void SetAttacking(bool value)
    {
        if (hasAttackParam) anim.SetBool("Attack", value);
    }

    // ------------------------------------------------------------------
    // 受击
    // ------------------------------------------------------------------
    /// <summary>返回 true 表示这次伤害打死了它</summary>
    public bool TakeDamage(int amount = 1)
    {
        health -= amount;
        if (health > 0) return false;

        if (map != null) map.VacateCell(Cell, this);
        Destroy(gameObject);
        return true;
    }
}
