using System.IO.MemoryMappedFiles;
using System.Text;

namespace NextLevelMetrics;

internal sealed record RtssGameSample(
    uint Pid, string Name, uint Time0, uint Time1, uint Frames, double? Fps);

// Layout and units are from RTSS's installed SDK/Include/RTSSSharedMemory.h.
internal sealed unsafe class RtssSharedMemoryClient : IDisposable
{
    private const uint Signature = 0x52545353; // 'RTSS' in the SDK's DWORD layout.
    private const string Owner = "NextLevelMetrics";
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private readonly uint _appEntrySize;
    private readonly uint _appOffset;
    private readonly uint _appCount;
    private readonly uint _osdEntrySize;
    private readonly uint _osdOffset;
    private readonly uint _osdCount;
    private int _slot = -1;
    private uint _positionedProcessId;
    private uint _previousX;
    private uint _previousY;
    private uint _appliedX;
    private uint _appliedY;
    private bool _disposed;

    public uint Version { get; }

    public RtssSharedMemoryClient()
    {
        _mapping = MemoryMappedFile.OpenExisting(
            "RTSSSharedMemoryV2", MemoryMappedFileRights.ReadWrite);
        _view = _mapping.CreateViewAccessor(
            0, 0, MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        try
        {
            if (Read32(0) != Signature)
                throw new InvalidOperationException("RTSS shared memory has an invalid signature.");
            Version = Read32(4);
            if (Version < 0x0002000E)
                throw new NotSupportedException($"RTSS shared memory version 0x{Version:X8} is unsupported.");

            _appEntrySize = Read32(8);
            _appOffset = Read32(12);
            _appCount = Read32(16);
            _osdEntrySize = Read32(20);
            _osdOffset = Read32(24);
            _osdCount = Read32(28);
            ValidateArray(_appOffset, _appEntrySize, _appCount, 324);
            ValidateArray(_osdOffset, _osdEntrySize, _osdCount, 4608);
        }
        catch
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mapping.Dispose();
            throw;
        }
    }

    private void ValidateArray(uint offset, uint entrySize, uint count, uint requiredSize)
    {
        if (entrySize < requiredSize || count == 0 ||
            (ulong)offset + (ulong)entrySize * count > (ulong)_view.Capacity)
            throw new InvalidOperationException("RTSS shared memory array bounds are invalid.");
    }

    private uint Read32(long offset) => _view.ReadUInt32(offset);

