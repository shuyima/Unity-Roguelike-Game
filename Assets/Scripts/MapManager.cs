using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地牢管理器：调用 DungeonGenerator 算出地图，再把预制体铺到场景里，
/// 并给那些"缺少组件"的预制体补上碰撞体和行为脚本。
///
/// 分工：
///   DungeonGenerator —— 纯算法，决定房间/走廊/出生点/出口在哪
///   MapManager       —— 实例化 GameObject、放置实体、维护格子占用、处理过关
///
/// 关于 AddComponent：
///   原工程的 Food / Soda / Enemy / Exit 预制体只有 SpriteRenderer，
///   既没有 Collider2D 也没有脚本，所以完全没有交互。这里在运行时补上，
///   好处是不用改动预制体资源本身，随时可以退回原样。
/// </summary>
public class MapManager : MonoBehaviour
{
    public static MapManager Instance { get; private set; }

    [Header("地形预制体")]
    public GameObject[] outWallArray;   // 边界墙与实心岩石（Tag: OutWall，不可破坏）
    public GameObject[] floorArray;     // 地面
    public GameObject[] wallArray;      // 房间内可破坏的墙体（Tag: Wall）
    public GameObject[] foodArray;      // 食物
    public GameObject[] enemyArray;     // 敌人
    public GameObject exitPrefab;       // 出口

    [Header("地图尺寸（格子数）")]
    public int cols = 26;
    public int rows = 15;

    [Header("房间参数")]
    public int minRoomSize = 4;
    public int maxRoomSize = 7;
    public int extraConnections = 2;

    [Header("随机种子")]
    [Tooltip("勾选则每次生成都随机；取消勾选则使用下面的 seed，同一个 seed 永远生成同一张地图")]
    public bool useRandomSeed = true;
    public int seed = 12345;

    [Header("房间内可破坏墙体的数量")]
    public int minCountWall = 2;
    public int maxCountWall = 8;

    [Header("玩家")]
    public bool spawnPlayer = true;
    public GameObject playerPrefab;
    [Tooltip("敌人不会出生在离玩家出生点这么近的格子里")]
    public int safeSpawnRadius = 5;

    [Header("调试")]
    public bool regenerateOnKey = true;
    public KeyCode regenerateKey = KeyCode.R;
    [Tooltip("勾选后会在 Console 打印整张地图的字符画")]
    public bool logAsciiMap = false;

    private GameManage gameManage;
    private Transform mapHolder;
    private GameObject currentPlayer;
    private DungeonGenerator dungeon;

    /// <summary>主通路（出生点 → 出口）上的格子，可破坏墙体不会放在这里</summary>
    private readonly HashSet<Vector2Int> protectedTiles = new HashSet<Vector2Int>();

    /// <summary>被敌人占据的格子</summary>
    private readonly Dictionary<Vector2Int, Enemy> occupied = new Dictionary<Vector2Int, Enemy>();

    /// <summary>可破坏墙体所在的格子（打碎后要移除，否则敌人以为那里还堵着）</summary>
    private readonly HashSet<Vector2Int> blockerCells = new HashSet<Vector2Int>();

    public DungeonGenerator Dungeon { get { return dungeon; } }

    void Awake()
    {
        Instance = this;

        // 自动挂上 HUD，省得还要手动建 Canvas 才能看到血量/分数
        if (GetComponent<HudUI>() == null) gameObject.AddComponent<HudUI>();
    }

    void Start()
    {
        gameManage = GetComponentInParent<GameManage>();
        Generate();
    }

    void Update()
    {
        if (regenerateOnKey && Input.GetKeyDown(regenerateKey))
            Generate();
    }

    // ==================================================================
    // 生成
    // ==================================================================
    public void Generate()
    {
        ClearMap();

        int actualSeed = useRandomSeed ? Random.Range(int.MinValue, int.MaxValue) : seed;

        dungeon = new DungeonGenerator(cols, rows, actualSeed,
                                       minRoomSize, maxRoomSize, 150, extraConnections);
        dungeon.Generate();

        if (logAsciiMap) Debug.Log(dungeon.ToAscii());

        // 记录主通路，避免可破坏墙体把出口堵死
        protectedTiles.Clear();
        List<Vector2Int> path = dungeon.ShortestPathToExit();
        for (int i = 0; i < path.Count; i++) protectedTiles.Add(path[i]);

        BuildTiles();
        SpawnExit();
        SpawnPlayer();     // 必须先于敌人，敌人需要拿到玩家位置
        SpawnItems();      // 先于敌人，可破坏墙体要登记占位
        SpawnEnemies();
    }

