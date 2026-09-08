using StalkerTrainer.Core;
using StalkerTrainer.Native;

namespace StalkerTrainer;

internal sealed class ModifierLocator(IGameMemory memory)
{
    internal ulong? Find(PlayerLocation player, uint key)
    {
        ulong stats = memory.ReadValue(player.Address + 0x668, ValueKind.Int64).Bits;
        if (stats < 0x10000) throw new IOException("Модификаторы игрока недоступны.");
        uint count = (uint)memory.ReadValue(stats + 0x68, ValueKind.Int32).Bits;
        if (count > 8192) throw new IOException("Некорректный размер таблицы модификаторов.");
        if (count == memory.ReadValue(stats + 0x94, ValueKind.Int32).Bits) return null;
        ulong buckets = memory.ReadValue(stats + 0xA0, ValueKind.Int64).Bits;
        if (buckets == 0) buckets = stats + 0x98;
        uint size = (uint)memory.ReadValue(stats + 0xA8, ValueKind.Int32).Bits;
        if (size == 0 || size > 16384 || (size & (size - 1)) != 0) throw new IOException("Некорректная хеш-таблица модификаторов.");
        uint index = (uint)memory.ReadValue(buckets + (key & (size - 1)) * 4, ValueKind.Int32).Bits;
        ulong entries = memory.ReadValue(stats + 0x60, ValueKind.Int64).Bits;
        for (int n = 0; index != uint.MaxValue; n++)
        {
            if (index >= count || n >= 8192) throw new IOException("Повреждена цепочка модификаторов.");
            ulong entry = entries + index * 16;
            if (memory.ReadValue(entry, ValueKind.Int32).Bits == key) return entry + 4;
            index = (uint)memory.ReadValue(entry + 8, ValueKind.Int32).Bits;
        }
        return null;
    }
}
