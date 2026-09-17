using UnityEngine;

/// <summary>
/// 玩家控制：格子式移动 + 撞击攻击 + 动画驱动。
///
/// 动画由本脚本通过 Animator 的 Trigger 参数驱动：
///   平时        -> 默认状态 PlayerIdle
///   撞到敌人/墙 -> Attack 触发器
///   受到伤害    -> Damage 触发器
/// 动画播完后由状态机自己回到 Idle。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(BoxCollider2D))]
public class Player : MonoBehaviour
{
    [Header("移动")]
    public float smoothing = 12f;
    [Tooltip("两次移动之间的最小间隔（秒）")]
    public float restTime = 0.28f;

    [Header("战斗")]
    public int attackDamage = 1;

    private Rigidbody2D rb2d;
    private BoxCollider2D boxCollider;
    private Animator anim;

    private Vector2 targetPos;
    private float restTimer;

    private bool hasAttackParam;
    private bool hasDamageParam;
    private bool dead;
    private bool warnedOffFloor;

    void Start()
    {
        rb2d = GetComponent<Rigidbody2D>();
        boxCollider = GetComponent<BoxCollider2D>();
        anim = GetComponent<Animator>();

        // 俯视角格子游戏必须关掉重力。Player.prefab 里的 gravityScale 是 1，
        // 移动时有 MovePosition 顶着看不出来，但站立不动时没有任何东西对抗重力，
        // 角色会被持续往下拽、慢慢偏离格子中心。
        rb2d.gravityScale = 0f;
        rb2d.freezeRotation = true;   // 避免撞墙时被物理系统转歪

        if (anim != null)
        {
            foreach (AnimatorControllerParameter p in anim.parameters)
            {
                if (p.name == "Attack" && p.type == AnimatorControllerParameterType.Trigger) hasAttackParam = true;
                if (p.name == "Damage" && p.type == AnimatorControllerParameterType.Trigger) hasDamageParam = true;
            }
        }

        // 出生点由 MapManager 按程序化地牢的结果摆放，不能写死成 (1,1)
        targetPos = transform.position;
    }

    void FixedUpdate()
    {
        if (dead) return;

        CheckOnFloor();

        // 还没走到目标格，继续插值移动
        Vector2 dif = new Vector2(transform.position.x - targetPos.x,
                                  transform.position.y - targetPos.y);
        if (dif.magnitude > 0.01f)
        {
            rb2d.MovePosition(Vector2.Lerp(transform.position, targetPos,
                                           Time.deltaTime * smoothing));
            return;
        }

        // 站定时把残余速度清零。
        // 角色是 Dynamic 刚体（触发器事件需要它），撞到敌人/墙时物理引擎可能会
        // 留下冲量，把角色一点点推出格子 —— 推出去之后就再也进不了输入分支了。
        rb2d.velocity = Vector2.zero;

        // 站定在格子中心后才响应输入
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        if (h == 0 && v == 0) return;

        // 归一化成 ±1，保证一次只走一格
        if (h != 0) h = h / Mathf.Abs(h);
        if (v != 0) v = v / Mathf.Abs(v);

        restTimer += Time.deltaTime;
        if (restTimer < restTime) return;
        restTimer = 0f;

        TryStep(new Vector2(h, v));
    }

    void TryStep(Vector2 dir)
    {
        // 临时关掉自己的碰撞体，否则射线会打到自己
        boxCollider.enabled = false;
        RaycastHit2D hit = Physics2D.Linecast(targetPos, targetPos + dir);
        boxCollider.enabled = true;

        // 什么都没有，或只是触发器（食物/苏打/出口）—— 直接走过去
        if (hit.collider == null || hit.collider.isTrigger)
        {
            targetPos += dir;
            TurnSystem.PlayerActed();
            return;
        }

        // 撞到实体：先看是不是可以攻击的目标
        Enemy enemy = hit.collider.GetComponent<Enemy>();
        if (enemy != null)
        {
            PlayAttack();
            enemy.TakeDamage(attackDamage);
            TurnSystem.PlayerActed();   // 攻击同样消耗一个回合
            return;
        }

        DestructibleWall wall = hit.collider.GetComponent<DestructibleWall>();
        if (wall != null)
        {
            PlayAttack();
            wall.TakeDamage(attackDamage);
            TurnSystem.PlayerActed();
            return;
        }

        // 剩下的是边界墙 / 实心岩石：撞不动，也不播攻击动画
    }

    void PlayAttack()
    {
        if (hasAttackParam) anim.SetTrigger("Attack");
    }

    /// <summary>
    /// 安全网：万一角色因为任何原因（物理推挤、重生成时序、手滑改参数）
    /// 落到了非地面格子上，立刻拉回出生点，并打一条警告方便定位。
    /// 正常情况下这个函数什么都不做，开销只有一次数组查表。
    /// </summary>
    void CheckOnFloor()
    {
        MapManager map = MapManager.Instance;
        if (map == null || map.Dungeon == null) return;

        Vector2Int cell = map.WorldToCell(transform.position);
        if (map.Dungeon.IsFloor(cell.x, cell.y)) return;

        Vector2Int spawn = map.Dungeon.PlayerStart;
        Vector3 safe = new Vector3(spawn.x, spawn.y, 0f);

        transform.position = safe;
        rb2d.position = safe;
        targetPos = safe;

        if (!warnedOffFloor)
        {
            warnedOffFloor = true;
            Debug.LogWarning(string.Format(
                "[Player] 角色跑到了非地面格子 ({0},{1})，已拉回出生点 ({2},{3})。" +
                "如果频繁出现，请把这条日志发出来。",
                cell.x, cell.y, spawn.x, spawn.y));
        }
    }

    // ------------------------------------------------------------------
    // 生命值
    // ------------------------------------------------------------------
    public void TakeDamage(int amount)
    {
        if (dead) return;

        if (hasDamageParam) anim.SetTrigger("Damage");

        if (GameManage.Instance == null) return;

        if (GameManage.Instance.TakeDamage(amount))
        {
            dead = true;
            MapManager map = MapManager.Instance;
            if (map != null) map.RestartRun();
        }
    }

    public void Heal(int amount)
    {
        if (GameManage.Instance != null) GameManage.Instance.Heal(amount);
    }

    /// <summary>重新开始时复位（MapManager 生成新地图后调用）</summary>
    public void Revive(Vector3 position)
    {
        dead = false;
        warnedOffFloor = false;

        // Dynamic 刚体必须同时同步 transform 和 rb2d.position，
        // 只设 transform 的话物理步进时可能被刚体内部的旧位置覆盖回去。
        transform.position = position;
        rb2d.position = position;
        rb2d.velocity = Vector2.zero;

        targetPos = position;
        restTimer = 0f;
    }
}
