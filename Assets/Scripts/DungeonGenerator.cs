using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 程序化地牢生成器。
///
/// 这个类不继承 MonoBehaviour，也不碰任何场景对象，只负责"算出一张地图"，
/// 所以它可以单独测试、单独复用（换关卡、换引擎都能整段搬走）。
///
/// 生成流程：
///   1. 整张地图先填满实心岩石
///   2. 随机撒下互不重叠的矩形房间（房间之间强制留 1 格间隔）
///   3. 用 Prim 最小生成树把所有房间连成一棵树，挖出 L 形走廊
///   4. 再额外挖几条走廊形成环路，避免"只有唯一通路"的无聊结构
///   5. 从出生点做一次 BFS 求出距离场，把出口放在"可达且最远"的格子上
///      —— 这一步顺带保证了出口一定可达，不需要额外校验
/// </summary>
public class DungeonGenerator
{
    public enum Tile
    {
        Solid = 0,   // 实心岩石，不可通行
        Floor = 1    // 地面，可通行
    }

    // 四方向邻接：右、左、上、下
    static readonly Vector2Int[] Dirs =
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0),
        new Vector2Int(0, 1), new Vector2Int(0, -1)
    };

    public readonly int Width;
    public readonly int Height;
    public readonly int Seed;

    /// <summary>地图格子，索引方式 [x, y]</summary>
    public Tile[,] Grid { get; private set; }

    /// <summary>距离场：从出生点走到该格子的最短步数，-1 表示不可达</summary>
    public int[,] Distance { get; private set; }

    /// <summary>所有房间的矩形范围</summary>
    public List<RectInt> Rooms { get; private set; }

    /// <summary>玩家出生点（第一个房间的中心）</summary>
    public Vector2Int PlayerStart { get; private set; }

    /// <summary>出口位置（BFS 可达范围内离出生点最远的格子）</summary>
    public Vector2Int ExitPosition { get; private set; }

    /// <summary>
    /// 整张地牢共用的随机数发生器。
    /// 外部（比如 MapManager）放置怪物、道具时应当复用它，
    /// 这样"同一个 seed 生成同一张地图"的承诺才成立。
    /// </summary>
    public System.Random Rng { get; private set; }

    readonly int minRoomSize;
    readonly int maxRoomSize;
    readonly int maxRoomAttempts;
    readonly int extraConnections;

    public DungeonGenerator(int width, int height, int seed,
                            int minRoomSize = 4, int maxRoomSize = 7,
                            int maxRoomAttempts = 150, int extraConnections = 2)
    {
        // 至少 8x8，否则房间里塞不下东西
        Width = Mathf.Max(8, width);
        Height = Mathf.Max(8, height);
        Seed = seed;

        this.minRoomSize = Mathf.Max(2, minRoomSize);
        this.maxRoomSize = Mathf.Max(this.minRoomSize, maxRoomSize);
        this.maxRoomAttempts = Mathf.Max(1, maxRoomAttempts);
        this.extraConnections = Mathf.Max(0, extraConnections);
    }

    /// <summary>执行生成，调用后即可读取 Grid / PlayerStart / ExitPosition 等结果</summary>
    public void Generate()
    {
        Rng = new System.Random(Seed);

        Grid = new Tile[Width, Height];
        Rooms = new List<RectInt>();

        PlaceRooms();
        ConnectRooms();

        PlayerStart = RoomCenter(Rooms[0]);

        BuildDistanceField(PlayerStart);
        ExitPosition = FindFarthestReachable();
    }

    // ------------------------------------------------------------------
    // 第 1~2 步：放置房间
    // ------------------------------------------------------------------
    void PlaceRooms()
    {
        // 房间数量按地图面积估算
        int target = Mathf.Clamp((Width * Height) / 60, 3, 40);

        // 单轴尺寸上限：保证同一方向上至少能并排放下两个房间（含 1 格间隔）。
        // 没有这一步的话，10x10 这种小地图会因为房间太大而只生成出 1 个房间。
        int limitW = Mathf.Max(3, (Width - 3) / 2);
        int limitH = Mathf.Max(3, (Height - 3) / 2);
        int maxW = Mathf.Min(maxRoomSize, limitW);
        int maxH = Mathf.Min(maxRoomSize, limitH);
        int minW = Mathf.Min(minRoomSize, maxW);
        int minH = Mathf.Min(minRoomSize, maxH);

        for (int attempt = 0; attempt < maxRoomAttempts && Rooms.Count < target; attempt++)
        {
            int w = Rng.Next(minW, maxW + 1);
            int h = Rng.Next(minH, maxH + 1);

            // 至少留出 1 格作边界墙
            int x = Rng.Next(1, Width - w);
            int y = Rng.Next(1, Height - h);

            RectInt room = new RectInt(x, y, w, h);
            if (OverlapsExisting(room)) continue;

            Rooms.Add(room);
            CarveRoom(room);
        }

        // 兜底：地图太小或运气太差，一个房间都没放下去，就挖一个居中的大房间
        if (Rooms.Count == 0)
        {
            RectInt fallback = new RectInt(1, 1, Width - 2, Height - 2);
            Rooms.Add(fallback);
            CarveRoom(fallback);
        }
    }

    /// <summary>把矩形向外扩 1 格再判断相交，等价于"两个房间之间至少隔 1 格"</summary>
    bool OverlapsExisting(RectInt room)
    {
        RectInt padded = new RectInt(room.x - 1, room.y - 1, room.width + 2, room.height + 2);
        for (int i = 0; i < Rooms.Count; i++)
        {
            RectInt other = Rooms[i];
            bool apart = padded.x + padded.width <= other.x ||
                         other.x + other.width <= padded.x ||
                         padded.y + padded.height <= other.y ||
                         other.y + other.height <= padded.y;
            if (!apart) return true;
        }
        return false;
    }

    void CarveRoom(RectInt room)
    {
        for (int x = room.xMin; x < room.xMax; x++)
            for (int y = room.yMin; y < room.yMax; y++)
                if (InsideBorder(x, y)) Grid[x, y] = Tile.Floor;
    }

    // ------------------------------------------------------------------
    // 第 3~4 步：连接房间
    // ------------------------------------------------------------------
    void ConnectRooms()
    {
        int n = Rooms.Count;
        if (n < 2) return;

        // Prim 最小生成树：每次把"离已有连通块最近"的房间接进来
        bool[] inTree = new bool[n];
        inTree[0] = true;
        int connected = 1;

        while (connected < n)
        {
            int bestFrom = -1, bestTo = -1, bestDist = int.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (!inTree[i]) continue;
                for (int j = 0; j < n; j++)
                {
                    if (inTree[j]) continue;
                    int d = RoomDistance(Rooms[i], Rooms[j]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestFrom = i;
                        bestTo = j;
                    }
                }
            }
            if (bestTo < 0) break;   // 理论上不会发生，防御性写法

            CarveCorridor(RoomCenter(Rooms[bestFrom]), RoomCenter(Rooms[bestTo]));
            inTree[bestTo] = true;
            connected++;
        }

        // 额外环路：让地图有不止一条通路，避免走回头路
        for (int k = 0; k < extraConnections && n >= 3; k++)
        {
            int a = Rng.Next(n);
            int b = Rng.Next(n);
            if (a == b) continue;
            CarveCorridor(RoomCenter(Rooms[a]), RoomCenter(Rooms[b]));
        }
    }

    static int RoomDistance(RectInt a, RectInt b)
    {
        Vector2Int ca = RoomCenter(a);
        Vector2Int cb = RoomCenter(b);
        return Mathf.Abs(ca.x - cb.x) + Mathf.Abs(ca.y - cb.y);   // 曼哈顿距离
    }

    static Vector2Int RoomCenter(RectInt r)
    {
        return new Vector2Int(r.x + r.width / 2, r.y + r.height / 2);
    }

    /// <summary>随机选择"先横后竖"或"先竖后横"，挖出一条 L 形走廊</summary>
    void CarveCorridor(Vector2Int a, Vector2Int b)
    {
        if (Rng.Next(2) == 0)
        {
            CarveHorizontal(a.x, b.x, a.y);
            CarveVertical(a.y, b.y, b.x);
        }
        else
        {
            CarveVertical(a.y, b.y, a.x);
            CarveHorizontal(a.x, b.x, b.y);
        }
    }

    void CarveHorizontal(int x0, int x1, int y)
    {
        if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
        for (int x = x0; x <= x1; x++) Carve(x, y);
    }

    void CarveVertical(int y0, int y1, int x)
    {
        if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
        for (int y = y0; y <= y1; y++) Carve(x, y);
    }

    void Carve(int x, int y)
    {
        if (!InsideBorder(x, y)) return;   // 最外一圈永远是边界墙
        Grid[x, y] = Tile.Floor;
    }

    // ------------------------------------------------------------------
    // 第 5 步：BFS 距离场
    // ------------------------------------------------------------------
    void BuildDistanceField(Vector2Int from)
    {
        Distance = new int[Width, Height];
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                Distance[x, y] = -1;

        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        Distance[from.x, from.y] = 0;
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            Vector2Int cur = queue.Dequeue();
            int next = Distance[cur.x, cur.y] + 1;

            for (int i = 0; i < Dirs.Length; i++)
            {
                int nx = cur.x + Dirs[i].x;
                int ny = cur.y + Dirs[i].y;

                if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) continue;
                if (Grid[nx, ny] != Tile.Floor) continue;
                if (Distance[nx, ny] != -1) continue;   // 已经访问过

                Distance[nx, ny] = next;
                queue.Enqueue(new Vector2Int(nx, ny));
            }
        }
    }

    Vector2Int FindFarthestReachable()
    {
        Vector2Int best = PlayerStart;
        int bestDist = 0;
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                if (Distance[x, y] > bestDist)
                {
                    bestDist = Distance[x, y];
                    best = new Vector2Int(x, y);
                }
            }
        }
        return best;
    }

    // ------------------------------------------------------------------
    // 对外查询接口
    // ------------------------------------------------------------------
    bool InsideBorder(int x, int y)
    {
        return x > 0 && y > 0 && x < Width - 1 && y < Height - 1;
    }

    public bool IsFloor(int x, int y)
    {
        return x >= 0 && y >= 0 && x < Width && y < Height && Grid[x, y] == Tile.Floor;
    }

    public bool IsReachable(int x, int y)
    {
        return x >= 0 && y >= 0 && x < Width && y < Height && Distance[x, y] >= 0;
    }

    /// <summary>所有"可达"的地面格子，用来放怪物和道具</summary>
    public List<Vector2Int> ReachableTiles()
    {
        List<Vector2Int> list = new List<Vector2Int>();
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                if (Grid[x, y] == Tile.Floor && Distance[x, y] >= 0)
                    list.Add(new Vector2Int(x, y));
        return list;
    }

    /// <summary>
    /// 从出口回溯到出生点的最短路径。
    /// 用途：放置可破坏墙体时避开这条路径，保证出口永远不会被堵死。
    /// </summary>
    public List<Vector2Int> ShortestPathToExit()
    {
        List<Vector2Int> path = new List<Vector2Int>();
        if (!IsReachable(ExitPosition.x, ExitPosition.y)) return path;

        Vector2Int cur = ExitPosition;
        int guard = Width * Height + 1;   // 防御性上限，避免意外死循环

        while (cur != PlayerStart && guard-- > 0)
        {
            path.Add(cur);
            int d = Distance[cur.x, cur.y];
            bool moved = false;

            for (int i = 0; i < Dirs.Length; i++)
            {
                int nx = cur.x + Dirs[i].x;
                int ny = cur.y + Dirs[i].y;
                if (!IsReachable(nx, ny)) continue;
                if (Distance[nx, ny] == d - 1)
                {
                    cur = new Vector2Int(nx, ny);
                    moved = true;
                    break;
                }
            }
            if (!moved) break;
        }

        path.Add(PlayerStart);
        return path;
    }

    /// <summary>把地图打成文本，方便在 Console 里肉眼检查生成结果</summary>
    public string ToAscii()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendFormat("Dungeon {0}x{1}  seed={2}  rooms={3}  start=({4},{5})  exit=({6},{7})\n",
            Width, Height, Seed, Rooms.Count,
            PlayerStart.x, PlayerStart.y, ExitPosition.x, ExitPosition.y);

        for (int y = Height - 1; y >= 0; y--)
        {
            for (int x = 0; x < Width; x++)
            {
                if (x == PlayerStart.x && y == PlayerStart.y) sb.Append('P');
                else if (x == ExitPosition.x && y == ExitPosition.y) sb.Append('E');
                else sb.Append(Grid[x, y] == Tile.Floor ? '.' : '#');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
