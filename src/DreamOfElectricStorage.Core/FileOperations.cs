using System.Runtime.InteropServices;

namespace DreamOfElectricStorage.Core;

/// <summary>
/// The manage-from-graph primitives: rename, move, delete-to-Recycle-Bin.
/// No index coupling on purpose — the USN journal watcher propagates every change
/// back into the index within ~1s, so callers never mutate the index directly.
/// </summary>
public static class FileOperations
{
    /// <summary>Renames a file or directory in place. Returns the new full path.</summary>
    public static string Rename(string path, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{newName}' contains characters that aren't allowed in file names.", nameof(newName));

        string parent = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));
        string destination = Path.Combine(parent, newName);

        if (string.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
            return path;
        if (Path.Exists(destination))
            throw new IOException($"'{newName}' already exists in {parent}.");

        MoveEntry(path, destination);
        return destination;
    }

    /// <summary>Moves a file or directory into <paramref name="destinationDir"/>. Returns the new full path.</summary>
    public static string Move(string sourcePath, string destinationDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);

        if (!Directory.Exists(destinationDir))
            throw new DirectoryNotFoundException($"Destination folder not found: {destinationDir}");

        string destination = Path.Combine(destinationDir, Path.GetFileName(sourcePath.TrimEnd('\\', '/')));
        if (string.Equals(destination, sourcePath, StringComparison.OrdinalIgnoreCase))
            return sourcePath;
        if (Path.Exists(destination))
            throw new IOException($"'{Path.GetFileName(destination)}' already exists in {destinationDir}.");

        bool isDirectory = Directory.Exists(sourcePath);
        bool crossVolume = !string.Equals(Path.GetPathRoot(sourcePath), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase);
        if (isDirectory && crossVolume)
            throw new NotSupportedException("Moving a folder to a different drive isn't supported yet (needs copy + delete).");

        MoveEntry(sourcePath, destination);
        return destination;
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        else if (File.Exists(source))
            File.Move(source, destination);
        else
            throw new FileNotFoundException($"Not found: {source}", source);
    }

    /// <summary>Sends a file or directory to the Recycle Bin (recoverable, not a hard delete).</summary>
    public static void DeleteToRecycleBin(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Recycle Bin deletion requires a fully qualified path.", nameof(path)); // else the shell hard-deletes
        if (!Path.Exists(path))
            throw new FileNotFoundException($"Not found: {path}", path);

        // pFrom is a double-null-terminated list; a managed string marshaled as LPWSTR
        // truncates at the first '\0', so the buffer is marshaled by hand.
        nint pFrom = Marshal.AllocHGlobal((path.Length + 2) * sizeof(char));
        try
        {
            char[] buffer = new char[path.Length + 2]; // string + "\0\0"
            path.CopyTo(buffer);
            Marshal.Copy(buffer, 0, pFrom, buffer.Length);

            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = pFrom,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };

            int result = SHFileOperationW(ref op);
            if (result != 0)
                throw new IOException($"Recycle Bin deletion of '{path}' failed (shell error 0x{result:x}).");
            if (op.fAnyOperationsAborted)
                throw new OperationCanceledException($"Recycle Bin deletion of '{path}' was aborted by the shell.");
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
        }
    }

    // Hand-written interop: CsWin32 refuses SHFILEOPSTRUCT under AnyCPU (PInvoke005) because
    // the x86 layout is packed. This app is 64-bit only (x64/ARM64 — default packing), so the
    // sequential layout below is correct. Values from shellapi.h.
    private const uint FO_DELETE = 0x3;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public nint pFrom;
        public nint pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public nint lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
