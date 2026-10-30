# Guarded UI navigation. No guessed coordinates, text entry or direct server writes.
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'OpenPrices', 'ReadPrices', 'SelectFirstIncluded', 'OpenMatching', 'InspectMatching', 'OpenSelectedPrice', 'ReadSelectedProduct', 'ReadMatchingCandidates')][string]$Mode = 'Inspect',
    [switch]$ConfirmNavigation
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName Accessibility
Add-Type -ReferencedAssemblies @([Accessibility.IAccessible].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

public sealed class GingerMenuNode {
    public IAccessible Owner;
    public object Child;
    public string Name;
    public string Path;
    public int State; public int Role;
}

public sealed class GingerPriceRow {
    internal IAccessible Target;
    internal IAccessible RowTarget;
    public int Position;
    public string Number;
    public string Supplier;
    public string Date;
    public bool Included;
    public bool Selected;
    public bool SupplierFocused;
}
public sealed class GingerPriceSnapshot {
    public long TableHandle;
    public GingerPriceRow[] Rows;
    public bool IdentityVerified = false;
    public bool CanWrite = false;
    public GingerPriceRow[] Queue { get { return Array.FindAll(Rows, row => row.Included); } }
}
public sealed class GingerMatchingView {
    public string Status = "panels_not_verified";
    public long SearchRows = -1;
    public long ProductRows = -1;
    public bool CanMatch = false;
}
public sealed class GingerTabCandidate {
    public long Handle,Parent;
    public int X,Y,Width,Height;
}
public sealed class GingerCatalogueCandidate {
    public int ObservedPosition;
    public string Title, DescriptionText;
    public bool Selected;
    public bool IdentityVerified=false;
}
public sealed class GingerCandidateSnapshot {
    public GingerSelectedProduct Source;
    public long PanelHandle;
    public GingerCatalogueCandidate[] Candidates;
    public bool IdentityVerified=false, CanMatch=false;
}
public sealed class GingerSelectedProduct {
    public long TableHandle;
    public int ObservedRowPosition;
    public string DocumentNumber, Supplier, Title, PriceText, LinkedProductText;
    public bool IdentityVerified=false, CanMatch=false;
}
public sealed class GingerPriceDocument {
    public string Number,Supplier;
    public bool CanWrite=false;
}

public static class GingerMenuAccess {
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X,Y; public uint Data,Flags,Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count,Input[] input,int size);
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Rectangle rect);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor,out int scale);
    private static void RequireInputScale(IntPtr root) {
        int scale;
        if(GetSystemMetrics(80)!=1 || GetScaleFactorForMonitor(MonitorFromWindow(root,2),out scale)!=0 || scale!=100)
            throw new InvalidOperationException("Pointer navigation currently requires one display at 100 percent scale");
    }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr root, EnumProc callback, IntPtr unused);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr root);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out IntPtr result);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
    [DllImport("oleacc.dll")] private static extern int WindowFromAccessibleObject(IAccessible accessible, out IntPtr hwnd);
    [DllImport("oleacc.dll")] private static extern int AccessibleChildren(IAccessible parent, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex=2)] object[] children, out int obtained);

    public static string Normalize(string value) {
        return (value ?? "").Replace("&", "").Trim().ToLowerInvariant();
    }
    public static void RequireWindow(IntPtr root, uint process) {
        uint owner;
        if (root == IntPtr.Zero || process == 0 || GetWindowThreadProcessId(root, out owner) == 0 || owner != process)
            throw new InvalidOperationException("Ginger window changed");
        IntPtr popup = GetLastActivePopup(root);
        if (popup != root && popup != IntPtr.Zero && IsWindowVisible(popup))
            throw new InvalidOperationException("A modal or owned window requires attention");
    }
    private static List<IntPtr> Windows(IntPtr root, uint process) {
        RequireWindow(root, process);
        List<IntPtr> windows = new List<IntPtr>();
        windows.Add(root);
        bool limited = false;
        EnumProc callback = delegate(IntPtr hwnd, IntPtr unused) {
            if (windows.Count >= 600) { limited = true; return false; }
            windows.Add(hwnd); return true;
        };
        EnumChildWindows(root, callback, IntPtr.Zero);
        if (limited) throw new InvalidOperationException("Window enumeration truncated");
        return windows;
    }
    private static IAccessible Client(IntPtr hwnd) {
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        IAccessible value;
        return AccessibleObjectFromWindow(hwnd, unchecked((uint)-4), ref iid, out value) >= 0 ? value : null;
    }
    private static void Walk(IAccessible owner, object child, string ancestors, int depth, List<GingerMenuNode> nodes) {
        if (depth > 20 || nodes.Count >= 2000) throw new InvalidOperationException("Accessibility enumeration truncated");
        string name = owner.get_accName(child) ?? "";
        int role = Convert.ToInt32(owner.get_accRole(child));
        int state = Convert.ToInt32(owner.get_accState(child));
        string path = ancestors;
        if (role == 12) path = ancestors.Length == 0 ? Normalize(name) : ancestors + "/" + Normalize(name);
        nodes.Add(new GingerMenuNode { Owner=owner, Child=child, Name=name, Path=path, State=state, Role=role });
        if (!(child is int) || (int)child != 0) return;
        int count = owner.accChildCount;
        if (count < 0 || count > 2000 - nodes.Count) throw new InvalidOperationException("Invalid child count");
        if (count == 0) return;
        object[] children = new object[count]; int obtained;
        if (AccessibleChildren(owner, 0, count, children, out obtained) < 0 || obtained != count)
            throw new InvalidOperationException("Incomplete accessibility read");
        foreach (object item in children) {
            IAccessible nested = item as IAccessible;
            if (nested != null) Walk(nested, 0, path, depth + 1, nodes);
            else if (item is int && (int)item > 0) Walk(owner, item, path, depth + 1, nodes);
            else throw new InvalidOperationException("Unknown accessibility child");
        }
    }
    public static List<GingerMenuNode> Menu(IntPtr root, uint process) {
        List<IAccessible> roots = new List<IAccessible>();
        foreach (IntPtr hwnd in Windows(root, process)) {
            IAccessible value = Client(hwnd);
            if (value != null && Normalize(value.get_accName(0)) == "menustrip1") roots.Add(value);
        }
        if (roots.Count != 1) throw new InvalidOperationException("Expected one menuStrip1");
        List<GingerMenuNode> nodes = new List<GingerMenuNode>();
        Walk(roots[0], 0, "", 0, nodes);
        RequireWindow(root, process);
        return nodes;
    }
    public static void Invoke(IntPtr root, uint process, string path) {
        if (path != "журналы" && path != "журналы/текущие прайс-листы")
            throw new InvalidOperationException("Menu action is not allowed");
        InvokeResolved(root,process,path);
    }
    private static void InvokeResolved(IntPtr root, uint process, string path) {
        List<GingerMenuNode> matches = Menu(root, process).FindAll(n => n.Role == 12 && n.Path == path && Normalize(n.Name) == path.Substring(path.LastIndexOf('/') + 1));
        if (matches.Count != 1) throw new InvalidOperationException("Menu item is missing or ambiguous");
        GingerMenuNode item = matches[0];
        IntPtr hwnd; uint owner;
        if (WindowFromAccessibleObject(item.Owner, out hwnd) < 0 || GetWindowThreadProcessId(hwnd, out owner) == 0 || owner != process)
            throw new InvalidOperationException("Menu owner changed");
        RequireWindow(root, process);
        int state = Convert.ToInt32(item.Owner.get_accState(item.Child));
        // Unavailable, invisible or offscreen controls cannot be invoked.
        if ((state & (1 | 0x8000 | 0x10000)) != 0 || Normalize(item.Owner.get_accName(item.Child)) != Normalize(item.Name))
            throw new InvalidOperationException("Menu item changed or is unavailable");
        if (String.IsNullOrWhiteSpace(item.Owner.get_accDefaultAction(item.Child)))
            throw new InvalidOperationException("Menu exposes no default action");
        item.Owner.accDoDefaultAction(item.Child);
    }
    public static List<string[]> Headers(IntPtr root, uint process) {
        List<string[]> result = new List<string[]>();
        foreach (IntPtr hwnd in Windows(root, process)) {
            StringBuilder name = new StringBuilder(256);
            GetClassNameW(hwnd, name, name.Capacity);
            if (!IsWindowVisible(hwnd)) continue;
            IAccessible value = Client(hwnd);
            if (value == null) continue;
            if (Convert.ToInt32(value.get_accRole(0)) == 24) {
                string[] columns = TableHeaders(value);
                if (columns.Length > 0) result.Add(columns);
                continue;
            }
            if (!name.ToString().Contains("SysHeader32")) continue;
            List<GingerMenuNode> nodes = new List<GingerMenuNode>();
            Walk(value, 0, "", 0, nodes);
            result.Add(nodes.ConvertAll(n => n.Name).ToArray());
        }
        return result;
    }
    // DataGridView exposes an accessible header row, not a SysHeader32 HWND.
    // Inspect only the first four table children and at most 64 header cells.
    // Never recurse through data rows or read cell values.
    private static object[] FirstChildren(IAccessible parent, int limit) {
        int total = parent.accChildCount;
        if (total < 0) throw new InvalidOperationException("Invalid child count");
        int count = Math.Min(total, limit);
        if (count == 0) return new object[0];
        object[] children = new object[count]; int obtained;
        if (AccessibleChildren(parent, 0, count, children, out obtained) < 0 || obtained != count)
            throw new InvalidOperationException("Incomplete header read");
        return children;
    }
    public static string[] TableHeaders(IAccessible table) {
        foreach (object child in FirstChildren(table, 4)) {
            IAccessible row = child as IAccessible;
            if (row == null || Convert.ToInt32(row.get_accRole(0)) != 28) continue;
            if (row.accChildCount > 64) continue;
            List<string> names = new List<string>();
            bool headerOnly = true;
            foreach (object cell in FirstChildren(row, 64)) {
                IAccessible nested = cell as IAccessible;
                IAccessible owner = nested ?? row;
                object id = nested != null ? (object)0 : cell;
                int role = Convert.ToInt32(owner.get_accRole(id));
                if (role == 26) { names.Add(""); continue; } // Preserve the corner's column offset.
                if (role != 25) { headerOnly = false; break; }
                names.Add(owner.get_accName(id) ?? "");
            }
            if (headerOnly && names.Count > 0) return names.ToArray();
        }
        return new string[0];
    }
    public static bool PricesVisible(IntPtr root, uint process) {
        int matches = 0;
        foreach (string[] header in Headers(root, process)) {
            bool supplier = false, included = false, number = false, date = false;
            foreach (string name in header) {
                supplier |= Normalize(name) == "поставщик";
                included |= Normalize(name) == "входит в сводные" || Normalize(name) == "вкл в сводный";
                number |= Normalize(name) == "номер";
                date |= Normalize(name) == "дата";
            }
            if (supplier && included && number && date) matches++;
        }
        RequireWindow(root, process);
        return matches == 1;
    }
    private static int Column(string[] headers, params string[] accepted) {
        int index = -1;
        for (int i=0; i<headers.Length; i++) {
            foreach (string label in accepted) {
                if (Normalize(headers[i]) != label) continue;
                if (index >= 0) throw new InvalidOperationException("Duplicate column");
                index = i;
            }
        }
        return index;
    }
    public static bool CheckedValue(string value, int state) {
        if ((state & 32) != 0 || (value != "True" && value != "False"))
            throw new InvalidOperationException("Unknown checkbox state");
        bool included = value == "True";
        if (included != ((state & 16) != 0))
            throw new InvalidOperationException("Checkbox value and state disagree");
        return included;
    }
    private static string CellValue(IAccessible cell) {
        if (cell == null || Convert.ToInt32(cell.get_accRole(0)) != 29)
            throw new InvalidOperationException("Expected a data cell");
        string value = cell.get_accValue(0);
        if (String.IsNullOrWhiteSpace(value) || value == "(null)" || value.Length > 512)
            throw new InvalidOperationException("Missing or oversized cell value");
        return value;
    }
    private static GingerPriceSnapshot ReadPricesOnce(IntPtr root, uint process) {
        GingerPriceSnapshot result = null;
        foreach (IntPtr hwnd in Windows(root, process)) {
            if (!IsWindowVisible(hwnd)) continue;
            IAccessible table = Client(hwnd);
            if (table == null || Convert.ToInt32(table.get_accRole(0)) != 24) continue;
            string[] headers = TableHeaders(table);
            int number=Column(headers,"номер"), supplier=Column(headers,"поставщик");
            int date=Column(headers,"дата"), included=Column(headers,"вкл в сводный","входит в сводные");
            if (number<0 || supplier<0 || date<0 || included<0) continue;
            if (result != null) throw new InvalidOperationException("Ambiguous price tables");
            int count=table.accChildCount;
            if (count<1 || count>503) throw new InvalidOperationException("Price table exceeds read limit");
            List<GingerPriceRow> rows=new List<GingerPriceRow>();
            HashSet<string> numbers=new HashSet<string>(StringComparer.Ordinal);
            bool seenHeader=false;
            foreach(object item in FirstChildren(table,503)) {
                IAccessible row=item as IAccessible;
                if(row==null) throw new InvalidOperationException("Unsupported table child");
                int role=Convert.ToInt32(row.get_accRole(0));
                if(role==3) continue; // Scroll bars are not data rows.
                if(role!=28 || row.accChildCount!=headers.Length)
                    throw new InvalidOperationException("Unexpected price row structure");
                object[] cells=FirstChildren(row,64);
                IAccessible n=cells[number] as IAccessible;
                if(n==null) throw new InvalidOperationException("Unreadable number cell");
                if(Convert.ToInt32(n.get_accRole(0))==25) {
                    if(seenHeader || rows.Count!=0) throw new InvalidOperationException("Unexpected header position");
                    seenHeader=true; continue;
                }
                if(!seenHeader || rows.Count>=500) throw new InvalidOperationException("Missing header or row limit exceeded");
                IAccessible s=cells[supplier] as IAccessible, d=cells[date] as IAccessible, c=cells[included] as IAccessible;
                if(s==null || d==null || c==null) throw new InvalidOperationException("Unreadable price cells");
                string num=CellValue(n);
                if(!numbers.Add(num)) throw new InvalidOperationException("Duplicate price number");
                bool selected=(Convert.ToInt32(row.get_accState(0)) & 2)!=0;
                foreach(object cell in cells) {
                    IAccessible accessible=cell as IAccessible;
                    if(accessible==null) throw new InvalidOperationException("Unreadable selection state");
                    selected |= (Convert.ToInt32(accessible.get_accState(0)) & 2)!=0;
                }
                rows.Add(new GingerPriceRow { Target=s, RowTarget=row, Position=rows.Count, Number=num, Supplier=CellValue(s), Date=CellValue(d),
                    Included=CheckedValue(CellValue(c),Convert.ToInt32(c.get_accState(0))),
                    Selected=selected, SupplierFocused=(Convert.ToInt32(s.get_accState(0)) & 4)!=0 });
            }
            if(!seenHeader || count!=table.accChildCount) throw new InvalidOperationException("Table changed during read");
            result=new GingerPriceSnapshot { TableHandle=hwnd.ToInt64(), Rows=rows.ToArray() };
        }
        RequireWindow(root,process);
        if(result==null) throw new InvalidOperationException("Price table not found");
        return result;
    }
    public static GingerPriceSnapshot ReadPrices(IntPtr root, uint process) {
        GingerPriceSnapshot a=ReadPricesOnce(root,process), b=ReadPricesOnce(root,process);
        if(a.TableHandle!=b.TableHandle || a.Rows.Length!=b.Rows.Length)
            throw new InvalidOperationException("Price snapshot changed");
        for(int i=0;i<a.Rows.Length;i++) {
            GingerPriceRow x=a.Rows[i], y=b.Rows[i];
            if(x.Number!=y.Number || x.Supplier!=y.Supplier || x.Date!=y.Date || x.Included!=y.Included || x.Selected!=y.Selected || x.SupplierFocused!=y.SupplierFocused)
                throw new InvalidOperationException("Price snapshot changed");
        }
        return b; // Observation only; never authorizes a later selection or write.
    }
    private static void SamePrices(GingerPriceSnapshot expected, GingerPriceSnapshot actual, bool selection) {
        if(expected==null || expected.TableHandle!=actual.TableHandle || expected.Rows.Length!=actual.Rows.Length)
            throw new InvalidOperationException("Price table changed; selection blocked");
        for(int i=0;i<expected.Rows.Length;i++) {
            GingerPriceRow x=expected.Rows[i], y=actual.Rows[i];
            if(x.Number!=y.Number || x.Supplier!=y.Supplier || x.Date!=y.Date || x.Included!=y.Included ||
               (selection && (x.Selected!=y.Selected || x.SupplierFocused!=y.SupplierFocused)))
                throw new InvalidOperationException("Price row changed; selection blocked");
        }
    }
    public static GingerPriceSnapshot SelectPrice(IntPtr root, uint process, GingerPriceSnapshot expected,
        string number, bool navigationConfirmed) {
        if(!navigationConfirmed) throw new InvalidOperationException("Navigation confirmation required");
        GingerPriceSnapshot fresh=ReadPrices(root,process);
        SamePrices(expected,fresh,true);
        GingerPriceRow[] matches=Array.FindAll(fresh.Rows,r=>r.Number==number && r.Included);
        if(matches.Length!=1) throw new InvalidOperationException("Expected one included price");
        GingerPriceRow target=matches[0];
        // Moving focus could commit an active edit: do not select while an editor is visible.
        foreach(IntPtr child in Windows(new IntPtr(fresh.TableHandle),process)) {
            StringBuilder cls=new StringBuilder(256); GetClassNameW(child,cls,cls.Capacity);
            if(IsWindowVisible(child) && cls.ToString().IndexOf("EDIT",StringComparison.OrdinalIgnoreCase)>=0)
                throw new InvalidOperationException("Finish the active table edit before navigation");
        }
        GingerPriceSnapshot last=ReadPricesOnce(root,process);
        SamePrices(fresh,last,true);
        target=Array.Find(last.Rows,r=>r.Number==number && r.Included);
        int state=Convert.ToInt32(target.Target.get_accState(0));
        if((state & (1|0x8000|0x10000))!=0 || (state & 0x200000)==0)
            throw new InvalidOperationException("Price row is not visibly selectable");
        RequireWindow(root,process);
        target.Target.accSelect(3,0); // TAKEFOCUS | TAKESELECTION, never invoke a cell action.
        GingerPriceSnapshot after=ReadPrices(root,process);
        SamePrices(fresh,after,false);
        GingerPriceRow[] selected=Array.FindAll(after.Rows,r=>r.Selected);
        if(selected.Length!=1 || selected[0].Number!=number || !selected[0].SupplierFocused)
            throw new InvalidOperationException("Selection result not verified; do not retry automatically");
        return after; // Still grants no catalogue-write permission or matching action.
    }
    private static void RequireSelectedPrice(GingerPriceSnapshot expected, GingerPriceSnapshot current, string number, bool focus) {
        SamePrices(expected,current,false);
        GingerPriceRow[] rows=Array.FindAll(current.Rows,r=>r.Selected);
        if(rows.Length!=1 || rows[0].Number!=number || !rows[0].Included || (focus && !rows[0].SupplierFocused))
            throw new InvalidOperationException("Selected supplier price not verified");
    }
    private static IntPtr PricePoint(IntPtr root,uint process,GingerPriceSnapshot snapshot,GingerPriceRow row) {
        RequireWindow(root,process);
        IntPtr table=new IntPtr(snapshot.TableHandle); uint owner;
        if(GetWindowThreadProcessId(table,out owner)==0 || owner!=process || !IsWindowVisible(table))
            throw new InvalidOperationException("Price table owner changed");
        string[] headers=TableHeaders(Client(table));
        int number=Column(headers,"номер"), supplier=Column(headers,"поставщик"), date=Column(headers,"дата"), included=Column(headers,"вкл в сводный","входит в сводные");
        if(number<0 || supplier<0 || date<0 || included<0 || row.RowTarget.accChildCount!=headers.Length)
            throw new InvalidOperationException("Price columns changed");
        object[] cells=FirstChildren(row.RowTarget,64);
        IAccessible n=cells[number] as IAccessible, s=cells[supplier] as IAccessible, d=cells[date] as IAccessible, c=cells[included] as IAccessible;
        if(n==null || s==null || d==null || c==null || CellValue(n)!=row.Number || CellValue(s)!=row.Supplier || CellValue(d)!=row.Date ||
           !CheckedValue(CellValue(c),Convert.ToInt32(c.get_accState(0))))
            throw new InvalidOperationException("Price identity or inclusion changed before click");
        IAccessible cell=row.Target;
        if(CellValue(cell)!=row.Supplier || (Convert.ToInt32(cell.get_accState(0)) & (1|0x8000|0x10000))!=0)
            throw new InvalidOperationException("Supplier cell changed or hidden");
        int x,y,w,h; cell.accLocation(out x,out y,out w,out h,0);
        if(w<4 || h<4) throw new InvalidOperationException("Supplier cell geometry unavailable");
        Point point=new Point { X=checked(x+w/2),Y=checked(y+h/2) };
        IAccessible hit=Client(table);
        bool verified=false;
        for(int depth=0;depth<8;depth++) {
            object child=hit.accHitTest(point.X,point.Y);
            IAccessible nested=child as IAccessible;
            if(nested!=null) { hit=nested; }
            else if(!(child is int) || (int)child!=0) break;
            if(Convert.ToInt32(hit.get_accRole(0))==29) {
                int hx,hy,hw,hh; hit.accLocation(out hx,out hy,out hw,out hh,0);
                verified=CellValue(hit)==row.Supplier && hit.get_accName(0)==cell.get_accName(0) && hx==x && hy==y && hw==w && hh==h;
                break;
            }
        }
        if(!verified || !ScreenToClient(table,ref point) || point.X<0 || point.Y<0 || point.X>32767 || point.Y>32767)
            throw new InvalidOperationException("Supplier cell hit-test failed");
        foreach(IntPtr child in Windows(table,process)) {
            StringBuilder cls=new StringBuilder(256); GetClassNameW(child,cls,cls.Capacity);
            if(IsWindowVisible(child) && cls.ToString().IndexOf("EDIT",StringComparison.OrdinalIgnoreCase)>=0)
                throw new InvalidOperationException("Active editor blocks price opening");
        }
        return new IntPtr((point.Y<<16)|point.X);
    }
    public static void OpenSelectedPrice(IntPtr root,uint process,GingerPriceSnapshot expected,string number,bool navigationConfirmed) {
        if(!navigationConfirmed) throw new InvalidOperationException("Navigation confirmation required");
        GingerPriceSnapshot fresh=ReadPrices(root,process);
        RequireSelectedPrice(expected,fresh,number,false);
        GingerPriceRow row=Array.Find(fresh.Rows,r=>r.Number==number);
        SetForegroundWindow(root);
        if(GetForegroundWindow()!=root) throw new InvalidOperationException("Ginger foreground could not be verified");
        RequireInputScale(root);
        IntPtr table=new IntPtr(fresh.TableHandle), packed=PricePoint(root,process,fresh,row);
        Point point=new Point { X=(int)(packed.ToInt64() & 65535),Y=(int)((packed.ToInt64()>>16)&65535) };
        if(!ClientToScreen(table,ref point) || WindowFromPoint(point)!=table)
            throw new InvalidOperationException("Supplier cell is covered by another window");
        int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
        if(width<2 || height<2 || point.X<left || point.Y<top || point.X>=left+width || point.Y>=top+height)
            throw new InvalidOperationException("Supplier cell is outside the desktop");
        int x=(int)(((long)(point.X-left)*65536+32768)/width),y=(int)(((long)(point.Y-top)*65536+32768)/height);
        Input[] input=new Input[4];
        for(int i=0;i<input.Length;i++) input[i]=new Input { Type=0,Mouse=new MouseInput { X=x,Y=y,Flags=(uint)(0xC001|(i%2==0?2:4)) } };
        RequireWindow(root,process);
        if(GetForegroundWindow()!=root || WindowFromPoint(point)!=table || PricePoint(root,process,fresh,row)!=packed)
            throw new InvalidOperationException("Double-click target changed");
        if(SendInput(4,input,Marshal.SizeOf(typeof(Input)))!=4)
            throw new InvalidOperationException("Incomplete price double click; outcome unknown, no retry");
        // Dispatch alone does not establish which document opened. Read the resulting UI.
    }
    public static void ActivateForRead(IntPtr root,uint process) {
        RequireWindow(root,process); SetForegroundWindow(root);
        if(GetForegroundWindow()!=root) throw new InvalidOperationException("Ginger must be foreground");
    }
    public static void RequireForeground(IntPtr root,uint process) {
        RequireWindow(root,process);
        if(GetForegroundWindow()!=root) throw new InvalidOperationException("Foreground changed during observation");
    }
    public static GingerPriceDocument ReadPriceDocument(IntPtr root,uint process) {
        string number=null,supplier=null;
        int tables=0;
        foreach(string[] headers in Headers(root,process)) {
            bool records=Column(headers,"название у поставщика")>=0 && Column(headers,"цена в прайсе")>=0 && Column(headers,"новый")>=0;
            bool matching=Column(headers,"название в прайсе")>=0 && Column(headers,"цена")>=0;
            if((records || matching) && Column(headers,"товар")>=0) tables++;
        }
        if(tables!=1) throw new InvalidOperationException("Supplier price document table not verified");
        foreach(IntPtr hwnd in Windows(root,process)) {
            if(!IsWindowVisible(hwnd)) continue;
            IAccessible value=Client(hwnd);
            if(value==null || Convert.ToInt32(value.get_accRole(0))!=42) continue;
            string name=Normalize(value.get_accName(0));
            if(name!="номер" && name!="поставщик") continue;
            string text=value.get_accValue(0);
            if(String.IsNullOrWhiteSpace(text) || text=="(null)" || text.Length>512) throw new InvalidOperationException("Document identity field unavailable");
            if(name=="номер") { if(number!=null) throw new InvalidOperationException("Ambiguous document number"); number=text; }
            if(name=="поставщик") { if(supplier!=null) throw new InvalidOperationException("Ambiguous document supplier"); supplier=text; }
        }
        RequireWindow(root,process);
        if(number==null || supplier==null) throw new InvalidOperationException("Document identity unavailable");
        return new GingerPriceDocument { Number=number,Supplier=supplier };
    }
    public static void OpenDocumentMatching(IntPtr root,uint process,GingerPriceDocument expected,bool confirmed) {
        if(!confirmed || expected==null) throw new InvalidOperationException("Document navigation confirmation required");
        GingerPriceDocument current=ReadPriceDocument(root,process);
        if(current.Number!=expected.Number || current.Supplier!=expected.Supplier) throw new InvalidOperationException("Price document changed");
        if(InspectMatching(root,process).Status!="panels_not_verified") return; // Already open: never toggle closed.
        InvokeResolved(root,process,"дополнительно");
        current=ReadPriceDocument(root,process);
        if(current.Number!=expected.Number || current.Supplier!=expected.Supplier) throw new InvalidOperationException("Price document changed while opening menu");
        InvokeResolved(root,process,"дополнительно/сопоставление снизу");
    }
    public static GingerTabCandidate[] TabCandidates(IntPtr root,uint process) {
        List<GingerTabCandidate> candidates=new List<GingerTabCandidate>(), result=new List<GingerTabCandidate>();
        foreach(IntPtr hwnd in Windows(root,process)) {
            if(!IsWindowVisible(hwnd)) continue;
            Rectangle r; StringBuilder cls=new StringBuilder(256); GetClassNameW(hwnd,cls,cls.Capacity);
            if(!cls.ToString().StartsWith("WindowsForms10.Window.") || !GetWindowRect(hwnd,out r)) continue;
            if(r.Bottom-r.Top<16 || r.Bottom-r.Top>40 || r.Right-r.Left<150) continue;
            candidates.Add(new GingerTabCandidate { Handle=hwnd.ToInt64(),Parent=GetParent(hwnd).ToInt64(),X=r.Left,Y=r.Top,Width=r.Right-r.Left,Height=r.Bottom-r.Top });
        }
        foreach(GingerTabCandidate c in candidates) {
            List<GingerTabCandidate> group=candidates.FindAll(x=>x.Parent==c.Parent && x.Y==c.Y && x.Height==c.Height && Math.Abs(x.Width-c.Width)<=4);
            if(group.Count!=3) continue;
            group.Sort((a,b)=>a.X.CompareTo(b.X));
            if(group[1].X-group[0].X-group[0].Width<0 || group[1].X-group[0].X-group[0].Width>4 ||
               group[2].X-group[1].X-group[1].Width<0 || group[2].X-group[1].X-group[1].Width>4) continue;
            if(!result.Exists(x=>x.Handle==c.Handle)) result.Add(c);
        }
        return result.ToArray();
    }
    public static void ClickVerifiedTab(IntPtr root,uint process,GingerTabCandidate expected,bool confirmed) {
        if(!confirmed) throw new InvalidOperationException("Navigation confirmation required");
        RequireWindow(root,process);
        RequireInputScale(root);
        GingerTabCandidate[] matches=Array.FindAll(TabCandidates(root,process),c=>c.Handle==expected.Handle && c.Parent==expected.Parent &&
            c.X==expected.X && c.Y==expected.Y && c.Width==expected.Width && c.Height==expected.Height);
        if(matches.Length!=1 || GetForegroundWindow()!=root) throw new InvalidOperationException("Tab target changed");
        Point point=new Point { X=expected.X+expected.Width/2,Y=expected.Y+expected.Height/2 };
        if(WindowFromPoint(point)!=new IntPtr(expected.Handle)) throw new InvalidOperationException("Tab is covered");
        int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
        if(width<2 || height<2 || point.X<left || point.Y<top || point.X>=left+width || point.Y>=top+height) throw new InvalidOperationException("Tab outside desktop");
        int x=(int)(((long)(point.X-left)*65536+32768)/width),y=(int)(((long)(point.Y-top)*65536+32768)/height);
        Input[] input=new Input[2];
        input[0]=new Input { Mouse=new MouseInput { X=x,Y=y,Flags=0xC003 } };
        input[1]=new Input { Mouse=new MouseInput { X=x,Y=y,Flags=0xC005 } };
        if(SendInput(2,input,Marshal.SizeOf(typeof(Input)))!=2) throw new InvalidOperationException("Tab click outcome unknown; no retry");
    }
    // These are UI observations only. Position and displayed names are never product IDs.
    private static GingerSelectedProduct ReadSelectedProductOnce(IntPtr root,uint process) {
        GingerPriceDocument document=ReadPriceDocument(root,process);
        GingerSelectedProduct result=null;
        int tables=0;
        foreach(IntPtr hwnd in Windows(root,process)) {
            if(!IsWindowVisible(hwnd)) continue;
            IAccessible table=Client(hwnd);
            if(table==null || Convert.ToInt32(table.get_accRole(0))!=24) continue;
            string[] headers=TableHeaders(table);
            int title=Column(headers,"название в прайсе","название у поставщика");
            int price=Column(headers,"цена","цена в прайсе"), linked=Column(headers,"товар");
            if(title<0 || price<0 || linked<0) continue;
            if(++tables!=1) throw new InvalidOperationException("Ambiguous product tables");
            int count=table.accChildCount, position=0;
            if(count<1 || count>503) throw new InvalidOperationException("Product read limit exceeded");
            bool headerSeen=false;
            foreach(object item in FirstChildren(table,503)) {
                IAccessible row=item as IAccessible;
                if(row==null) throw new InvalidOperationException("Unsupported product row");
                int role=Convert.ToInt32(row.get_accRole(0));
                if(role==3) continue;
                if(role!=28 || row.accChildCount!=headers.Length)
                    throw new InvalidOperationException("Unexpected product row structure");
                object[] cells=FirstChildren(row,64);
                IAccessible nameCell=cells[title] as IAccessible;
                if(nameCell==null) throw new InvalidOperationException("Missing product cell");
                if(Convert.ToInt32(nameCell.get_accRole(0))==25) {
                    if(headerSeen || position!=0) throw new InvalidOperationException("Unexpected product header");
                    headerSeen=true; continue;
                }
                if(!headerSeen) throw new InvalidOperationException("Product header missing");
                position++;
                bool selected=(Convert.ToInt32(row.get_accState(0)) & 2)!=0;
                foreach(object cell in cells) {
                    IAccessible accessible=cell as IAccessible;
                    if(accessible==null) throw new InvalidOperationException("Unreadable product cell");
                    selected |= (Convert.ToInt32(accessible.get_accState(0)) & 2)!=0;
                }
                if(!selected) continue;
                if(result!=null) throw new InvalidOperationException("Multiple selected products");
                result=new GingerSelectedProduct {
                    TableHandle=hwnd.ToInt64(), ObservedRowPosition=position,
                    DocumentNumber=document.Number,Supplier=document.Supplier,
                    Title=CellValue(nameCell),PriceText=CellValue(cells[price] as IAccessible),
                    LinkedProductText=CellValue(cells[linked] as IAccessible)
                };
            }
            if(table.accChildCount!=count) throw new InvalidOperationException("Product rows changed");
        }
        GingerPriceDocument after=ReadPriceDocument(root,process);
        if(after.Number!=document.Number || after.Supplier!=document.Supplier)
            throw new InvalidOperationException("Supplier document changed during read");
        if(result==null) throw new InvalidOperationException("Exactly one selected product required");
        RequireWindow(root,process);
        return result;
    }
    public static GingerSelectedProduct ReadSelectedProduct(IntPtr root,uint process) {
        GingerSelectedProduct first=ReadSelectedProductOnce(root,process);
        GingerSelectedProduct second=ReadSelectedProductOnce(root,process);
        RequireSameSelectedProduct(first,second);
        return second;
    }
    public static void RequireSameSelectedProduct(GingerSelectedProduct first,GingerSelectedProduct second) {
        if(first==null || second==null || first.TableHandle!=second.TableHandle || first.ObservedRowPosition!=second.ObservedRowPosition ||
           first.DocumentNumber!=second.DocumentNumber || first.Supplier!=second.Supplier ||
           first.Title!=second.Title || first.PriceText!=second.PriceText || first.LinkedProductText!=second.LinkedProductText)
            throw new InvalidOperationException("Selected product changed between observations");
    }
    private static long ListCount(IntPtr hwnd,uint process) {
        uint owner; IntPtr count;
        if(GetWindowThreadProcessId(hwnd,out owner)==0 || owner!=process ||
           SendMessageTimeoutW(hwnd,0x1004,IntPtr.Zero,IntPtr.Zero,2,100,out count)==IntPtr.Zero || count.ToInt64()<0)
            throw new InvalidOperationException("Candidate count unavailable");
        return count.ToInt64();
    }
    private static GingerCandidateSnapshot ReadCandidatesOnce(IntPtr root,uint process) {
        GingerSelectedProduct source=ReadSelectedProduct(root,process);
        GingerCandidateSnapshot result=null;
        foreach(IntPtr hwnd in Windows(root,process)) {
            StringBuilder cls=new StringBuilder(256); GetClassNameW(hwnd,cls,cls.Capacity);
            if(!IsWindowVisible(hwnd) || !cls.ToString().Contains("SysListView32")) continue;
            bool products=false;
            foreach(string[] columns in Headers(hwnd,process))
                products |= Column(columns,"товар")>=0 && Column(columns,"цена")>=0;
            if(!products) continue;
            if(result!=null) throw new InvalidOperationException("Ambiguous candidate panels");
            long count=ListCount(hwnd,process);
            if(count>256) throw new InvalidOperationException("Candidate read limit exceeded");
            IAccessible list=Client(hwnd);
            if(list==null || Convert.ToInt32(list.get_accRole(0))!=33 || list.accChildCount>260)
                throw new InvalidOperationException("Unsupported candidate list");
            int childCount=list.accChildCount;
            List<GingerCatalogueCandidate> candidates=new List<GingerCatalogueCandidate>();
            foreach(object child in FirstChildren(list,260)) {
                IAccessible nested=child as IAccessible;
                IAccessible owner=nested??list;
                object id=nested!=null?(object)0:child;
                if(nested==null && (!(id is int) || (int)id<=0))
                    throw new InvalidOperationException("Invalid candidate child identifier");
                int role=Convert.ToInt32(owner.get_accRole(id));
                if(role==9 && nested!=null) continue; // Native header window, not a product.
                if(role!=34) throw new InvalidOperationException("Unexpected candidate child role");
                string title=owner.get_accName(id), description=owner.get_accDescription(id)??"";
                if(String.IsNullOrWhiteSpace(title) || title=="(null)" || title.Length>512 || description.Length>2048)
                    throw new InvalidOperationException("Missing or oversized candidate text");
                candidates.Add(new GingerCatalogueCandidate {
                    ObservedPosition=candidates.Count+1,Title=title,DescriptionText=description,
                    Selected=(Convert.ToInt32(owner.get_accState(id)) & 2)!=0
                });
            }
            if(candidates.Count!=count || ListCount(hwnd,process)!=count || list.accChildCount!=childCount)
                throw new InvalidOperationException("Candidate list changed or incomplete");
            result=new GingerCandidateSnapshot {Source=source,PanelHandle=hwnd.ToInt64(),Candidates=candidates.ToArray()};
        }
        if(result==null) throw new InvalidOperationException("Candidate panel missing");
        RequireSameSelectedProduct(source,ReadSelectedProduct(root,process));
        RequireWindow(root,process);
        return result;
    }
    public static void RequireSameCandidates(GingerCandidateSnapshot first,GingerCandidateSnapshot second) {
        if(first==null || second==null || first.Candidates==null || second.Candidates==null ||
           first.PanelHandle!=second.PanelHandle || first.Candidates.Length!=second.Candidates.Length)
            throw new InvalidOperationException("Candidate panel changed");
        RequireSameSelectedProduct(first.Source,second.Source);
        for(int i=0;i<first.Candidates.Length;i++) {
            GingerCatalogueCandidate a=first.Candidates[i], b=second.Candidates[i];
            if(a==null || b==null || a.ObservedPosition!=b.ObservedPosition || a.Title!=b.Title ||
               a.DescriptionText!=b.DescriptionText || a.Selected!=b.Selected)
                throw new InvalidOperationException("Candidate rows changed between observations");
        }
    }
    public static GingerCandidateSnapshot ReadMatchingCandidates(IntPtr root,uint process) {
        GingerCandidateSnapshot first=ReadCandidatesOnce(root,process), second=ReadCandidatesOnce(root,process);
        RequireSameCandidates(first,second);
        return second;
    }
    public static GingerMatchingView InspectMatching(IntPtr root,uint process) {
        GingerMatchingView result=new GingerMatchingView();
        int search=0, products=0;
        foreach(IntPtr hwnd in Windows(root,process)) {
            StringBuilder cls=new StringBuilder(256); GetClassNameW(hwnd,cls,cls.Capacity);
            if(!IsWindowVisible(hwnd) || !cls.ToString().Contains("SysListView32")) continue;
            bool isSearch=false,isProduct=false;
            foreach(string[] columns in Headers(hwnd,process)) {
                isSearch |= Column(columns,"слово")>=0 && Column(columns,"поиск")>=0;
                isProduct |= Column(columns,"товар")>=0 && Column(columns,"цена")>=0;
            }
            if(!isSearch && !isProduct) continue;
            if(isSearch && isProduct) throw new InvalidOperationException("Ambiguous matching panel");
            uint owner; IntPtr count;
            if(GetWindowThreadProcessId(hwnd,out owner)==0 || owner!=process ||
               SendMessageTimeoutW(hwnd,0x1004,IntPtr.Zero,IntPtr.Zero,2,100,out count)==IntPtr.Zero || count.ToInt64()<0)
                throw new InvalidOperationException("Matching row count unavailable");
            if(isSearch) { search++; result.SearchRows=count.ToInt64(); }
            if(isProduct) { products++; result.ProductRows=count.ToInt64(); }
        }
        RequireWindow(root,process);
        if(search>1 || products>1) throw new InvalidOperationException("Duplicate matching panels");
        if(search==1 && products==1)
            result.Status=result.SearchRows==0 && result.ProductRows==0 ? "panels_visible_empty" : "rows_observed_identity_unverified";
        return result;
    }
}
'@

