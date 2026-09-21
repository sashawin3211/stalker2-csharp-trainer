using StalkerTrainer.Core;

namespace StalkerTrainer;

internal sealed record WeaponRuntime(uint Handle, ulong Model, ulong Cache);

internal sealed class WeaponAccuracy(IGameMemory memory, ulong moduleBase)
{
    // WeaponRuntimeData, returned by RVA 0x617992 as cache + 8. RVA 0x61159A
    // multiplies these base radii before applying buffs: normal/first shot and recoil.
    internal static readonly uint[] RadiusOffsets = [0x1F0, 0x1F4, 0x280];
    private readonly EquipmentLocator _equipment = new(memory, moduleBase);
    private WeaponRuntime? _current;
    private readonly Dictionary<uint, MemoryValue> _originals = [];

    internal WeaponRuntime? Resolve(uint handle)
    {
        var item = _equipment.ReadItem(handle, "В руках");
        if (item is null || memory.ReadValue(item.Model, ValueKind.Int64).Bits != moduleBase + GameProfile.WeaponModelVtable) return null;
        ulong cache = memory.ReadValue(item.Model + 0x90, ValueKind.Int64).Bits;
        if (cache < 0x10000 || cache >= 0x7FFFFFFF0000) throw new IOException("Данные стрельбы недоступны.");
        byte[] dirty = new byte[1];
        if (memory.Read(cache, dirty, 1) != 1) throw new IOException("Не удалось прочитать данные стрельбы.");
        if (dirty[0] != 0) return null; // The game must finish rebuilding its lazy cache first.
        return new(handle, item.Model, cache);
    }

    internal void Tick(uint handle)
    {
        var next = Resolve(handle);
        if (_current != next) Restore();
        if (next is null) return;
        var values = RadiusOffsets.Select(offset => memory.ReadValue(next.Cache + 8 + offset, ValueKind.Float32)).ToArray();
        foreach (var value in values)
        {
            float radius = BitConverter.UInt32BitsToSingle((uint)value.Bits);
            if (!float.IsFinite(radius) || radius < 0 || radius > 100_000) throw new IOException("Радиус стрельбы вне допустимого диапазона.");
        }
        if (Resolve(handle) != next) return;
        _current = next;
        for (int i = 0; i < RadiusOffsets.Length; i++)
        {
            uint offset = RadiusOffsets[i];
            var before = values[i];
            if (!_originals.ContainsKey(offset) || before.Bits != 0) _originals[offset] = before;
            if (before.Bits != 0) memory.WriteValue(next.Cache + 8 + offset, new(ValueKind.Float32, 0), before);
        }
    }

    internal void Restore()
    {
        if (_current is not { } saved) return;
        WeaponRuntime? live;
        try { live = Resolve(saved.Handle); }
        catch (IOException) { live = null; } // A deleted/recycled item must never receive an old value.
        if (live == saved)
            foreach (var (offset, original) in _originals)
            {
                var address = saved.Cache + 8 + offset;
                var now = memory.ReadValue(address, ValueKind.Float32);
                if (now.Bits == 0 && original.Bits != 0) memory.WriteValue(address, original, now);
            }
        _current = null;
        _originals.Clear();
    }
}