    void ClearMap()
    {
        // 静态事件会跨场景残留，先清掉旧的订阅者
        TurnSystem.Clear();

        // 旧敌人标记退役：Destroy 要帧末才生效，这中间它们可能还会被回合事件调到
        foreach (Enemy e in occupied.Values)
            if (e != null) e.Retire();

        if (mapHolder != null) Destroy(mapHolder.gameObject);

        if (currentPlayer != null)
        {
            Destroy(currentPlayer);
            currentPlayer = null;   // 立刻断开引用，否则 SpawnPlayer 会去"复活"一个待销毁的对象
        }

        mapHolder = new GameObject("Map").transform;
        protectedTiles.Clear();
        occupied.Clear();
        blockerCells.Clear();
    }

    // ------------------------------------------------------------------
    // 地形
    // ------------------------------------------------------------------
    void BuildTiles()
    {
        for (int x = 0; x < dungeon.Width; x++)
        {
            for (int y = 0; y < dungeon.Height; y++)
            {
                Vector3 pos = new Vector3(x, y, 0);

                if (dungeon.Grid[x, y] == DungeonGenerator.Tile.Floor)
                {
                    SetSorting(SpawnRandom(floorArray, pos), LAYER_DEFAULT, 0);
                }
                else
                {
                    // 边界和实心岩石统一用 OutWall：不可通行、不可破坏
                    SetSorting(SpawnRandom(outWallArray, pos), LAYER_DEFAULT, 1);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // 出口
    // ------------------------------------------------------------------
    /// <summary>
    /// 修正精灵渲染层级。
    ///
    /// 工程的排序层（由下至上）是：Default -> Background -> Items -> Roles，
    /// 作者本意显然是「地板 Default / 道具 Items / 角色 Roles」。
    /// 但 Exit / Food / Soda / Wall 这些预制体全被留在了 Default 层，
    /// 和地板同层同序。出口与地板占同一格，排序完全相同时 Unity 的绘制顺序
    /// 是不确定的 —— 地板经常把出口盖住，表现就是"这一层没有出口"；
    /// 食物和可破坏墙体同理（会撞上看不见的墙）。
    ///
    /// 这里在生成时按类别归位，不改动预制体资源。
    /// 敌人和玩家本来就在 Roles 层，无需处理。
    /// </summary>
    const string LAYER_DEFAULT = "Default";
    const string LAYER_ITEMS = "Items";

    static void SetSorting(GameObject go, string layerName, int order)
    {
        if (go == null) return;
        SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
        if (sr == null) return;
        sr.sortingLayerName = layerName;
        sr.sortingOrder = order;
    }

    void SpawnExit()
    {
        if (exitPrefab == null) return;

        Vector3 pos = new Vector3(dungeon.ExitPosition.x, dungeon.ExitPosition.y, 0);
        GameObject go = Instantiate(exitPrefab, pos, Quaternion.identity);
        go.transform.SetParent(mapHolder);
        SetSorting(go, LAYER_ITEMS, 2);

        EnsureTriggerCollider(go, 0.7f);
        if (go.GetComponent<ExitPortal>() == null) go.AddComponent<ExitPortal>();

        Debug.Log(string.Format("[MapManager] 出口位置 ({0},{1})   该格是否地面 = {2}",
            dungeon.ExitPosition.x, dungeon.ExitPosition.y,
            dungeon.IsFloor(dungeon.ExitPosition.x, dungeon.ExitPosition.y)));
    }

    // ------------------------------------------------------------------
    // 食物 / 苏打 / 可破坏墙体
    // ------------------------------------------------------------------
    void SpawnItems()
    {
        List<Vector2Int> free = CollectFreeTiles();
        Shuffle(free);

        int level = CurrentLevel;
        int roomBonus = dungeon.Rooms.Count / 3;

        // 拾取物也随层数增加，保证补给跟得上难度
        int pickupCount = Mathf.Clamp(3 + level / 2 + roomBonus / 2, 3, 16);
        int wallCount = dungeon.Rng.Next(minCountWall, maxCountWall + 1);

        int cursor = SpawnPickups(free, 0, pickupCount);
        SpawnWalls(free, cursor, wallCount);
    }

    /// <summary>
    /// 从 foodArray 里随机取预制体生成拾取物。
    /// 场景里 foodArray 同时装了 Food 和 Soda 两个预制体，
    /// 所以直接按预制体名字判断类型（Food 回血 / Soda 加分）。
    /// </summary>
    int SpawnPickups(List<Vector2Int> free, int cursor, int count)
    {
        if (foodArray == null || foodArray.Length == 0) return cursor;

        for (int i = 0; i < count && cursor < free.Count; i++, cursor++)
        {
            Vector2Int cell = free[cursor];
            GameObject prefab = foodArray[dungeon.Rng.Next(foodArray.Length)];

            GameObject go = Instantiate(prefab, new Vector3(cell.x, cell.y, 0), Quaternion.identity);
            go.transform.SetParent(mapHolder);
            go.name = prefab.name + "_" + cell.x + "_" + cell.y;
            SetSorting(go, LAYER_ITEMS, 1);

            EnsureTriggerCollider(go, 0.5f);

            bool isSoda = prefab.name.ToLower().Contains("soda");
            Pickup p = go.AddComponent<Pickup>();
            p.kind = isSoda ? Pickup.PickupKind.Soda : Pickup.PickupKind.Food;
            p.value = isSoda ? 5 : 1;
        }
        return cursor;
    }

    void SpawnWalls(List<Vector2Int> free, int cursor, int count)
    {
        if (wallArray == null || wallArray.Length == 0) return;

        for (int i = 0; i < count && cursor < free.Count; i++, cursor++)
        {
            Vector2Int cell = free[cursor];
            GameObject prefab = wallArray[dungeon.Rng.Next(wallArray.Length)];
            GameObject go = Instantiate(prefab, new Vector3(cell.x, cell.y, 0), Quaternion.identity);
            go.transform.SetParent(mapHolder);
            SetSorting(go, LAYER_ITEMS, 0);

            if (go.GetComponent<DestructibleWall>() == null) go.AddComponent<DestructibleWall>();
            blockerCells.Add(cell);
        }
    }

    // ------------------------------------------------------------------
    // 敌人
    // ------------------------------------------------------------------
    void SpawnEnemies()
    {
        if (enemyArray == null || enemyArray.Length == 0) return;

        int level = CurrentLevel;
        int roomBonus = dungeon.Rooms.Count / 3;

        // 难度曲线：敌人数量随层数线性增长（带上限，避免后期卡死）。
        // 原来写的是 level / 2，整数除法导致第 1~3 层敌人数完全相同，
        // 玩起来"换了层却没变难"。
        int enemyCount = Mathf.Clamp(2 + level + roomBonus / 2, 2, 18);

        // 敌人属性也随层数成长，否则后期只是数量堆叠、单体毫无威胁
        int enemyHealth = 2 + level / 2;
        int enemyDamage = 1 + level / 4;

        // 离出生点太近的格子不放敌人，否则一出生就被围殴
        List<Vector2Int> candidates = new List<Vector2Int>();
        List<Vector2Int> tiles = dungeon.ReachableTiles();
        for (int i = 0; i < tiles.Count; i++)
        {
            Vector2Int t = tiles[i];
            if (protectedTiles.Contains(t)) continue;
            if (occupied.ContainsKey(t)) continue;
            if (blockerCells.Contains(t)) continue;
            if (dungeon.Distance[t.x, t.y] < safeSpawnRadius) continue;
            candidates.Add(t);
        }
        Shuffle(candidates);

        int count = Mathf.Min(enemyCount, candidates.Count);
        for (int i = 0; i < count; i++)
        {
            Vector2Int cell = candidates[i];
            GameObject prefab = enemyArray[dungeon.Rng.Next(enemyArray.Length)];
            GameObject go = Instantiate(prefab, new Vector3(cell.x, cell.y, 0), Quaternion.identity);
            go.transform.SetParent(mapHolder);
            go.name = "Enemy_" + cell.x + "_" + cell.y;

            EnsureSolidCollider(go, 0.85f);

            Enemy e = go.AddComponent<Enemy>();
            e.health = enemyHealth;
            e.damage = enemyDamage;
            e.Setup(this, currentPlayer != null ? currentPlayer.transform : null, cell);
            occupied[cell] = e;
        }
    }

    // ------------------------------------------------------------------
    // 玩家
    // ------------------------------------------------------------------
    void SpawnPlayer()
    {
        if (!spawnPlayer) return;

        Vector3 pos = new Vector3(dungeon.PlayerStart.x, dungeon.PlayerStart.y, 0);

        if (currentPlayer != null)
        {
            currentPlayer.GetComponent<Player>().Revive(pos);
            return;
        }

        if (playerPrefab == null)
        {
            Debug.LogWarning("[MapManager] 没有指定 playerPrefab，已跳过玩家生成。" +
                             "请把 Player.prefab 拖到 Inspector 的「玩家」一栏。");
            return;
        }

        currentPlayer = Instantiate(playerPrefab, pos, Quaternion.identity);
        currentPlayer.name = "Player";

        // 诊断：确认出生点确实是地面。这条日志同时用来排查"角色卡墙里"的问题
        Debug.Log(string.Format(
            "[MapManager] 玩家出生点 ({0},{1})   该格是否地面 = {2}   房间数 = {3}",
            dungeon.PlayerStart.x, dungeon.PlayerStart.y,
            dungeon.IsFloor(dungeon.PlayerStart.x, dungeon.PlayerStart.y),
            dungeon.Rooms.Count));
        // 玩家不挂在 mapHolder 下，否则重新生成地图时会被一起销毁
    }

    // ==================================================================
    // 过关 / 重开
    // ==================================================================
    /// <summary>防止在一次重新生成的中途被再次触发（出口的触发回调可能连发）</summary>
    private bool regenerating;

    public void GoToNextLevel()
    {
        if (regenerating) return;
        regenerating = true;

        if (gameManage != null) gameManage.level++;
        Debug.Log(string.Format("[MapManager] 进入第 {0} 层", CurrentLevel));

        Generate();

        regenerating = false;
    }

    public void RestartRun()
    {
        if (regenerating) return;
        regenerating = true;

        if (gameManage != null) gameManage.ResetRun();
        Debug.Log("[MapManager] 角色死亡，从第 1 层重新开始");

        Generate();

        regenerating = false;
    }

    int CurrentLevel
    {
        get { return (gameManage != null) ? Mathf.Max(1, gameManage.level) : 1; }
    }

    // ==================================================================
    // 格子占用查询（供敌人使用）
    // ==================================================================
    public Vector2Int WorldToCell(Vector2 position)
    {
        return new Vector2Int(Mathf.RoundToInt(position.x), Mathf.RoundToInt(position.y));
    }

    /// <summary>该格子是否能让敌人站上去（地面 + 无其他敌人 + 无可破坏墙体）</summary>
    public bool IsCellFree(Vector2Int cell, Enemy self)
    {
        if (dungeon == null || !dungeon.IsFloor(cell.x, cell.y)) return false;
        if (blockerCells.Contains(cell)) return false;

        Enemy other;
        if (occupied.TryGetValue(cell, out other) && other != null && other != self) return false;

        // 玩家站着的格子也不让进（贴身时应该走攻击分支）
        if (currentPlayer != null && WorldToCell(currentPlayer.transform.position) == cell) return false;

        return true;
    }

    public void OccupyCell(Vector2Int cell, Enemy who)
    {
        occupied[cell] = who;
    }

    public void VacateCell(Vector2Int cell, Enemy who)
    {
        Enemy current;
        if (occupied.TryGetValue(cell, out current) && current == who)
            occupied.Remove(cell);
    }

    /// <summary>可破坏墙体碎掉时调用，让这格重新变成可通行</summary>
    public void NotifyCellFreed(Vector2Int cell)
    {
        blockerCells.Remove(cell);
    }

    // ==================================================================
    // 工具
    // ==================================================================
    List<Vector2Int> CollectFreeTiles()
    {
        List<Vector2Int> free = new List<Vector2Int>();
        List<Vector2Int> tiles = dungeon.ReachableTiles();

        for (int i = 0; i < tiles.Count; i++)
        {
            Vector2Int t = tiles[i];
            if (t == dungeon.PlayerStart) continue;
            if (t == dungeon.ExitPosition) continue;
            if (protectedTiles.Contains(t)) continue;
            free.Add(t);
        }
        return free;
    }

    void Shuffle(List<Vector2Int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = dungeon.Rng.Next(i + 1);
            Vector2Int tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }

    GameObject SpawnRandom(GameObject[] prefabs, Vector3 pos)
    {
        if (prefabs == null || prefabs.Length == 0) return null;

        GameObject prefab = prefabs[dungeon.Rng.Next(prefabs.Length)];
        GameObject go = Instantiate(prefab, pos, Quaternion.identity);
        go.transform.SetParent(mapHolder);
        return go;
    }

    /// <summary>补一个触发碰撞体（食物、苏打、出口用）</summary>
    static void EnsureTriggerCollider(GameObject go, float size)
    {
        BoxCollider2D box = go.GetComponent<BoxCollider2D>();
        if (box == null) box = go.AddComponent<BoxCollider2D>();
        box.isTrigger = true;
        box.size = new Vector2(size, size);
    }

    /// <summary>补一个实心碰撞体（敌人用，挡住玩家去路，撞上去就是攻击）</summary>
    static void EnsureSolidCollider(GameObject go, float size)
    {
        BoxCollider2D box = go.GetComponent<BoxCollider2D>();
        if (box == null) box = go.AddComponent<BoxCollider2D>();
        box.isTrigger = false;
        box.size = new Vector2(size, size);
    }
}
