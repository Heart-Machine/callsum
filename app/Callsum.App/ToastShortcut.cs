using System.Runtime.InteropServices;
using System.Text;

namespace Callsum.App;

/// <summary>Registers the desktop-toast identity and icon with a Start menu shortcut.</summary>
internal static class ToastShortcut
{
    private static readonly PropertyKey AppUserModelId = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "callsum-icon.ico");

    public static void Install(string appId, string displayName)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var shortcutPath = Path.Combine(folder, "callsum.lnk");
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(executable);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory);
            link.SetDescription(displayName);
            link.SetIconLocation(IconPath, 0);

            var store = (IPropertyStore)link;
            var appUserModelId = AppUserModelId;
            using var value = new PropVariant(appId);
            store.SetValue(ref appUserModelId, ref value.Value);
            store.Commit();

            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativePropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    private sealed class PropVariant(string value) : IDisposable
    {
        public NativePropVariant Value = new()
        {
            Type = (ushort)VarEnum.VT_LPWSTR,
            Pointer = Marshal.StringToCoTaskMemUni(value),
        };

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(Value.Pointer);
            Value.Pointer = IntPtr.Zero;
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder path, int capacity, IntPtr data, uint flags);
        void GetIdList(out IntPtr idList);
        void SetIdList(IntPtr idList);
        void GetDescription(StringBuilder description, int capacity);
        void SetDescription(string description);
        void GetWorkingDirectory(StringBuilder directory, int capacity);
        void SetWorkingDirectory(string directory);
        void GetArguments(StringBuilder arguments, int capacity);
        void SetArguments(string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int command);
        void SetShowCommand(int command);
        void GetIconLocation(StringBuilder iconPath, int capacity, out int index);
        void SetIconLocation(string iconPath, int index);
        void SetRelativePath(string relativePath, int reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath(string path);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassId(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load(string fileName, int mode);
        void Save(string fileName, bool remember);
        void SaveCompleted(string fileName);
        void GetCurrentFile(out IntPtr fileName);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out NativePropVariant value);
        void SetValue(ref PropertyKey key, ref NativePropVariant value);
        void Commit();
    }
}
