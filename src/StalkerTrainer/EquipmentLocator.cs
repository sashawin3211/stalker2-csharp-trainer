using StalkerTrainer.Core;
using StalkerTrainer.Native;

namespace StalkerTrainer;

internal sealed record EquipmentItem(string Slot, uint Handle, ulong Data, ulong Model, ulong DurabilityAddress, float Condition);

internal sealed class EquipmentLocator(IGameMemory memory, ulong moduleBase)
{
    internal EquipmentItem? ReadItem(uint handle, string slot)
    {
        if (handle >= 0xFFFFFFF0) return null;
        uint index = handle & 0x7FFFFFF;
        if (index >= 262144) throw new IOException("Некорректный индекс предмета.");
        ulong pool = moduleBase + 0xA774850;
        while (index >= 4096)
        {
            pool = memory.ReadValue(pool, ValueKind.Int64).Bits;
            if (pool < 0x10000 || pool >= 0x7FFFFFFF0000) throw new IOException("Пул предметов недоступен.");
            index -= 4096;
        }
        ulong item = pool + 0x10 + index * 0x88;
        if (memory.ReadValue(item + 8, ValueKind.Int32).Bits != handle) throw new IOException("Предмет сменился.");
        ulong model = memory.ReadValue(item + 0x30, ValueKind.Int64).Bits;
        if (model < 0x10000) return null;
        if (memory.ReadValue(model + 0x18, ValueKind.Int64).Bits != item) throw new IOException("Связь предмета с моделью не совпала.");
        ulong table = memory.ReadValue(model + 0x28, ValueKind.Int64).Bits;
        if (table < moduleBase + 0x7CC4000 || table >= moduleBase + 0x9E89000) return null;
        ulong getter = memory.ReadValue(table + 8, ValueKind.Int64).Bits;
        if (getter < moduleBase + 0x1000 || getter >= moduleBase + 0x7CC4000) return null;
        byte[] code = new byte[6];
        if (memory.Read(getter, code, code.Length) != code.Length ||
            code[0] != 0xF3 || code[1] != 0x0F || code[2] != 0x10 || code[3] != 0x41 || code[5] != 0xC3 || code[4] >= 0x80)
            return null;
        ulong address = model + 0x28 + code[4];
        float condition = BitConverter.UInt32BitsToSingle((uint)memory.ReadValue(address, ValueKind.Float32).Bits);
        if (!float.IsFinite(condition) || condition < 0 || condition > 1) throw new IOException("Прочность предмета вне допустимого диапазона.");
        return new(slot, handle, item, model, address, condition);
    }

    internal List<EquipmentItem> ReadEquipped(PlayerLocation player)
    {
        var result = new List<EquipmentItem>();
        ulong equipment = memory.ReadValue(player.Address + 0x678, ValueKind.Int64).Bits;
        if (equipment < 0x10000) return result;
        foreach (var (offset, name) in new (ulong, string)[] { (0x110, "В руках"), (0x1F0, "Основное оружие"), (0x208, "Второе оружие"), (0x220, "Пистолет"), (0x250, "Шлем"), (0x268, "Броня") })
        {
            uint handle = (uint)memory.ReadValue(equipment + offset, ValueKind.Int32).Bits;
            if (result.Any(i => i.Handle == handle)) continue;
            var item = ReadItem(handle, name);
            if (item is not null) result.Add(item);
        }
        return result;
    }

    internal uint HeldHandle(PlayerLocation player)
    {
        ulong equipment = memory.ReadValue(player.Address + 0x678, ValueKind.Int64).Bits;
        return (uint)memory.ReadValue(equipment + 0x110, ValueKind.Int32).Bits;
    }
}
