using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace KeystrokeTracker.Input.Windows
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new RawInputForm());
        }
    }

    internal sealed class RawInputForm : Form
    {
        // Windows message ID for Raw Input
        private const int WM_INPUT = 0x00FF;

        // Raw Input constants
        private const int RIM_TYPEKEYBOARD = 1;
        private const int RID_INPUT = 0x10000003;

        private const int RIDEV_INPUTSINK = 0x00000100; // receive input even when not focused

        private SqliteConnection? _db;
        private long _sessionId;

        // Track which physical keys are currently down (for repeat detection)
        private readonly HashSet<string> _downKeys = new();

        private static long UtcUsNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        private static string PhysicalKeyId(int makeCode, int e0, int e1) => $"{makeCode:X2}:{e0}:{e1}";

        private static readonly string LogPath =
            Path.Combine(AppContext.BaseDirectory, "kt_debug.log");

        private static void Log(string msg)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            Debug.WriteLine(line);
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { /* ignore logging failures */ }
        }

        public RawInputForm()
        {
            Text = "KeystrokeTracker (hidden)";
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;

            Log("[CTOR] RawInputForm constructed");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            Log($"[HANDLE] Created. Handle=0x{Handle.ToInt64():X}");

            // 1) Register to receive raw keyboard input
            var rid = new RAWINPUTDEVICE[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = 0x01, // Generic Desktop Controls
                    usUsage = 0x06,     // Keyboard
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = Handle
                }
            };

            if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                int err = Marshal.GetLastWin32Error();
                Log($"[RAW] RegisterRawInputDevices FAILED err={err}");
                throw new System.ComponentModel.Win32Exception(err);
            }

            Log("[RAW] RegisterRawInputDevices OK");

            // 2) Open SQLite DB + create schema + create a session
            var dbPath = Path.Combine(AppContext.BaseDirectory, "keystroke_tracker.sqlite");
            Log($"[DB] Path = {dbPath}");

            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            _db = new SqliteConnection(cs);
            _db.Open();

            using (var cmd = _db.CreateCommand())
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

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                INSERT INTO sessions (started_utc_us)
                VALUES ($started_utc_us);

                SELECT last_insert_rowid();
                """;
                cmd.Parameters.AddWithValue("$started_utc_us", UtcUsNow());
                _sessionId = (long)cmd.ExecuteScalar()!;
            }

            Log($"[DB] Opened OK session_id={_sessionId}");
            Log($"[DB] File exists? {File.Exists(dbPath)}");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                Log("[WM_INPUT] message arrived");

                // Uncomment this if you want a log line for every WM_INPUT message (very spammy)
                // Log("[WM_INPUT] received");

                // 1) Ask Windows how big the raw input payload is
                uint dwSize = 0;
                uint res1 = GetRawInputData(
                    m.LParam,
                    RID_INPUT,
                    IntPtr.Zero,
                    ref dwSize,
                    (uint)Marshal.SizeOf<RAWINPUTHEADER>());

                Log($"[WM_INPUT] sizeQuery res=0x{res1:X} size={dwSize}");

                if (res1 == 0xFFFFFFFF) // error
                {
                    int err = Marshal.GetLastWin32Error();
                    Log($"[WM_INPUT] sizeQuery FAILED err={err}");
                    return;
                }

                if (dwSize == 0)
                {
                    Log("[WM_INPUT] sizeQuery returned size=0 (unexpected)");
                    return;
                }

                // 2) Allocate and fetch the payload
                IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
                try
                {
                    uint res2 = GetRawInputData(
                        m.LParam,
                        RID_INPUT,
                        buffer,
                        ref dwSize,
                        (uint)Marshal.SizeOf<RAWINPUTHEADER>());

                    Log($"[WM_INPUT] dataQuery res={res2} expectedSize={dwSize}");

                    if (res2 == 0xFFFFFFFF)
                    {
                        int err = Marshal.GetLastWin32Error();
                        Log($"[WM_INPUT] dataQuery FAILED err={err}");
                        return;
                    }


                    // 3) Read the header (safe on both 32-bit and 64-bit)
                    var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);

                    if (header.dwType == RIM_TYPEKEYBOARD)
                    {
                        // Keyboard data starts immediately after the header
                        int headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
                        IntPtr kbPtr = IntPtr.Add(buffer, headerSize);

                        var kb = Marshal.PtrToStructure<RAWKEYBOARD>(kbPtr);

                        bool isUp = (kb.Flags & RawKeyboardFlags.RI_KEY_BREAK) != 0;
                        bool e0 = (kb.Flags & RawKeyboardFlags.RI_KEY_E0) != 0;
                        bool e1 = (kb.Flags & RawKeyboardFlags.RI_KEY_E1) != 0;

                        int eventType = isUp ? 2 : 1;

                        int vkey = kb.VKey;
                        int makeCode = kb.MakeCode;

                        int e0i = e0 ? 1 : 0;
                        int e1i = e1 ? 1 : 0;

                        // Repeat detection: a DOWN is repeat if key is already down
                        bool isRepeat = false;
                        var keyId = PhysicalKeyId(makeCode, e0i, e1i);

                        if (eventType == 1) // down
                        {
                            isRepeat = _downKeys.Contains(keyId);
                            _downKeys.Add(keyId);
                        }
                        else // up
                        {
                            _downKeys.Remove(keyId);
                        }

                        // Log the event (Debug output + kt_debug.log)
                        Log(
                            $"{(isUp ? "UP  " : "DOWN")} " +
                            $"VKey=0x{vkey:X} MakeCode=0x{makeCode:X} E0={e0i} E1={e1i} " +
                            $"Repeat={(isRepeat ? 1 : 0)} Device=0x{header.hDevice.ToInt64():X}"
                        );

                        // Insert into SQLite
                        if (_db != null)
                        {
                            using var cmd = _db.CreateCommand();
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

                            cmd.Parameters.AddWithValue("$session_id", _sessionId);
                            cmd.Parameters.AddWithValue("$timestamp_utc_us", UtcUsNow());
                            cmd.Parameters.AddWithValue("$event_type", eventType);
                            cmd.Parameters.AddWithValue("$vkey", vkey);
                            cmd.Parameters.AddWithValue("$make_code", makeCode);
                            cmd.Parameters.AddWithValue("$e0", e0i);
                            cmd.Parameters.AddWithValue("$e1", e1i);
                            cmd.Parameters.AddWithValue("$modifiers_mask", 0);           // next step
                            cmd.Parameters.AddWithValue("$is_repeat", isRepeat ? 1 : 0);

                            cmd.ExecuteNonQuery();
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Log("[EXIT] Form closing, disposing DB");
            _db?.Dispose();
            base.OnFormClosed(e);
        }

        // ---------- P/Invoke structs + enums ----------

        [Flags]
        private enum RawKeyboardFlags : ushort
        {
            RI_KEY_MAKE = 0,
            RI_KEY_BREAK = 1,
            RI_KEY_E0 = 2,
            RI_KEY_E1 = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public RawKeyboardFlags Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [DllImport("User32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("User32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);
    }
}