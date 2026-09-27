using System.Diagnostics;
using System.Runtime.InteropServices;

namespace pViewer.Services;

/// <summary>Integration with File Explorer and the Windows desktop.</summary>
public static class Shell
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, string vParam, uint winIni);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Deletes a file, moving it to the Recycle Bin if requested.</summary>
    public static void DeleteFile(string path, bool toRecycleBin, IntPtr owner = default)
    {
        if (!toRecycleBin)
        {
            File.Delete(path);
            return;
        }
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            // Without FOF_WANTNUKEWARNING, files that cannot be recycled (network shares, drives with no
            // Recycle Bin, files too big for it) would be deleted permanently without asking.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
            hwnd = owner,
        };
        int result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
            throw new IOException(op.fAnyOperationsAborted ? "The file was not deleted." : $"Cannot move the file to the Recycle Bin (code {result}).");
    }

    public static void ShowProperties(string path, IntPtr owner)
    {
        const uint SEE_MASK_INVOKEIDLIST = 0x0C;
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
        };
        ShellExecuteEx(ref info);
    }

    public static void OpenWith(string path) =>
        Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false });

    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void SetWallpaper(string path)
    {
        const uint SPI_SETDESKWALLPAPER = 0x14, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDWININICHANGE = 0x02;
        if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE))
            throw new IOException("Windows did not accept the image as background.");
    }

    /// <summary>Folder for the app's working files (e.g. the wallpaper copy).</summary>
    public static string LocalDataFolder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pViewer");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