    private string ReadText(long offset, int maxBytes)
    {
        byte[] bytes = new byte[maxBytes];
        _view.ReadArray(offset, bytes, 0, bytes.Length);
        int length = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, length < 0 ? bytes.Length : length);
    }

    private bool TryLock()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (System.Threading.Interlocked.CompareExchange(
                    ref *(int*)(_pointer + 36), 1, 0) == 0)
                return true;
            Thread.Sleep(1);
        }
        return false;
    }

    private void Unlock() => System.Threading.Volatile.Write(ref *(int*)(_pointer + 36), 0);

    public int AcquireOsdSlot()
    {
        if (_slot >= 0) return _slot;
        if (!TryLock()) throw new TimeoutException("RTSS OSD is busy.");
        try
        {
            // The official sample leaves slot 0 for a primary OSD client.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int index = 1; index < _osdCount; index++)
                {
                    long entry = _osdOffset + (long)index * _osdEntrySize;
                    string owner = ReadText(entry + 256, 256);
                    if ((pass == 0 && owner == Owner) || (pass == 1 && owner.Length == 0))
                    {
                        if (owner.Length == 0)
                            WriteText(entry + 256, 256, Owner);
                        _slot = index;
                        return index;
                    }
                }
            }
            throw new InvalidOperationException("No free RTSS OSD slot is available.");
        }
        finally { Unlock(); }
    }

    private void WriteText(long offset, int capacity, string text)
    {
        byte[] bytes = new byte[capacity];
        int count = Encoding.Latin1.GetBytes(text, 0,
            Math.Min(text.Length, capacity - 1), bytes, 0);
        bytes[count] = 0;
        _view.WriteArray(offset, bytes, 0, bytes.Length);
    }

    public void WriteOsd(string text)
    {
        if (_slot < 0) throw new InvalidOperationException("No RTSS OSD slot acquired.");
        if (!TryLock()) throw new TimeoutException("RTSS OSD is busy.");
        try
        {
            long entry = _osdOffset + (long)_slot * _osdEntrySize;
            if (ReadText(entry + 256, 256) != Owner)
                throw new InvalidOperationException("RTSS OSD slot ownership changed.");
            WriteText(entry + 512, 4096, text);
            if (text.Length == 0) RestoreGamePosition();
            _view.Write(32, unchecked(Read32(32) + 1));
        }
        finally { Unlock(); }
    }

    private long FindGameEntry(uint processId)
    {
        for (int index = 0; index < _appCount; index++)
        {
            long entry = _appOffset + (long)index * _appEntrySize;
            if (Read32(entry) == processId) return entry;
        }
        return -1;
    }

    private void RestoreGamePosition()
    {
        if (_positionedProcessId == 0) return;
        long entry = FindGameEntry(_positionedProcessId);
        if (entry >= 0 && Read32(entry + 316) == _appliedX &&
            Read32(entry + 320) == _appliedY)
        {
            _view.Write(entry + 316, _previousX);
            _view.Write(entry + 320, _previousY);
        }
        _positionedProcessId = 0;
    }

    private void PositionGame(uint processId, OverlayPosition position)
    {
        if (processId == 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (!TryLock()) throw new TimeoutException("RTSS OSD is busy.");
        try
        {
            if (_positionedProcessId != processId) RestoreGamePosition();
            long entry = FindGameEntry(processId);
            if (entry < 0) throw new InvalidOperationException("RTSS no encuentra el proceso del juego.");

            if (_positionedProcessId == 0)
            {
                _previousX = Read32(entry + 316);
                _previousY = Read32(entry + 320);
                _positionedProcessId = processId;
            }

            bool right = position is OverlayPosition.SuperiorDerecha or OverlayPosition.InferiorDerecha;
            bool bottom = position is OverlayPosition.InferiorIzquierda or OverlayPosition.InferiorDerecha;
            _appliedX = unchecked((uint)(right ? -20 : 20));
            _appliedY = unchecked((uint)(bottom ? -20 : 20));
            _view.Write(entry + 316, _appliedX);
            _view.Write(entry + 320, _appliedY);
        }
        finally { Unlock(); }
    }

    public void WriteMetrics(OverlayPresentation presentation, OverlaySettings settings, uint gameProcessId)
    {
        PositionGame(gameProcessId, settings.Posicion);
        int fontWeight = settings.Fuente == "Segoe UI Semibold" ? 600 : 400;
        // Calibración inicial: el texto RTSS a 17 unidades se veía a mitad de tamaño que WPF.
        const int fontHeightAt100 = 34;
        var text = new StringBuilder();
        text.Append("<L0>");
        text.Append("<FNT=").Append(settings.Fuente).Append(",-")
            .Append(fontHeightAt100).Append(',')
            .Append(fontWeight).Append(",1>");
        text.Append("<S=").Append(settings.TamanoPorcentaje).Append('>');
        if (settings.Fondo == OverlayBackground.OscuroSuave)
            text.Append("<C=202020><B=0,0>\b");

        string? previousColor = null;
        foreach (OverlayPart part in presentation.Parts)
        {
            if (!string.Equals(previousColor, part.Color, StringComparison.OrdinalIgnoreCase))
            {
                text.Append("<C=").Append(part.Color.AsSpan(1)).Append('>');
                previousColor = part.Color;
            }
            text.Append(part.Text);
        }
        WriteOsd(text.ToString());
    }

    public RtssGameSample? ReadGame(string executable, bool includeFps)
    {
        for (int index = 0; index < _appCount; index++)
        {
            long entry = _appOffset + (long)index * _appEntrySize;
            uint pid = Read32(entry);
            if (pid == 0) continue;
            string name = ReadText(entry + 4, 260);
            if (!string.Equals(System.IO.Path.GetFileName(name), executable,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            uint time0 = Read32(entry + 268);
            uint time1 = Read32(entry + 272);
            uint frames = Read32(entry + 276);
            uint elapsed = unchecked(time1 - time0);
            double? fps = includeFps && time0 != 0 && elapsed > 0 && elapsed < 5000 && frames > 0
                ? 1000.0 * frames / elapsed
                : null;
            return new RtssGameSample(pid, name, time0, time1, frames, fps);
        }
        return null;
    }

    public RtssGameSample? ReadProcess(uint processId)
    {
        if (processId == 0) return null;
        for (int index = 0; index < _appCount; index++)
        {
            long entry = _appOffset + (long)index * _appEntrySize;
            if (Read32(entry) != processId) continue;
            string name = ReadText(entry + 4, 260);
            uint api = Read32(entry + 264) & 0xFFFF;
            if (name.Length == 0 || api is < 1 or > 10) return null;

            uint time0 = Read32(entry + 268);
            uint time1 = Read32(entry + 272);
            uint frames = Read32(entry + 276);
            uint elapsed = unchecked(time1 - time0);
            double? fps = time0 != 0 && elapsed > 0 && elapsed < 5000 && frames > 0
                ? 1000.0 * frames / elapsed
                : null;
            return new RtssGameSample(processId, name, time0, time1, frames, fps);
        }
        return null;
    }

    public uint FindActiveProcessId(uint preferredProcessId)
    {
        uint now = unchecked((uint)Environment.TickCount);
        uint bestProcessId = 0;
        uint bestAge = uint.MaxValue;

        for (int index = 0; index < _appCount; index++)
        {
            long entry = _appOffset + (long)index * _appEntrySize;
            uint pid = Read32(entry);
            uint api = Read32(entry + 264) & 0xFFFF;
            uint time1 = Read32(entry + 272);
            uint frames = Read32(entry + 276);
            uint age = unchecked(now - time1);
            if (pid == 0 || api is < 1 or > 10 || time1 == 0 || frames == 0 || age > 3000)
                continue;

            if (pid == preferredProcessId) return pid;
            if (age < bestAge)
            {
                bestAge = age;
                bestProcessId = pid;
            }
        }

        return bestProcessId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_slot >= 0 && TryLock())
            {
                try
                {
                    RestoreGamePosition();
                    long entry = _osdOffset + (long)_slot * _osdEntrySize;
                    if (ReadText(entry + 256, 256) == Owner)
                    {
                        WriteText(entry, 256, "");
                        WriteText(entry + 512, 4096, "");
                        WriteText(entry + 256, 256, "");
                        _view.Write(32, unchecked(Read32(32) + 1));
                    }
                }
                finally { Unlock(); }
            }
        }
        finally
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mapping.Dispose();
        }
    }
}
