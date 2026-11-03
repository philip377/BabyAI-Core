using System.Runtime.InteropServices;
using BabyAI.Desktop;

internal static class NativeSelectionTests
{
    [StructLayout(LayoutKind.Sequential)] private struct Controls { public int Size; public uint Classes; }
    [StructLayout(LayoutKind.Sequential)] private struct Item
    {
        public uint Mask; public int Index, SubItem; public uint State, StateMask;
        public IntPtr Text; public int TextLength, Image; public IntPtr Param;
        public int Indent, GroupId; public uint Columns; public IntPtr ColumnArray, Formats; public int Group;
    }
    [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref Controls controls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    internal static void Run()
    {
        var controls = new Controls { Size = Marshal.SizeOf<Controls>(), Classes = 1 };
        if (!InitCommonControlsEx(ref controls)) throw new Exception("Cannot initialize synthetic ListView");
        var parent = CreateWindowExW(0, "STATIC", "UNIX hidden test fixture", 0,
            0, 0, 400, 300, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (parent == IntPtr.Zero) throw new Exception("Cannot create synthetic parent");
        try
        {
            // Hidden virtual table belonging to this test process. Never targets Ginger.
            var table = CreateWindowExW(0, "SysListView32", "", 0x40001001,
                0, 0, 300, 200, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (table == IntPtr.Zero) throw new Exception("Cannot create synthetic table");
            SendMessageW(table, 0x102F, new IntPtr(3), IntPtr.Zero); // fixture SETITEMCOUNT
            var process = (uint)Environment.ProcessId;
            void Check(string state, int? count, int? index)
            {
                var observed = GingerNativeSelection.Read(table, process);
                if (observed.Status != state || observed.SelectedCount != count ||
                    observed.SelectedIndex != index || observed.IdentityVerified)
                    throw new Exception("Wrong native observation: " + observed);
            }
            void Select(int index)
            {
                var item = new Item { State = 2, StateMask = 2 };
                var memory = Marshal.AllocHGlobal(Marshal.SizeOf<Item>());
                try
                {
                    Marshal.StructureToPtr(item, memory, false);
                    SendMessageW(table, 0x102B, new IntPtr(index), memory); // fixture SETITEMSTATE
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            Check("no_selection", 0, null);
            Select(1);
            Check("single_index_observed", 1, 1);
            Select(2);
            Check("multiple_selection", 2, null);
            if (GingerNativeSelection.Read(table, process + 1).Status != "unavailable" ||
                GingerNativeSelection.Read(parent, process).Status != "unavailable" ||
                GingerNativeSelection.Read(IntPtr.Zero, process).Status != "unavailable")
                throw new Exception("Wrong owner/class/handle accepted");
        }
        finally { DestroyWindow(parent); }
        Console.WriteLine("PASS: native empty/single/multiple selection, wrong owner/class/handle rejected");
    }
}
