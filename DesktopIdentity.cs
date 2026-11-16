using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CodexIdleHibernate
{
    // A GUI launched by the packaged Codex app otherwise inherits Codex's package identity.
    // Windows then groups it under Codex in the taskbar. Launch the standalone utility outside that identity.
    static class DesktopIdentity
    {
        [StructLayout(LayoutKind.Sequential)]
        struct StartupInfo
        {
            public uint cb;
            public IntPtr reserved, desktop, title;
            public uint x, y, xSize, ySize, xChars, yChars, fill, flags;
            public ushort show, reservedSize;
            public IntPtr reservedBytes, input, output, error;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct StartupInfoEx { public StartupInfo startup; public IntPtr attributes; }
        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInfo { public IntPtr process, thread; public uint processId, threadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetCurrentPackageFullName(ref uint length, StringBuilder name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")]
        static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
            bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
        public static bool RelaunchIfPackaged(string[] args)
        {
            uint length = 0;
            int result = GetCurrentPackageFullName(ref length, null);
            if (result == 15700) return false; // APPMODEL_ERROR_NO_PACKAGE
            if (result != 122) throw new Win32Exception(result, "无法确认程序的 Windows 应用身份。");
            if (Array.IndexOf(args, "--desktop-process") >= 0)
                throw new InvalidOperationException("未能从 Codex 应用身份切换到独立桌面程序。");
            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr attributes = Marshal.AllocHGlobal(bytes), policy = Marshal.AllocHGlobal(4);
            bool initialized = false;
            try
            {
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                // Windows SDK WinBase.h: DESKTOP_APP_POLICY = input | 18; ENABLE_PROCESS_TREE = 1.
                // OVERRIDE (4) would explicitly retain the package environment, so do not set it.
                Marshal.WriteInt32(policy, 1);
                if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020012), policy, new IntPtr(4), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var startup = new StartupInfoEx { attributes = attributes };
                startup.startup.cb = (uint)Marshal.SizeOf(typeof(StartupInfoEx));
                startup.startup.flags = 1; startup.startup.show = 1;
                ProcessInfo child;
                if (!CreateProcess(Application.ExecutablePath, new StringBuilder(Environment.CommandLine + " --desktop-process"),
                    IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, Program.Root, ref startup, out child))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动独立桌面窗口。");
                CloseHandle(child.thread); CloseHandle(child.process);
                return true;
            }
            finally
            {
                if (initialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(policy); Marshal.FreeHGlobal(attributes);
            }
        }
    }
}
