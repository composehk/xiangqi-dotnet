using Microsoft.Data.Sqlite;

namespace Xiangqi.Server;

/// <summary>Persisted state of one 断点续弈 session.</summary>
public sealed class ContinueSession
{
    public string Id { get; set; } = "";
    public string Token { get; set; } = "";
    public string Nonce { get; set; } = "";
    public long Timestamp { get; set; }
    public long CreatedAt { get; set; }
    public string SourceFen { get; set; } = "";
    public string SourceMoves { get; set; } = "";
    public int Ply { get; set; }
    public string StartFen { get; set; } = "";
    public string Difficulty { get; set; } = "medium";
    public string HumanColor { get; set; } = "red";
    /// <summary>pending_auth → pending_payment → active → finished</summary>
    public string Status { get; set; } = "pending_auth";
    public string? Address { get; set; }
    public string? Signature { get; set; }
    public string? Txid { get; set; }
    public string Moves { get; set; } = "";
    public string Result { get; set; } = "ongoing";
    public long UpdatedAt { get; set; }
}

/// <summary>SQLite persistence for continue sessions, payments and used txids.</summary>
public sealed class ContinueStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public ContinueStore(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DbPath, Cache = SqliteCacheMode.Shared }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS continue_sessions (
              id TEXT PRIMARY KEY, token TEXT NOT NULL, nonce TEXT NOT NULL, ts INTEGER NOT NULL, created_at INTEGER NOT NULL,
              source_fen TEXT NOT NULL, source_moves TEXT NOT NULL, ply INTEGER NOT NULL, start_fen TEXT NOT NULL,
              difficulty TEXT NOT NULL, human_color TEXT NOT NULL, status TEXT NOT NULL,
              address TEXT, signature TEXT, txid TEXT UNIQUE, moves TEXT NOT NULL, result TEXT NOT NULL, updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS used_txids (txid TEXT PRIMARY KEY, session_id TEXT NOT NULL, used_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS payments (
              id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, txid TEXT NOT NULL, address TEXT,
              amount_sompi INTEGER NOT NULL, confirmations INTEGER NOT NULL, verified_at INTEGER NOT NULL);
            """);
    }

    public string DbPath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Insert(ContinueSession s)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO continue_sessions (id, token, nonce, ts, created_at, source_fen, source_moves, ply, start_fen,
                  difficulty, human_color, status, address, signature, txid, moves, result, updated_at)
                VALUES ($id,$token,$nonce,$ts,$created,$sfen,$smoves,$ply,$start,$diff,$color,$status,$addr,$sig,$txid,$moves,$result,$updated)
                """;
            Bind(cmd, s);
            cmd.ExecuteNonQuery();
        }
    }

    public void Update(ContinueSession s)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE continue_sessions SET ts=$ts, status=$status, address=$addr, signature=$sig, txid=$txid,
                  moves=$moves, result=$result, updated_at=$updated WHERE id=$id
                """;
            Bind(cmd, s);
            cmd.ExecuteNonQuery();
        }
    }

    public ContinueSession? Get(string id)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, token, nonce, ts, created_at, source_fen, source_moves, ply, start_fen, difficulty, human_color, status, address, signature, txid, moves, result, updated_at FROM continue_sessions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return null;
            return new ContinueSession
            {
                Id = r.GetString(0), Token = r.GetString(1), Nonce = r.GetString(2), Timestamp = r.GetInt64(3),
                CreatedAt = r.GetInt64(4), SourceFen = r.GetString(5), SourceMoves = r.GetString(6), Ply = r.GetInt32(7),
                StartFen = r.GetString(8), Difficulty = r.GetString(9), HumanColor = r.GetString(10), Status = r.GetString(11),
                Address = r.IsDBNull(12) ? null : r.GetString(12), Signature = r.IsDBNull(13) ? null : r.GetString(13),
                Txid = r.IsDBNull(14) ? null : r.GetString(14), Moves = r.GetString(15), Result = r.GetString(16),
                UpdatedAt = r.GetInt64(17),
            };
        }
    }

    /// <summary>
    /// Atomically marks <paramref name="txid"/> as used by <paramref name="sessionId"/> (anti-replay),
    /// records the payment and activates the session. Returns false if the txid was already used.
    /// </summary>
    public bool TryConsumeTxid(ContinueSession s, string txid, long amountSompi, long confirmations)
    {
        lock (_gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using (var ins = c.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO used_txids (txid, session_id, used_at) VALUES ($t, $s, $now)";
                ins.Parameters.AddWithValue("$t", txid);
                ins.Parameters.AddWithValue("$s", s.Id);
                ins.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (ins.ExecuteNonQuery() == 0)
                {
                    tx.Rollback();
                    return false;
                }
            }
            using (var pay = c.CreateCommand())
            {
                pay.Transaction = tx;
                pay.CommandText = "INSERT INTO payments (session_id, txid, address, amount_sompi, confirmations, verified_at) VALUES ($s,$t,$a,$amt,$conf,$now)";
                pay.Parameters.AddWithValue("$s", s.Id);
                pay.Parameters.AddWithValue("$t", txid);
                pay.Parameters.AddWithValue("$a", (object?)s.Address ?? DBNull.Value);
                pay.Parameters.AddWithValue("$amt", amountSompi);
                pay.Parameters.AddWithValue("$conf", confirmations);
                pay.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                pay.ExecuteNonQuery();
            }
            using (var upd = c.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE continue_sessions SET txid=$t, status='active', updated_at=$now WHERE id=$s";
                upd.Parameters.AddWithValue("$t", txid);
                upd.Parameters.AddWithValue("$s", s.Id);
                upd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                upd.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
    }

    public bool IsTxidUsed(string txid)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM used_txids WHERE txid=$t";
            cmd.Parameters.AddWithValue("$t", txid);
            return cmd.ExecuteScalar() != null;
        }
    }

    private static void Bind(SqliteCommand cmd, ContinueSession s)
    {
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$token", s.Token);
        cmd.Parameters.AddWithValue("$nonce", s.Nonce);
        cmd.Parameters.AddWithValue("$ts", s.Timestamp);
        cmd.Parameters.AddWithValue("$created", s.CreatedAt);
        cmd.Parameters.AddWithValue("$sfen", s.SourceFen);
        cmd.Parameters.AddWithValue("$smoves", s.SourceMoves);
        cmd.Parameters.AddWithValue("$ply", s.Ply);
        cmd.Parameters.AddWithValue("$start", s.StartFen);
        cmd.Parameters.AddWithValue("$diff", s.Difficulty);
        cmd.Parameters.AddWithValue("$color", s.HumanColor);
        cmd.Parameters.AddWithValue("$status", s.Status);
        cmd.Parameters.AddWithValue("$addr", (object?)s.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sig", (object?)s.Signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$txid", (object?)s.Txid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$moves", s.Moves);
        cmd.Parameters.AddWithValue("$result", s.Result);
        cmd.Parameters.AddWithValue("$updated", s.UpdatedAt);
    }
}
