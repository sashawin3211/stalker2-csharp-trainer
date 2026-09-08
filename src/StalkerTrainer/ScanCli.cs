using System.Text.Json;
using System.Diagnostics;
using StalkerTrainer.Core;
using StalkerTrainer.Native;

namespace StalkerTrainer;

internal sealed record ScanSnapshot(int ProcessId, long StartedUtcTicks, List<Candidate> Candidates);

internal static class ScanCli
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("--scan-money VALUE OUTPUT.json | --refine-money INPUT.json VALUE OUTPUT.json");
            using var process = InstallationInfo.FindRunning(InstallationInfo.DefaultRoot)
                ?? throw new InvalidOperationException("STALKER 2 process not found or its path is inaccessible.");
            var writeMode = args[0] is "--verify-money-write" or "--verify-player-flags" or "--test-features" or "--test-accuracy";
            using var memory = new ProcessMemory(process.Id, writable: writeMode);
            var scanner = new MemoryScanner(memory);
            var started = process.StartTime.ToUniversalTime().Ticks;
            if (args is ["--test-features" or "--test-accuracy", var secondsText])
            {
                int seconds = int.Parse(secondsText);
                if (seconds is < 1 or > 600) throw new ArgumentException("Duration must be 1..600 seconds.");
                _ = KnownMoneyFeature.TryCreate(process, memory) ?? throw new InvalidOperationException("Unsupported EXE.");
                ulong moduleBase = (ulong)process.MainModule!.BaseAddress;
                var features = new TrainerFeatures(memory, moduleBase);
                try
                {
                    bool accuracyOnly = args[0] == "--test-accuracy";
                    if (!accuracyOnly)
                        foreach (var feature in TrainerFeatures.Descriptions.Where(f => f.Id != FeatureId.NoReload)) features.Set(feature.Id, true);
                    Console.WriteLine($"{args[0]} active for {seconds}s. {features.Status}");
                    var watch = Stopwatch.StartNew();
                    int report = -1;
                    while (watch.Elapsed.TotalSeconds < seconds)
                    {
                        if (accuracyOnly && !features.IsEnabled(FeatureId.Accuracy))
                        {
                            var held = new EquipmentLocator(memory, moduleBase).HeldHandle(features.Player);
                            if (new WeaponAccuracy(memory, moduleBase).Resolve(held) is not null)
                            {
                                features.Set(FeatureId.Accuracy, true);
                                Console.WriteLine("Accuracy enabled; base shot and recoil radii set to zero.");
                            }
                        }
                        features.Tick();
                        int interval = (int)watch.Elapsed.TotalSeconds / 20;
                        if (interval != report)
                        {
                            report = interval;
                            Console.WriteLine($"{watch.Elapsed.TotalSeconds:F0}s {features.Status}; flags=0x{memory.ReadValue(features.Player.Address + 0x13C, ValueKind.Int32).Bits:X}; hunger={memory.ReadValue(features.Player.Address + 0x17C, ValueKind.Float32)}; radiation={memory.ReadValue(features.Player.Address + 0x16C, ValueKind.Float32)}");
                        }
                        Thread.Sleep(100);
                    }
                }
                finally
                {
                    features.StopAll();
                    Console.WriteLine("Test finished; flags and current weapon modifiers restored.");
                }
                return 0;
            }
            if (args is ["--inspect-accuracy"])
            {
                _ = KnownMoneyFeature.TryCreate(process, memory) ?? throw new InvalidOperationException("Unsupported EXE.");
                ulong moduleBase = (ulong)process.MainModule!.BaseAddress;
                var player = new PlayerLocator(memory, moduleBase).Resolve();
                var accuracy = new WeaponAccuracy(memory, moduleBase);
                foreach (var item in new EquipmentLocator(memory, moduleBase).ReadEquipped(player))
                {
                    Console.WriteLine($"{item.Slot}: modelVtable=0x{memory.ReadValue(item.Model, ValueKind.Int64).Bits - moduleBase:X}");
                    if (accuracy.Resolve(item.Handle) is not { } weapon) continue;
                    Console.WriteLine($"  handle=0x{weapon.Handle:X} cache=0x{weapon.Cache:X}");
                    foreach (uint offset in WeaponAccuracy.RadiusOffsets)
                        Console.WriteLine($"  radius +0x{offset:X}: {memory.ReadValue(weapon.Cache + 8 + offset, ValueKind.Float32)}");
                }
                return 0;
            }
            if (args is ["--inspect-effects"])
            {
                _ = KnownMoneyFeature.TryCreate(process, memory) ?? throw new InvalidOperationException("Unsupported EXE.");
                var player = new PlayerLocator(memory, (ulong)process.MainModule!.BaseAddress).Resolve();
                var modifiers = new ModifierLocator(memory);
                for (uint key = 0; key < 0xA0; key++)
                    if (modifiers.Find(player, key) is { } entry)
                        Console.WriteLine($"{key:X2}: {memory.ReadValue(entry, ValueKind.Float32)} @0x{entry:X}");
                return 0;
            }
            if (args is ["--inspect-equipment"])
            {
                _ = KnownMoneyFeature.TryCreate(process, memory) ?? throw new InvalidOperationException("Unsupported EXE.");
                ulong moduleBase = (ulong)process.MainModule!.BaseAddress;
                var player = new PlayerLocator(memory, moduleBase).Resolve();
                ulong actorTable = memory.ReadValue(player.Actor, ValueKind.Int64).Bits;
                ulong aimFunction = memory.ReadValue(actorTable + 0xAA0, ValueKind.Int64).Bits;
                Console.WriteLine($"Actor=0x{player.Actor:X}; VtableRva=0x{actorTable-moduleBase:X}; GetAimTransformRva=0x{aimFunction-moduleBase:X}");
                foreach (var item in new EquipmentLocator(memory, moduleBase).ReadEquipped(player))
                    Console.WriteLine($"{item.Slot}: handle=0x{item.Handle:X}; data=0x{item.Data:X}; model=0x{item.Model:X}; durability=0x{item.DurabilityAddress:X}; condition={item.Condition:P2}");
                return 0;
            }
            if (args is ["--verify-player-flags", var maskText, var expectedTextFlags])
            {
                _ = KnownMoneyFeature.TryCreate(process, memory) ?? throw new InvalidOperationException("Unsupported EXE.");
                var player = new PlayerLocator(memory, (ulong)process.MainModule!.BaseAddress).Resolve();
                uint mask = Convert.ToUInt32(maskText, 16);
                uint expected = Convert.ToUInt32(expectedTextFlags, 16);
                if ((mask & ~0x1Fu) != 0) throw new ArgumentException("Unsupported flags.");
                memory.WriteValue(player.Address + 0x13C, new(ValueKind.Int32, mask), new(ValueKind.Int32, expected));
                Console.WriteLine($"Player=0x{player.Address:X} Flags: {expected:X} -> {memory.ReadValue(player.Address + 0x13C, ValueKind.Int32).Bits:X}");
                return 0;
            }
            if (args is ["--inspect-player"])
            {
                _ = KnownMoneyFeature.TryCreate(process, memory) ?? throw new InvalidOperationException("Unsupported EXE.");
                ulong moduleBase = (ulong)process.MainModule!.BaseAddress;
                uint handle = (uint)memory.ReadValue(moduleBase + 0x9EDD140, ValueKind.Int32).Bits;
                uint index = handle & 0x7FFFFFF;
                ulong pool = moduleBase + 0xA3237F0;
                for (var n = 0; index >= 1024; n++)
                {
                    if (n >= 64) throw new InvalidOperationException("Invalid player handle.");
                    pool = memory.ReadValue(pool, ValueKind.Int64).Bits;
                    index -= 1024;
                }
                ulong address = pool + index * 0x700 + 0x10;
                Console.WriteLine($"PID={process.Id} Base=0x{moduleBase:X} Handle=0x{handle:X8} Index={index} PlayerData=0x{address:X}");
                byte[] bytes = new byte[0x700];
                if (memory.Read(address - 0x10, bytes, bytes.Length) != bytes.Length) throw new IOException("Cannot read player data.");
                Directory.CreateDirectory("artifacts");
                File.WriteAllBytes("artifacts/player-data.bin", bytes);
                for (int i = 0; i < 0x1E0; i += 4)
                    Console.WriteLine($"+{i - 0x10:X3}: {BitConverter.ToUInt32(bytes, i):X8} Int={BitConverter.ToInt32(bytes, i)} Float={BitConverter.ToSingle(bytes, i):G9}");
                ulong equipment = memory.ReadValue(address + 0x678, ValueKind.Int64).Bits;
                uint weaponHandle = (uint)memory.ReadValue(equipment + 0x110, ValueKind.Int32).Bits;
                uint weaponIndex = weaponHandle & 0x7FFFFFF;
                ulong itemPool = moduleBase + 0xA774850;
                if (weaponIndex >= 65536) throw new IOException("No held weapon.");
                while (weaponIndex >= 4096) { itemPool = memory.ReadValue(itemPool, ValueKind.Int64).Bits; weaponIndex -= 4096; }
                ulong weapon = itemPool + 0x10 + weaponIndex * 0x88;
                Console.WriteLine($"Equipment=0x{equipment:X}; WeaponHandle=0x{weaponHandle:X}; Weapon=0x{weapon:X}");
                byte[] itemBytes = new byte[0x88];
                memory.Read(weapon, itemBytes, itemBytes.Length);
                File.WriteAllBytes("artifacts/weapon-data.bin", itemBytes);
                byte[] equipBytes = new byte[0x400];
                memory.Read(equipment, equipBytes, equipBytes.Length);
                File.WriteAllBytes("artifacts/equipment-data.bin", equipBytes);
                ulong model = BitConverter.ToUInt64(itemBytes, 0x30);
                byte[] modelBytes = new byte[0x400];
                memory.Read(model, modelBytes, modelBytes.Length);
                File.WriteAllBytes("artifacts/weapon-model.bin", modelBytes);
                Console.WriteLine($"WeaponModel=0x{model:X}; ModelVtableRva=0x{BitConverter.ToUInt64(modelBytes, 0) - moduleBase:X}; DurabilityVtableRva=0x{BitConverter.ToUInt64(modelBytes, 0x28)-moduleBase:X}");
                for (int i = 0; i < 0x100; i += 4)
                    Console.WriteLine($"Model+{i:X3}: {BitConverter.ToUInt32(modelBytes, i):X8} Float={BitConverter.ToSingle(modelBytes, i):G9}");
                for (int i = 0; i < itemBytes.Length; i += 4)
                    Console.WriteLine($"Weapon+{i:X3}: {BitConverter.ToUInt32(itemBytes, i):X8} Int={BitConverter.ToInt32(itemBytes, i)} Float={BitConverter.ToSingle(itemBytes, i):G9}");
                return 0;
            }
            if (args is ["--check-money"])
            {
                var feature = KnownMoneyFeature.TryCreate(process, memory) ?? throw new InvalidOperationException("Executable does not match the money profile.");
                Console.WriteLine($"Money profile verified; PID={process.Id}; Address=0x{feature.Address:X}; Coupons={feature.Read()}");
                return 0;
            }
            ScanResult result;
            string output;
            if (args is ["--verify-money-write", var inputSnapshot, var expectedText, var newText])
            {
                var snapshot = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(inputSnapshot)) ?? throw new FormatException("Invalid scan file.");
                if (snapshot.ProcessId != process.Id || snapshot.StartedUtcTicks != started)
                    throw new InvalidOperationException("Game restarted. Old addresses cannot be reused.");
                var addresses = snapshot.Candidates.Select(c => c.Address).Distinct().ToArray();
                if (addresses.Length != 1) throw new InvalidOperationException("A single verified candidate address is required.");
                var expected = MemoryValue.Parse(expectedText, ValueKind.Int32);
                var desired = MemoryValue.Parse(newText, ValueKind.Int32);
                var newAmount = int.Parse(newText);
                if (newAmount < 0 || newAmount > 1_000_000) throw new ArgumentOutOfRangeException(nameof(newText));
                memory.WriteValue(addresses[0], desired, expected);
                var readback = memory.ReadValue(addresses[0], ValueKind.Int32);
                var module = process.MainModule!;
                Console.WriteLine($"Address=0x{addresses[0]:X}; Before={expected}; After={readback}; ModuleBase=0x{(ulong)module.BaseAddress:X}; Offset=0x{addresses[0] - (ulong)module.BaseAddress:X}");
                return 0;
            }
            else if (args is ["--scan-money", var value, var outPath])
            {
                result = scanner.FirstScanMany(Enum.GetValues<ValueKind>().Select(kind => MemoryValue.Parse(value, kind)).ToArray(), new ConsoleProgress(), default);
                output = outPath;
            }
            else if (args is ["--refine-money", var source, var current, var destination])
            {
                var snapshot = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(source)) ?? throw new FormatException("Invalid scan file.");
                if (snapshot.ProcessId != process.Id || snapshot.StartedUtcTicks != started)
                    throw new InvalidOperationException("Game restarted. Old addresses cannot be reused. Start a new scan.");
                var combined = new List<Candidate>();
                long read = 0, skipped = 0;
                foreach (var group in snapshot.Candidates.GroupBy(c => c.Value.Kind))
                {
                    var refined = scanner.Refine(group.ToList(), FilterKind.Equals, MemoryValue.Parse(current, group.Key), null, default);
                    combined.AddRange(refined.Candidates);
                    read += refined.BytesRead;
                    skipped += refined.SkippedBytes;
                }
                result = new(combined, read, skipped);
                output = destination;
            }
            else throw new ArgumentException("--scan-money VALUE OUTPUT.json | --refine-money INPUT.json VALUE OUTPUT.json");
            File.WriteAllText(Path.GetFullPath(output), JsonSerializer.Serialize(new ScanSnapshot(process.Id, started, result.Candidates), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PID={process.Id}; Matches={result.Candidates.Count}; Read={result.BytesRead}; Skipped={result.SkippedBytes}; Output={Path.GetFullPath(output)}");
            foreach (var group in result.Candidates.GroupBy(c => c.Value.Kind)) Console.WriteLine($"{group.Key}: {group.Count()}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

internal sealed class ConsoleProgress : IProgress<ScanProgress>
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public void Report(ScanProgress progress)
    {
        if (_watch.ElapsedMilliseconds < 15000) return;
        Console.WriteLine($"Read {progress.BytesRead / 1048576} MiB; candidates {progress.Matches}; regions {progress.RegionsDone}/{progress.RegionsTotal}");
        _watch.Restart();
    }
}
