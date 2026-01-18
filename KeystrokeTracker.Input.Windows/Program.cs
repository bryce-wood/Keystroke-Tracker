using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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
        private const int RIDI_DEVICENAME = 0x20000007;

        private const int RIDEV_INPUTSINK = 0x00000100; // receive input even when not focused

        // create the form
        public RawInputForm()
        {
            Text = "KeystrokeTracker (hidden)";
        }

        // make sure the form is always hidden
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Register to receive raw keyboard input
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
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                // 1) Ask Windows how big the raw input payload is
                uint dwSize = 0;
                if (GetRawInputData(m.LParam, RID_INPUT, IntPtr.Zero, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != 0)
                    return;

                // 2) Allocate and fetch the payload
                IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
                try
                {
                    if (GetRawInputData(m.LParam, RID_INPUT, buffer, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != dwSize)
                        return;

                    // 3) Interpret as RAWINPUT and read keyboard fields
                    var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);

                    if (raw.header.dwType == RIM_TYPEKEYBOARD)
                    {
                        var kb = raw.keyboard;

                        bool isUp = (kb.Flags & RawKeyboardFlags.RI_KEY_BREAK) != 0;
                        bool e0 = (kb.Flags & RawKeyboardFlags.RI_KEY_E0) != 0;
                        bool e1 = (kb.Flags & RawKeyboardFlags.RI_KEY_E1) != 0;

                        // Minimal: print the core fields you planned to store
                        Console.WriteLine(
                            $"{(isUp ? "UP  " : "DOWN")} " +
                            $"VKey=0x{kb.VKey:X} MakeCode=0x{kb.MakeCode:X} E0={(e0 ? 1 : 0)} E1={(e1 ? 1 : 0)} " +
                            $"Device=0x{raw.header.hDevice.ToInt64():X}"
                        );
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            base.WndProc(ref m);
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

        [StructLayout(LayoutKind.Explicit)]
        private struct RAWINPUT
        {
            [FieldOffset(0)]
            public RAWINPUTHEADER header;

            [FieldOffset(16)]
            public RAWKEYBOARD keyboard;
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
