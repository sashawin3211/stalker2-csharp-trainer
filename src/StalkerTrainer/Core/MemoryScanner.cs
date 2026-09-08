using System.Diagnostics;

namespace StalkerTrainer.Core;

public readonly record struct MemoryRegion(ulong Base, ulong Size);
public readonly record struct Candidate(ulong Address, MemoryValue Value);
public readonly record struct ScanProgress(long BytesRead, int Matches, int RegionsDone, int RegionsTotal);
public sealed record ScanResult(List<Candidate> Candidates, long BytesRead, long SkippedBytes);

public interface IMemoryReader
{
    IReadOnlyList<MemoryRegion> GetRegions(CancellationToken token);
    int Read(ulong address, byte[] buffer, int count);
}

public sealed class MemoryScanner(IMemoryReader memory, int maxCandidates = 500_000)
{
    public ScanResult FirstScan(MemoryValue target, IProgress<ScanProgress>? progress, CancellationToken token)
        => FirstScanMany([target], progress, token);

    public ScanResult FirstScanMany(IReadOnlyList<MemoryValue> targets, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        if (targets.Count == 0) throw new ArgumentException("No search values provided.");
        var regions = memory.GetRegions(token);
        var results = new List<Candidate>();
        var buffer = new byte[1024 * 1024];
        var patterns = targets.Select(t => t.ToBytes()).ToArray();
        var bridge = new byte[14];
        var tailLength = 0;
        ulong previousEnd = 0;
        long totalRead = 0, skipped = 0;
        var clock = Stopwatch.StartNew();
        for (var r = 0; r < regions.Count; r++)
        {
            var region = regions[r];
            for (ulong offset = 0; offset < region.Size;)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min((ulong)buffer.Length, region.Size - offset);
                var read = memory.Read(region.Base + offset, buffer, count);
                ScanBlock(buffer.AsSpan(0, read), region.Base + offset);
                totalRead += read;
                // A concurrently changing region may fail as a large read. Recover page by page.
                if (read < count)
                {
                    var pageOffset = read;
                    while (pageOffset < count)
                    {
                        token.ThrowIfCancellationRequested();
                        var address = region.Base + offset + (ulong)pageOffset;
                        var pageCount = Math.Min(4096 - (int)(address % 4096), count - pageOffset);
                        var pageRead = memory.Read(address, buffer, pageCount);
                        ScanBlock(buffer.AsSpan(0, pageRead), address);
                        totalRead += pageRead;
                        skipped += pageCount - pageRead;
                        pageOffset += pageCount;
                    }
                }
                offset += (ulong)count;
                if (clock.ElapsedMilliseconds > 200)
                {
                    progress?.Report(new(totalRead, results.Count, r, regions.Count));
                    clock.Restart();
                }
            }
        }
        progress?.Report(new(totalRead, results.Count, regions.Count, regions.Count));
        results.Sort((a, b) => a.Address.CompareTo(b.Address));
        return new(results, totalRead, skipped);

        void ScanBlock(ReadOnlySpan<byte> data, ulong start)
        {
            if (data.Length == 0) { tailLength = 0; return; }
            if (previousEnd == start && tailLength > 0)
            {
                var headLength = Math.Min(7, data.Length);
                data[..headLength].CopyTo(bridge.AsSpan(tailLength));
                for (var t = 0; t < targets.Count; t++)
                {
                    // Inspect just values which straddle the previous/current read boundary.
                    for (var i = Math.Max(0, tailLength - patterns[t].Length + 1); i < tailLength; i++)
                    {
                        if (i + patterns[t].Length <= tailLength + headLength && bridge.AsSpan(i, patterns[t].Length).SequenceEqual(patterns[t]))
                            AddCandidate(start - (ulong)tailLength + (ulong)i, targets[t], results);
                    }
                }
            }
            for (var t = 0; t < targets.Count; t++) AddMatches(data, start, targets[t], patterns[t], results);
            tailLength = Math.Min(7, data.Length);
            data[^tailLength..].CopyTo(bridge);
            previousEnd = start + (ulong)data.Length;
        }
    }

    private void AddMatches(ReadOnlySpan<byte> data, ulong start, MemoryValue target, byte[] pattern, List<Candidate> results)
    {
        var offset = 0;
        while (offset <= data.Length - pattern.Length)
        {
            var index = data[offset..].IndexOf(pattern);
            if (index < 0) break;
            offset += index;
            var address = start + (ulong)offset;
            AddCandidate(address, target, results);
            offset++;
        }
    }

    private void AddCandidate(ulong address, MemoryValue target, List<Candidate> results)
    {
        if (results.Count >= maxCandidates)
            throw new InvalidOperationException($"Больше {maxCandidates:N0} совпадений. Измени число в игре и начни новый поиск по менее частому значению.");
        results.Add(new(address, target));
    }

    public ScanResult Refine(IReadOnlyList<Candidate> previous, FilterKind filter, MemoryValue? target, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var results = new List<Candidate>();
        var page = new byte[4096];
        ulong loadedPage = ulong.MaxValue;
        var pageRead = 0;
        long bytesRead = 0, skipped = 0;
        for (var i = 0; i < previous.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = previous[i];
            var pageBase = item.Address & ~4095UL;
            if (pageBase != loadedPage)
            {
                pageRead = memory.Read(pageBase, page, page.Length);
                bytesRead += pageRead;
                loadedPage = pageBase;
            }
            var offset = (int)(item.Address - pageBase);
            MemoryValue current;
            if (offset + item.Value.Size <= pageRead)
                current = MemoryValue.Read(page.AsSpan(offset, item.Value.Size), item.Value.Kind);
            else
            {
                var small = new byte[item.Value.Size];
                var read = memory.Read(item.Address, small, small.Length);
                bytesRead += read;
                if (read != small.Length) { skipped += small.Length - read; continue; }
                current = MemoryValue.Read(small, item.Value.Kind);
            }
            if (current.Matches(item.Value, filter, target)) results.Add(new(item.Address, current));
            if (i % 10000 == 0) progress?.Report(new(bytesRead, results.Count, i, previous.Count));
        }
        progress?.Report(new(bytesRead, results.Count, previous.Count, previous.Count));
        return new(results, bytesRead, skipped);
    }
}
