using Microsoft.Data.Sqlite;

// --------- constants / helpers ----------
const int EventDown = 1;
const int EventUp = 2;

static long UtcUsNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

// "Physical key id" in Windows terms
static string PhysicalKeyId(int makeCode, int e0, int e1) => $"{makeCode:X2}:{e0}:{e1}";

// Insert helper (keeps this step readable)
static void InsertKeyEvent(
    SqliteConnection connection,
    long sessionId,
    long timestampUtcUs,
    int eventType,
    int vkey,
    int makeCode,
    int e0,
    int e1,
    int modifiersMask,
    bool isRepeat)
{
    using var cmd = connection.CreateCommand();
    cmd.CommandText = """
    INSERT INTO key_events (
      session_id, timestamp_utc_us,
      event_type, vkey, make_code, e0, e1, modifiers_mask, is_repeat
    )
    VALUES (
      $session_id, $timestamp_utc_us,
      $event_type, $vkey, $make_code, $e0, $e1, $modifiers_mask, $is_repeat
    );
    """;

    cmd.Parameters.AddWithValue("$session_id", sessionId);
    cmd.Parameters.AddWithValue("$timestamp_utc_us", timestampUtcUs);
    cmd.Parameters.AddWithValue("$event_type", eventType);
    cmd.Parameters.AddWithValue("$vkey", vkey);
    cmd.Parameters.AddWithValue("$make_code", makeCode);
    cmd.Parameters.AddWithValue("$e0", e0);
    cmd.Parameters.AddWithValue("$e1", e1);
    cmd.Parameters.AddWithValue("$modifiers_mask", modifiersMask);
    cmd.Parameters.AddWithValue("$is_repeat", isRepeat ? 1 : 0);

    cmd.ExecuteNonQuery();
}

// --------- open db ----------
var dbPath = "keystroke_tracker.sqlite";
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

using var connection = new SqliteConnection(connectionString);
connection.Open();

// Create tables (same as before)
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = """
    CREATE TABLE IF NOT EXISTS sessions (
      session_id        INTEGER PRIMARY KEY,
      started_utc_us    INTEGER NOT NULL
    );

    CREATE TABLE IF NOT EXISTS key_events (
      event_id          INTEGER PRIMARY KEY,
      session_id        INTEGER NOT NULL,
      timestamp_utc_us  INTEGER NOT NULL,

      event_type        INTEGER NOT NULL,  -- 1=down, 2=up
      vkey              INTEGER,
      make_code         INTEGER,
      e0                INTEGER NOT NULL DEFAULT 0,
      e1                INTEGER NOT NULL DEFAULT 0,
      modifiers_mask    INTEGER NOT NULL DEFAULT 0,
      is_repeat         INTEGER NOT NULL DEFAULT 0,

      FOREIGN KEY(session_id) REFERENCES sessions(session_id)
    );
    """;
    cmd.ExecuteNonQuery();
}

// Insert session and capture ID
long sessionId;
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = """
    INSERT INTO sessions (started_utc_us)
    VALUES ($started_utc_us);

    SELECT last_insert_rowid();
    """;
    cmd.Parameters.AddWithValue("$started_utc_us", UtcUsNow());
    sessionId = (long)cmd.ExecuteScalar()!;
}

// --------- simulate events + repeat detection ----------
const int VkA = 0x41;
const int MakeCodeA = 0x1E;
const int E0 = 0;
const int E1 = 0;
const int Modifiers = 0;

// Track which physical keys are currently down
var downKeys = new HashSet<string>();

bool ProcessEvent(int eventType, int vkey, int makeCode, int e0, int e1)
{
    var keyId = PhysicalKeyId(makeCode, e0, e1);

    if (eventType == EventDown)
    {
        var isRepeat = downKeys.Contains(keyId);
        Console.WriteLine($"DOWN {keyId} vkey=0x{vkey:X} repeat={isRepeat}");
        downKeys.Add(keyId);
        return isRepeat;
    }
    else
    {
        Console.WriteLine($"UP   {keyId} vkey=0x{vkey:X}");
        downKeys.Remove(keyId);
        return false;
    }
}

// 1) A down
var t1 = UtcUsNow();
var r1 = ProcessEvent(EventDown, VkA, MakeCodeA, E0, E1);
InsertKeyEvent(connection, sessionId, t1, EventDown, VkA, MakeCodeA, E0, E1, Modifiers, isRepeat: r1);

// 2) A up (matching key)
var t2 = UtcUsNow();
var r2 = ProcessEvent(EventUp, VkA, MakeCodeA, E0, E1);
InsertKeyEvent(connection, sessionId, t2, EventUp, VkA, MakeCodeA, E0, E1, Modifiers, isRepeat: r2);

// 3) A down again (should NOT be a repeat because we saw an up)
var t3 = UtcUsNow();
var r3 = ProcessEvent(EventDown, VkA, MakeCodeA, E0, E1);
InsertKeyEvent(connection, sessionId, t3, EventDown, VkA, MakeCodeA, E0, E1, Modifiers, isRepeat: r3);

Console.WriteLine($"\nSQLite DB path: {Path.GetFullPath(dbPath)}");
