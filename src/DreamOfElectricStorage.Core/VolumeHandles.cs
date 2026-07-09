using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace DreamOfElectricStorage.Core;

/// <summary>Shared raw-volume handle plumbing for the MFT enumerator and journal watcher.</summary>
internal static class VolumeHandles
{
    /// <summary>Accepts "C", "C:", or "C:\" (any case) and yields the raw volume path \\.\C:.</summary>
    internal static string Normalize(string volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);

        string trimmed = volume.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':')
            trimmed = trimmed[..1];

        if (trimmed.Length != 1 || !char.IsAsciiLetter(trimmed[0]))
            throw new ArgumentException($"'{volume}' is not a drive letter (expected e.g. \"C:\").", nameof(volume));

        return $@"\\.\{char.ToUpperInvariant(trimmed[0])}:";
    }

    /// <summary>Opens a read handle to the volume ("C:" or already-normalized \\.\C:). Needs elevation.</summary>
    internal static SafeFileHandle Open(string volume)
    {
        string volumePath = volume.StartsWith(@"\\.\", StringComparison.Ordinal) ? volume : Normalize(volume);

        SafeFileHandle handle = PInvoke.CreateFile(
            volumePath,
            (uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            hTemplateFile: null);

        if (!handle.IsInvalid)
            return handle;

        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw error switch
        {
            WIN32_ERROR.ERROR_ACCESS_DENIED => new UnauthorizedAccessException(
                $"Access to {volumePath} was denied. Raw volume access requires an elevated (administrator) process."),
            _ => new IOException($"Failed to open volume {volumePath} (Win32 error {(uint)error})."),
        };
    }

    /// <summary>Maps the current last-error to the conventional exception for a failed volume FSCTL.</summary>
    internal static Exception MapError(string operation, string volume)
    {
        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        return error switch
        {
            WIN32_ERROR.ERROR_INVALID_FUNCTION => new NotSupportedException(
                $"{volume} does not support {operation} (NTFS only)."),
            WIN32_ERROR.ERROR_ACCESS_DENIED => new UnauthorizedAccessException(
                $"Access to {volume} was denied. {operation} requires an elevated (administrator) process."),
            _ => new IOException($"{operation} on {volume} failed with Win32 error {(uint)error}."),
        };
    }
}
