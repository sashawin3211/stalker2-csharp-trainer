using StalkerTrainer.Core;

namespace StalkerTrainer;

internal sealed record GrenadeStack(uint Handle, ulong Address, ulong Owner, ulong Model, uint Prototype, int Count);

internal sealed class GrenadeInventory(IGameMemory memory, ulong moduleBase)
{
    internal const ulong PoolRva = GameProfile.ItemPool;
    internal const ulong ModelVtableRva = GameProfile.GrenadeModelVtable;
    private const uint ChunkSize = 4096, ItemSize = 0x88, MaxChunks = 64;
    private static bool Pointer(ulong p) => p >= 0x10000 && p < 0x7FFFFFFF0000;

    internal GrenadeStack? Read(PlayerLocation player, uint handle)
    {
        uint index = handle & 0x7FFFFFF;
        if (handle >= 0xFFFFFFF0 || index >= ChunkSize * MaxChunks) return null;
        ulong pool = moduleBase + PoolRva;
        while (index >= ChunkSize)
        {
            pool = memory.ReadValue(pool, ValueKind.Int64).Bits;
            if (!Pointer(pool)) return null;
            index -= ChunkSize;
        }
        ulong address = pool + 0x10 + index * ItemSize;
        byte[] bytes = new byte[ItemSize];
        if (memory.Read(address, bytes, bytes.Length) != bytes.Length) return null;
        if (BitConverter.ToUInt32(bytes, 8) != handle) return null;
        ulong owner = BitConverter.ToUInt64(bytes, 0x20), model = BitConverter.ToUInt64(bytes, 0x30);
        int count = BitConverter.ToInt32(bytes, 0x44);
        if (count <= 0 || count > 10_000 || !Pointer(owner) || !Pointer(model)) return null;
        if (memory.ReadValue(owner + 0x68, ValueKind.Int32).Bits != player.Handle ||
            memory.ReadValue(model, ValueKind.Int64).Bits != moduleBase + ModelVtableRva ||
            memory.ReadValue(model + 0x18, ValueKind.Int64).Bits != address) return null;
        return new(handle, address, owner, model, BitConverter.ToUInt32(bytes, 0x38), count);
    }

    internal List<GrenadeStack> Find(PlayerLocation player)
    {
        var result = new List<GrenadeStack>();
        var owners = new Dictionary<ulong, bool>();
        var visited = new HashSet<ulong>();
        ulong pool = moduleBase + PoolRva;
        for (uint chunk = 0; chunk < MaxChunks && Pointer(pool); chunk++)
        {
            if (!visited.Add(pool)) throw new IOException("Повреждена цепочка инвентаря.");
            byte[] bytes = new byte[ChunkSize * ItemSize];
            if (memory.Read(pool + 0x10, bytes, bytes.Length) != bytes.Length) throw new IOException("Инвентарь временно недоступен.");
            for (uint index = 0; index < ChunkSize; index++)
            {
                int p = (int)(index * ItemSize);
                uint handle = BitConverter.ToUInt32(bytes, p + 8);
                ulong owner = BitConverter.ToUInt64(bytes, p + 0x20);
                if ((handle & 0x7FFFFFF) != chunk * ChunkSize + index || !Pointer(owner) || BitConverter.ToInt32(bytes, p + 0x44) <= 0) continue;
                if (!owners.TryGetValue(owner, out bool owned))
                    owners[owner] = owned = memory.ReadValue(owner + 0x68, ValueKind.Int32).Bits == player.Handle;
                if (owned && Read(player, handle) is { } grenade) result.Add(grenade);
            }
            pool = memory.ReadValue(pool, ValueKind.Int64).Bits;
        }
        return result;
    }

    internal void Refill(PlayerLocation player, GrenadeStack expected, int target)
    {
        if (target is < 2 or > 10_000) throw new ArgumentOutOfRangeException(nameof(target));
        // Recheck ownership, generation, model and count immediately before the write.
        if (Read(player, expected.Handle) != expected) return;
        memory.WriteValue(expected.Address + 0x44, new(ValueKind.Int32, (uint)target), new(ValueKind.Int32, (uint)expected.Count));
    }
}

internal sealed class InfiniteGrenades(GrenadeInventory inventory)
{
    private readonly Dictionary<uint, GrenadeStack> _stacks = [];
    private int _untilScan;
    internal int ProtectedStacks => _stacks.Count;

    internal void Start(PlayerLocation player)
    {
        var found = inventory.Find(player);
        if (found.Count == 0) throw new InvalidOperationException("Для включения нужна хотя бы одна граната в инвентаре.");
        _stacks.Clear();
        foreach (var item in found) _stacks[item.Handle] = item with { Count = Math.Max(2, item.Count) };
        _untilScan = 20;
        Tick(player);
    }

    internal void Tick(PlayerLocation player)
    {
        if (--_untilScan <= 0)
        {
            foreach (var item in inventory.Find(player))
                if (!_stacks.ContainsKey(item.Handle)) _stacks[item.Handle] = item with { Count = Math.Max(2, item.Count) };
            _untilScan = 20;
        }
        foreach (uint handle in _stacks.Keys.ToArray())
        {
            var before = inventory.Read(player, handle);
            if (before is null) { _stacks.Remove(handle); continue; }
            var saved = _stacks[handle];
            if (before with { Count = saved.Count } != saved || before.Count > saved.Count)
                _stacks[handle] = saved = before with { Count = Math.Max(2, before.Count) };
            if (before.Count < saved.Count) inventory.Refill(player, before, saved.Count);
        }
    }

    internal void Stop() { _stacks.Clear(); _untilScan = 0; }
}