$processes = @(Get-Process -Name Ginger4 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
if ($processes.Count -ne 1) { throw 'Exactly one visible Ginger4 process is required' }
$target = $processes[0]
if ([IO.Path]::GetFileName($target.Path) -ine 'Ginger4.exe') { throw 'Unexpected process executable' }
$window = $target.MainWindowHandle
$processId = [uint32]$target.Id
$actions = [System.Collections.Generic.List[string]]::new()
$prices = $null
$selectedProduct = $null
$candidateSnapshot = $null
if ($Mode -eq 'ReadMatchingCandidates') { $candidateSnapshot = [GingerMenuAccess]::ReadMatchingCandidates($window, $processId) }
if ($Mode -eq 'ReadSelectedProduct') { $selectedProduct = [GingerMenuAccess]::ReadSelectedProduct($window, $processId) }
$matchingStatus = 'not_requested'
$matchingView = $null
if ($Mode -eq 'InspectMatching') { $matchingView = [GingerMenuAccess]::InspectMatching($window, $processId); $matchingStatus = $matchingView.Status }
if ($Mode -eq 'ReadPrices') { $prices = [GingerMenuAccess]::ReadPrices($window, $processId) }
if ($Mode -eq 'SelectFirstIncluded') {
    if (-not $ConfirmNavigation) { throw 'Navigation confirmation required' }
    $before = [GingerMenuAccess]::ReadPrices($window, $processId)
    if ($before.Queue.Length -eq 0) { throw 'No included prices; no selection performed' }
    $prices = [GingerMenuAccess]::SelectPrice($window, $processId, $before, $before.Queue[0].Number, $true)
    $actions.Add('Select first included price: selection verified')
}
if ($Mode -eq 'OpenMatching') {
    if (-not $ConfirmNavigation) { throw 'Navigation confirmation required' }
    $document = [GingerMenuAccess]::ReadPriceDocument($window, $processId)
    [GingerMenuAccess]::OpenDocumentMatching($window, $processId, $document, $true)
    $actions.Add('Ensure bottom matching view for verified supplier document')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $matchingView = [GingerMenuAccess]::InspectMatching($window, $processId)
        if ($matchingView.Status -ne 'panels_not_verified') { break }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt 10)
    $matchingStatus = $matchingView.Status
}
if ($Mode -eq 'OpenPrices' -and -not [GingerMenuAccess]::PricesVisible($window, $processId)) {
    [GingerMenuAccess]::Invoke($window, $processId, 'журналы')
    $actions.Add('Журналы')
    # Re-resolve the leaf from a fresh tree. Never reuse a cached element index.
    [GingerMenuAccess]::Invoke($window, $processId, 'журналы/текущие прайс-листы')
    $actions.Add('Текущие прайс-листы')
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not [GingerMenuAccess]::PricesVisible($window, $processId)) {
        if ($deadline.Elapsed.TotalSeconds -ge 10) { throw 'Price table not verified after navigation; do not retry automatically' }
        Start-Sleep -Milliseconds 200
    }
}
if ($Mode -eq 'OpenSelectedPrice') {
    if (-not $ConfirmNavigation) { throw 'Navigation confirmation required' }
    $before = [GingerMenuAccess]::ReadPrices($window, $processId)
    $selected = @($before.Rows | Where-Object Selected)
    if ($selected.Count -ne 1) { throw 'Exactly one selected price is required' }
    [GingerMenuAccess]::OpenSelectedPrice($window, $processId, $before, $selected[0].Number, $true)
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    do {
        $document=$null
        try {$document=[GingerMenuAccess]::ReadPriceDocument($window,$processId)} catch { }
        if($null -ne $document){break}
        Start-Sleep -Milliseconds 200
    } while($deadline.Elapsed.TotalSeconds -lt 10)
    if($null -eq $document -or $document.Number -ne $selected[0].Number -or $document.Supplier -ne $selected[0].Supplier) {throw 'Opened price document not verified; no retry'}
    $actions.Add('Double-click supplier cell: document number and supplier verified')
}
$menu = @([GingerMenuAccess]::Menu($window, $processId) | Where-Object { $_.Path -in @('журналы', 'журналы/текущие прайс-листы') } | ForEach-Object { @{name=$_.Name; path=$_.Path; state=$_.State} })
[pscustomobject]@{
    source = 'Ginger semantic navigation'; mode = $Mode; process_id = $processId
    prices_visible = [GingerMenuAccess]::PricesVisible($window, $processId)
    actions = $actions.ToArray(); menu = $menu
    headers = [GingerMenuAccess]::Headers($window, $processId).ToArray()
    catalogue_changed = $false; row_identity_verified = $false
    candidate_snapshot = $candidateSnapshot
    selected_product = $selectedProduct
    prices = $prices
    matching_status = $matchingStatus
    matching_view = $matchingView
} | ConvertTo-Json -Depth 6
