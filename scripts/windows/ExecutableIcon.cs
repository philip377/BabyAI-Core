using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

// Build-time resource editing only. LoadLibraryEx uses DATAFILE/IMAGE_RESOURCE,
// so the supplied PE is never executed. Call on a staging copy before appending
// the SFX configuration/archive; preserve every non-icon resource unchanged.
public static class BabyAIExecutableIcon
{
    private const int IconType = 3, GroupType = 14;
    private delegate bool NameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr context);
    private delegate bool LanguageCallback(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr context);
    private sealed class Group
    {
        internal int Id;
        internal string Name;
        internal readonly List<ushort> Languages = new List<ushort>();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, NameCallback callback, IntPtr context);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumResourceLanguagesW(IntPtr module, IntPtr type, IntPtr name, LanguageCallback callback, IntPtr context);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string path, [MarshalAs(UnmanagedType.Bool)] bool deleteExisting);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateResourceW(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndUpdateResourceW(IntPtr update, [MarshalAs(UnmanagedType.Bool)] bool discard);

    public static void Apply(string executable, string iconPath)
    {
        executable = Path.GetFullPath(executable);
        iconPath = Path.GetFullPath(iconPath);
        if (!File.Exists(executable) || !File.Exists(iconPath)) throw new FileNotFoundException("Executable/icon missing");
        if ((File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(iconPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Executable/icon must be ordinary files");
        byte[] icon = File.ReadAllBytes(iconPath);
        if (icon.Length < 22 || icon.Length > 4 * 1024 * 1024 || U16(icon, 0) != 0 || U16(icon, 2) != 1)
            throw new InvalidDataException("Invalid ICO header");
        int count = U16(icon, 4);
        if (count < 1 || count > 16 || icon.Length < 6 + 16 * count) throw new InvalidDataException("Invalid ICO count");
        var images = new List<byte[]>();
        long previousEnd = 6 + 16 * count;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            uint length = U32(icon, entry + 8), offset = U32(icon, entry + 12);
            if (icon[entry] != icon[entry + 1] || icon[entry + 3] != 0 || U16(icon, entry + 4) != 1 ||
                U16(icon, entry + 6) != 32 || length == 0 || offset != previousEnd || (long)offset + length > icon.Length)
                throw new InvalidDataException("Invalid or overlapping 32-bit ICO image");
            var bytes = new byte[checked((int)length)];
            Buffer.BlockCopy(icon, checked((int)offset), bytes, 0, bytes.Length);
            images.Add(bytes);
            previousEnd = (long)offset + length;
        }
        if (previousEnd != icon.Length) throw new InvalidDataException("Trailing ICO data");

        var groups = new List<Group>();
        var usedIds = new HashSet<int>();
        IntPtr module = LoadLibraryExW(executable, IntPtr.Zero, 0x02 | 0x20);
        if (module == IntPtr.Zero) throw Error("Cannot read PE resources");
        try
        {
            NameCallback icons = delegate(IntPtr m, IntPtr t, IntPtr name, IntPtr ignored)
            {
                long id = name.ToInt64();
                if (id >= 0 && id <= ushort.MaxValue) usedIds.Add((int)id);
                return true;
            };
            EnumerateNames(module, IconType, icons);
            Exception callbackError = null;
            NameCallback names = delegate(IntPtr m, IntPtr t, IntPtr name, IntPtr ignored)
            {
                try
                {
                    long id = name.ToInt64();
                    Group group = new Group();
                    if (id >= 0 && id <= ushort.MaxValue) group.Id = (int)id;
                    else group.Name = Marshal.PtrToStringUni(name);
                    LanguageCallback languages = delegate(IntPtr lm, IntPtr lt, IntPtr ln, ushort language, IntPtr lp)
                    {
                        group.Languages.Add(language);
                        return true;
                    };
                    if (!EnumResourceLanguagesW(m, t, name, languages, IntPtr.Zero)) throw Error("Cannot enumerate icon languages");
                    groups.Add(group);
                    return true;
                }
                catch (Exception error) { callbackError = error; return false; }
            };
            bool enumerated = EnumResourceNamesW(module, new IntPtr(GroupType), names, IntPtr.Zero);
            int enumerationError = Marshal.GetLastWin32Error();
            if (callbackError != null) throw new InvalidOperationException("Cannot inspect PE icon groups", callbackError);
            if (!enumerated && enumerationError != 1813 && enumerationError != 1812)
                throw new Win32Exception(enumerationError, "Cannot enumerate PE icon groups");
        }
        finally { FreeLibrary(module); }
        if (groups.Count == 0)
        {
            Group group = new Group { Id = 1 };
            group.Languages.Add(0);
            groups.Add(group);
        }
        // Keep all existing group IDs/languages, including the SFX dialog icon.
        // Allocate unused RT_ICON IDs instead of overwriting unrelated resources.
        var ids = new ushort[count];
        int next = 1;
        for (int i = 0; i < count; i++)
        {
            while (next <= ushort.MaxValue && usedIds.Contains(next)) next++;
            if (next > ushort.MaxValue) throw new InvalidDataException("No free icon resource IDs");
            ids[i] = (ushort)next++;
        }
        byte[] groupData = new byte[6 + count * 14];
        Buffer.BlockCopy(icon, 0, groupData, 0, 6);
        for (int i = 0; i < count; i++)
        {
            Buffer.BlockCopy(icon, 6 + i * 16, groupData, 6 + i * 14, 12);
            groupData[6 + i * 14 + 12] = (byte)(ids[i] & 255);
            groupData[6 + i * 14 + 13] = (byte)(ids[i] >> 8);
        }
        var allLanguages = new HashSet<ushort>();
        foreach (Group group in groups) foreach (ushort language in group.Languages) allLanguages.Add(language);
        IntPtr update = BeginUpdateResourceW(executable, false);
        if (update == IntPtr.Zero) throw Error("Cannot begin icon update");
        try
        {
            foreach (ushort language in allLanguages)
                for (int i = 0; i < count; i++)
                    Put(update, IconType, new IntPtr(ids[i]), language, images[i]);
            foreach (Group group in groups)
            {
                IntPtr name = group.Name == null ? new IntPtr(group.Id) : Marshal.StringToHGlobalUni(group.Name);
                try { foreach (ushort language in group.Languages) Put(update, GroupType, name, language, groupData); }
                finally { if (group.Name != null) Marshal.FreeHGlobal(name); }
            }
            // EndUpdateResource closes the handle even when commit reports failure.
            IntPtr committing = update;
            update = IntPtr.Zero;
            if (!EndUpdateResourceW(committing, false)) throw Error("Cannot commit icon resources");
        }
        finally { if (update != IntPtr.Zero) EndUpdateResourceW(update, true); }
    }

    private static void EnumerateNames(IntPtr module, int type, NameCallback callback)
    {
        if (EnumResourceNamesW(module, new IntPtr(type), callback, IntPtr.Zero)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != 1813 && error != 1812) throw new Win32Exception(error, "Cannot enumerate PE icon resources");
    }
    private static void Put(IntPtr update, int type, IntPtr name, ushort language, byte[] bytes)
    {
        if (!UpdateResourceW(update, new IntPtr(type), name, language, bytes, (uint)bytes.Length)) throw Error("Cannot write icon resource");
    }
    private static ushort U16(byte[] bytes, int offset) { return BitConverter.ToUInt16(bytes, offset); }
    private static uint U32(byte[] bytes, int offset) { return BitConverter.ToUInt32(bytes, offset); }
    private static Win32Exception Error(string message) { return new Win32Exception(Marshal.GetLastWin32Error(), message); }
}
