using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using StalkerTrainer.Core;
using StalkerTrainer.Native;
using StalkerTrainer;

if (args is ["--memory-fixture"])
{
    var pointer = Marshal.AllocHGlobal(64);
    try
    {
        Marshal.WriteInt32(pointer, 33032);
        Console.WriteLine(((ulong)pointer).ToString());
        Console.Out.Flush();
        while (Console.ReadLine() is { } command && command != "quit")
        {
            if (command == "read") { Console.WriteLine(Marshal.ReadInt32(pointer)); Console.Out.Flush(); }
        }
    }
    finally { Marshal.FreeHGlobal(pointer); }
    return 0;
}

var tests = new List<(string Name, Action Run)>
{
    ("numeric parsing and byte roundtrip", () =>
    {
        foreach (var kind in Enum.GetValues<ValueKind>())
        {
            var value = MemoryValue.Parse("33032", kind);
            Equal(value, MemoryValue.Read(value.ToBytes(), kind));
        }
        Equal("12.5", MemoryValue.Parse("12,5", ValueKind.Float32).ToString());
        Equal("-2147483648", MemoryValue.Parse("-2147483648", ValueKind.Int32).ToString());
        Throws<OverflowException>(() => MemoryValue.Parse("2147483648", ValueKind.Int32));
        Throws<FormatException>(() => MemoryValue.Parse("NaN", ValueKind.Float32));
        Throws<FormatException>(() => MemoryValue.Parse("Infinity", ValueKind.Float64));
    }),
    ("Int64 comparisons do not lose integer precision", () =>
    {
        var previous = MemoryValue.Parse("9007199254740992", ValueKind.Int64);
        var next = MemoryValue.Parse("9007199254740993", ValueKind.Int64);
        True(next.Matches(previous, FilterKind.Increased, null));
        True(previous.Matches(next, FilterKind.Decreased, null));
    }),
    ("snapshot serialization preserves addresses and numeric types", () =>
    {
        var candidates = Enum.GetValues<ValueKind>().Select(k => new Candidate(0x10000, MemoryValue.Parse("32967", k))).ToArray();
        var reloaded = JsonSerializer.Deserialize<Candidate[]>(JsonSerializer.Serialize(candidates))!;
        True(candidates.SequenceEqual(reloaded));
    }),
    ("one memory pass finds integer and floating point representations", () =>
    {
        var fake = new FakeMemory(8192);
        var targets = Enum.GetValues<ValueKind>().Select(k => MemoryValue.Parse("32967", k)).ToArray();
        for (var i = 0; i < targets.Length; i++) targets[i].ToBytes().CopyTo(fake.Bytes, i * 16);
        var result = new MemoryScanner(fake).FirstScanMany(targets, null, default);
        for (var i = 0; i < targets.Length; i++) True(result.Candidates.Contains(new Candidate(fake.Base + (ulong)(i * 16), targets[i])));
    }),
    ("scan finds aligned, unaligned and boundary-spanning values", () =>
    {
        var fake = new FakeMemory(2 * 1024 * 1024);
        foreach (var offset in new[] { 0, 16, 1024 * 1024 - 4, 1024 * 1024, fake.Bytes.Length - 4 }) fake.Set(offset, 33032);
        fake.Set(101, 33032); // Deliberately unaligned.
        fake.Set(1024 * 1024 + 4095, 33032);
        var result = new MemoryScanner(fake).FirstScan(MemoryValue.Parse("33032", ValueKind.Int32), null, default);
        Equal(7, result.Candidates.Count);
        True(result.Candidates.Any(c => c.Address == fake.Base + 101));
        Equal((long)fake.Bytes.Length, result.BytesRead);
    }),
    ("unaligned values crossing read chunks are found exactly once", () =>
    {
        var fake = new FakeMemory(2 * 1024 * 1024);
        fake.Set(1024 * 1024 - 2, 33032);
        var result = new MemoryScanner(fake).FirstScan(MemoryValue.Parse("33032", ValueKind.Int32), null, default);
        Equal(1, result.Candidates.Count);
        Equal(fake.Base + 1024 * 1024 - 2, result.Candidates[0].Address);
        Equal(1, new MemoryScanner(fake).Refine(result.Candidates, FilterKind.Unchanged, null, null, default).Candidates.Count);
    }),
    ("refine filters retain real changes and refresh snapshots", () =>
    {
        var fake = new FakeMemory(8192);
        fake.Set(4, 33032); fake.Set(8, 33032); fake.Set(12, 33032);
        var scanner = new MemoryScanner(fake);
        var initial = scanner.FirstScan(MemoryValue.Parse("33032", ValueKind.Int32), null, default);
        fake.Set(4, 33030); fake.Set(8, 33040);
        Equal(1, scanner.Refine(initial.Candidates, FilterKind.Unchanged, null, null, default).Candidates.Count);
        Equal(2, scanner.Refine(initial.Candidates, FilterKind.Changed, null, null, default).Candidates.Count);
        var lower = scanner.Refine(initial.Candidates, FilterKind.Decreased, null, null, default);
        Equal(fake.Base + 4, lower.Candidates.Single().Address);
        Equal("33030", lower.Candidates.Single().Value.ToString());
        var exact = scanner.Refine(initial.Candidates, FilterKind.Equals, MemoryValue.Parse("33040", ValueKind.Int32), null, default);
        Equal(fake.Base + 8, exact.Candidates.Single().Address);
    }),
    ("failed large reads recover accessible pages", () =>
    {
        var fake = new FakeMemory(16384) { FailLargeReads = true, UnreadablePage = 1 };
        fake.Set(4, 33032); fake.Set(8196, 33032);
        var result = new MemoryScanner(fake).FirstScan(MemoryValue.Parse("33032", ValueKind.Int32), null, default);
        Equal(2, result.Candidates.Count);
        Equal(4096L, result.SkippedBytes);
    }),
    ("partial reads recover the remainder without duplicates", () =>
    {
        var fake = new FakeMemory(16384) { PartialLargeReads = true };
        fake.Set(4, 33032); fake.Set(8196, 33032);
        var result = new MemoryScanner(fake).FirstScan(MemoryValue.Parse("33032", ValueKind.Int32), null, default);
        Equal(2, result.Candidates.Count);
        Equal(0L, result.SkippedBytes);
        Equal(16384L, result.BytesRead);
    }),
    ("unreadable candidates are removed on refine", () =>
    {
        var fake = new FakeMemory(8192);
        fake.Set(4, 33032); fake.Set(4100, 33032);
        var scanner = new MemoryScanner(fake);
        var before = scanner.FirstScan(MemoryValue.Parse("33032", ValueKind.Int32), null, default);
        fake.UnreadablePage = 1;
        var after = scanner.Refine(before.Candidates, FilterKind.Unchanged, null, null, default);
        Equal(1, after.Candidates.Count);
    }),
    ("candidate overflow never returns a misleading partial scan", () =>
    {
        var fake = new FakeMemory(4096);
        fake.Set(0, 1); fake.Set(4, 1);
        Throws<InvalidOperationException>(() => new MemoryScanner(fake, 1).FirstScan(MemoryValue.Parse("1", ValueKind.Int32), null, default));
    }),
    ("cancellation stops a scan", () =>
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Throws<OperationCanceledException>(() => new MemoryScanner(new FakeMemory(4096)).FirstScan(MemoryValue.Parse("1", ValueKind.Int32), null, cancel.Token));
    }),
    ("MEMORY_BASIC_INFORMATION x64 layout", () =>
    {
        Equal(8, IntPtr.Size);
        Equal(48, Marshal.SizeOf<ProcessMemory.MemoryBasicInformation>());
        Equal((nint)24, Marshal.OffsetOf<ProcessMemory.MemoryBasicInformation>("RegionSize"));
        Equal((nint)32, Marshal.OffsetOf<ProcessMemory.MemoryBasicInformation>("State"));
    }),
    ("real Windows API reads and writes only this test process buffer", () =>
    {
        var pointer = Marshal.AllocHGlobal(64);
        try
        {
            Marshal.WriteInt32(pointer, 33032);
            using var memory = new ProcessMemory(Environment.ProcessId);
            var address = (ulong)pointer;
            var before = memory.ReadValue(address, ValueKind.Int32);
            Equal("33032", before.ToString());
            memory.WriteValue(address, MemoryValue.Parse("44044", ValueKind.Int32), before);
            Equal(44044, Marshal.ReadInt32(pointer));
            var money = new KnownMoneyFeature(memory, address);
            Equal(45044, money.Set(45044));
            Equal(45044, Marshal.ReadInt32(pointer));
            Throws<ArgumentOutOfRangeException>(() => money.Set(1_000_001));
            Throws<ArgumentOutOfRangeException>(() => money.Set(-1));
            using var currentProcess = Process.GetCurrentProcess();
            True(KnownMoneyFeature.TryCreate(currentProcess, memory) is null);
            Throws<InvalidOperationException>(() => memory.WriteValue(address, before, before));
            memory.WriteValue(address + 17, before);
            Equal(33032, Marshal.ReadInt32(pointer + 17));
            Throws<ArgumentException>(() => memory.WriteValue(address, before, MemoryValue.Parse("33032", ValueKind.Float64)));
            using var readOnly = new ProcessMemory(Environment.ProcessId, writable: false);
            Throws<InvalidOperationException>(() => readOnly.WriteValue(address, before));
            True(memory.GetRegions(default).Any(r => address >= r.Base && address < r.Base + r.Size));
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }),
    ("external child process memory and exit handling", () =>
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(FakeMemory).Assembly.Location);
        info.ArgumentList.Add("--memory-fixture");
        using var child = Process.Start(info)!;
        try
        {
            var firstLine = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var address = ulong.Parse(firstLine!);
            using var memory = new ProcessMemory(child.Id);
            var original = memory.ReadValue(address, ValueKind.Int32);
            Equal("33032", original.ToString());
            memory.WriteValue(address, MemoryValue.Parse("33132", ValueKind.Int32), original);
            child.StandardInput.WriteLine("read"); child.StandardInput.Flush();
            Equal("33132", child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            child.StandardInput.WriteLine("quit"); child.StandardInput.Flush();
            True(child.WaitForExit(5000));
            True(!memory.IsAlive);
            Throws<InvalidOperationException>(() => memory.WriteValue(address, original));
        }
        finally { if (!child.HasExited) { child.StandardInput.Close(); child.WaitForExit(5000); } }
    })
};

tests.AddRange(FeatureTests.All);
var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed. No writes to the game were made by these tests.");
return failures == 0 ? 0 : 1;

static void True(bool value) { if (!value) throw new Exception("Expected true."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class FakeMemory(int size) : IMemoryReader
{
    public ulong Base => 0x10000;
    public byte[] Bytes { get; } = new byte[size];
    public bool FailLargeReads { get; set; }
    public bool PartialLargeReads { get; set; }
    public int UnreadablePage { get; set; } = -1;
    public void Set(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset, 4), value);
    public IReadOnlyList<MemoryRegion> GetRegions(CancellationToken token) { token.ThrowIfCancellationRequested(); return [new(Base, (ulong)Bytes.Length)]; }
    public int Read(ulong address, byte[] buffer, int count)
    {
        if (address < Base || address >= Base + (ulong)Bytes.Length || (FailLargeReads && count > 4096)) return 0;
        var offset = (int)(address - Base);
        if (offset / 4096 == UnreadablePage) return 0;
        count = Math.Min(count, Bytes.Length - offset);
        if (PartialLargeReads && count > 4096) count = 4096;
        Array.Copy(Bytes, offset, buffer, 0, count);
        return count;
    }
}
