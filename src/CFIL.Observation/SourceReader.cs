using System.Text.Json;

namespace CFIL.Observation;

public sealed record LinePointer(long Offset, int Length, long TimeMs, long Sequence = 0);

/// <summary>Only newline-terminated records become visible. A producer's partial write is never a scene.</summary>
public sealed class SourceReader(string path)
{
    public const int MaximumLineBytes = 16 * 1024 * 1024;
    public long Offset { get; private set; }
    public long BytesRead { get; private set; }
    public long Passes { get; private set; }
    public bool Exists => File.Exists(path);
    public long PublishedLength => Exists?new FileInfo(path).Length:0;
    public bool HasMore => Exists && new FileInfo(path).Length > Offset;
    public int ReadBatch(Action<JsonElement,LinePointer> accept, int budget = 8 * 1024 * 1024)
    {
        if (!Exists) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        if (stream.Length < Offset) throw new InvalidDataException("Source truncated; projection invalidated.");
        stream.Position = Offset; Passes++;
        var buffer = new byte[65536]; using var line = new MemoryStream();
        long position = Offset, start = Offset, initial = Offset; int count = 0;
        while (position - initial < budget || line.Length > 0)
        {
            var size = stream.Read(buffer); if (size == 0) break;
            BytesRead += size;
            for (var i=0;i<size;i++)
            {
                position++;
                if (buffer[i] != 10)
                {
                    line.WriteByte(buffer[i]);
                    if (line.Length > MaximumLineBytes) throw new InvalidDataException("Source record exceeds observation budget.");
                    continue;
                }
                using var document = JsonDocument.Parse(line.ToArray());
                var value = document.RootElement;
                accept(value, new(start, checked((int)(position-start)), value.Long("simulationTimeMs",value.Long("timeMs")),value.Long("sequence")));
                count++; Offset = position; start = position; line.SetLength(0);
                if (Offset-initial >= budget) return count;
            }
            // Offset advances only after the callback succeeds. A failed or partial record
            // is re-read after recovery instead of silently skipping its business information.
            if (position-start == 0 && position >= stream.Length) break;
            if (position >= stream.Length) break;
        }
        return count;
    }
    public JsonElement Read(LinePointer pointer)
    {
        using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        stream.Position=pointer.Offset; var bytes=new byte[pointer.Length];stream.ReadExactly(bytes);
        using var document=JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
    public T Read<T>(LinePointer pointer,JsonSerializerOptions options)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        stream.Position=pointer.Offset;
        var bytes=System.Buffers.ArrayPool<byte>.Shared.Rent(pointer.Length);
        try
        {
            stream.ReadExactly(bytes.AsSpan(0,pointer.Length));
            return JsonSerializer.Deserialize<T>(bytes.AsSpan(0,pointer.Length),options)??throw new InvalidDataException("Empty source frame.");
        }
        finally{System.Buffers.ArrayPool<byte>.Shared.Return(bytes);}
    }
}
