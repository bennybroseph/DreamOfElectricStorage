using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace DreamOfElectricStorage.Core;

/// <summary>Snapshot of a volume's journal identity: where a watch should start.</summary>
public readonly record struct JournalState(ulong JournalId, long NextUsn);

/// <summary>
/// A batch of journal changes. <see cref="RequiresRebuild"/> means the journal was
/// recreated or wrapped past our position — the index snapshot has a gap and the
/// volume must be fully re-enumerated. It is the final batch of the sequence.
/// </summary>
public sealed record UsnChangeBatch(IReadOnlyList<UsnJournalEntry> Entries, long NextUsn, bool RequiresRebuild)
{
    public static readonly UsnChangeBatch Rebuild = new([], 0, RequiresRebuild: true);
}

/// <summary>
/// Streams NTFS USN change-journal records for one volume via FSCTL_READ_USN_JOURNAL.
/// Requires elevation, like all raw volume access.
/// </summary>
public sealed class UsnJournalWatcher
{
    private const int ChunkSize = 64 * 1024;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);

    private const uint WatchedReasons =
        PInvoke.USN_REASON_FILE_CREATE |
        PInvoke.USN_REASON_FILE_DELETE |
        PInvoke.USN_REASON_RENAME_OLD_NAME |
        PInvoke.USN_REASON_RENAME_NEW_NAME;

    /// <summary>Reads the volume's current journal id and next USN (the watch start point).</summary>
    public static unsafe JournalState QueryJournal(string volume)
    {
        using SafeFileHandle handle = VolumeHandles.Open(volume);

        USN_JOURNAL_DATA_V0 data;
        uint bytesReturned;
        BOOL ok = PInvoke.DeviceIoControl(
            handle,
            PInvoke.FSCTL_QUERY_USN_JOURNAL,
            null,
            0,
            &data,
            (uint)sizeof(USN_JOURNAL_DATA_V0),
            &bytesReturned,
            null);

        if (!ok)
            throw VolumeHandles.MapError("FSCTL_QUERY_USN_JOURNAL", volume);

        return new JournalState(data.UsnJournalID, data.NextUsn);
    }

    /// <summary>
    /// Polls the journal from <paramref name="state"/> onward, yielding non-empty batches.
    /// Ends after yielding a <see cref="UsnChangeBatch.RequiresRebuild"/> batch when the
    /// journal identity breaks (recreated/wrapped) — re-enumerate, re-query, watch again.
    /// </summary>
    public async IAsyncEnumerable<UsnChangeBatch> WatchAsync(
        string volume, JournalState state, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using SafeFileHandle handle = VolumeHandles.Open(volume);
        byte[] buffer = new byte[ChunkSize];
        long startUsn = state.NextUsn;

        while (!cancellationToken.IsCancellationRequested)
        {
            uint bytesReturned;
            bool discontinuity = false;
            try
            {
                long usn = startUsn;
                bytesReturned = await Task.Run(
                    () => ReadJournal(handle, state.JournalId, usn, buffer), cancellationToken).ConfigureAwait(false);
            }
            catch (JournalDiscontinuityException)
            {
                discontinuity = true;
                bytesReturned = 0;
            }

            if (discontinuity)
            {
                yield return UsnChangeBatch.Rebuild;
                yield break;
            }

            var entries = new List<UsnJournalEntry>();
            long nextUsn = UsnRecordParser.ParseJournalChunk(buffer.AsSpan(0, (int)bytesReturned), entries);

            if (nextUsn == startUsn || entries.Count == 0)
            {
                // Caught up (or only filtered records) — per docs, don't spin on the FSCTL.
                if (nextUsn != 0)
                    startUsn = nextUsn;
                await Task.Delay(IdleDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            startUsn = nextUsn;
            yield return new UsnChangeBatch(entries, nextUsn, RequiresRebuild: false);
        }
    }

    private static unsafe uint ReadJournal(SafeFileHandle handle, ulong journalId, long startUsn, byte[] buffer)
    {
        var request = new READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = startUsn,
            ReasonMask = WatchedReasons,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0, // non-blocking: empty read returns immediately with next USN
            UsnJournalID = journalId,
        };

        uint bytesReturned;
        BOOL ok;
        fixed (byte* bufferPtr = buffer)
        {
            ok = PInvoke.DeviceIoControl(
                handle,
                PInvoke.FSCTL_READ_USN_JOURNAL,
                &request,
                (uint)sizeof(READ_USN_JOURNAL_DATA_V0),
                bufferPtr,
                (uint)buffer.Length,
                &bytesReturned,
                null);
        }

        if (ok)
            return bytesReturned;

        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        throw error switch
        {
            // Journal recreated, wrapped past our USN, or being deleted → snapshot has a gap.
            WIN32_ERROR.ERROR_JOURNAL_ENTRY_DELETED or
            WIN32_ERROR.ERROR_JOURNAL_DELETE_IN_PROGRESS or
            WIN32_ERROR.ERROR_JOURNAL_NOT_ACTIVE or
            WIN32_ERROR.ERROR_INVALID_PARAMETER => new JournalDiscontinuityException(),
            WIN32_ERROR.ERROR_ACCESS_DENIED => new UnauthorizedAccessException(
                "Access to the volume was denied. Journal watching requires an elevated (administrator) process."),
            _ => new IOException($"FSCTL_READ_USN_JOURNAL failed with Win32 error {(uint)error}."),
        };
    }

    private sealed class JournalDiscontinuityException : Exception;
}
