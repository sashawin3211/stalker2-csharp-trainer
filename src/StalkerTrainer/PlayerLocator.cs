using StalkerTrainer.Core;
using StalkerTrainer.Native;

namespace StalkerTrainer;

internal sealed record PlayerLocation(ulong Address, uint Handle, ulong ActorIdentity, ulong Actor);

internal sealed class PlayerLocator(IGameMemory memory, ulong moduleBase)
{
    internal const ulong HandleRva = GameProfile.PlayerHandle;
    internal const ulong PoolRva = GameProfile.PlayerPool;
    internal PlayerLocation Resolve()
    {
        uint handle = (uint)memory.ReadValue(moduleBase + HandleRva, ValueKind.Int32).Bits;
        uint index = handle & 0x7FFFFFF;
        if (handle == uint.MaxValue || index >= 65536) throw new InvalidOperationException("Загрузи сохранение: персонаж недоступен.");
        ulong pool = moduleBase + PoolRva;
        while (index >= 1024)
        {
            pool = memory.ReadValue(pool, ValueKind.Int64).Bits;
            if (pool < 0x10000 || pool >= 0x7FFFFFFF0000) throw new IOException("Пул персонажа недоступен.");
            index -= 1024;
        }
        ulong address = pool + index * 0x700 + 0x10;
        if (memory.ReadValue(address, ValueKind.Int64).Bits != moduleBase + GameProfile.PlayerVtable ||
            memory.ReadValue(address + 0x10, ValueKind.Int32).Bits != handle)
            throw new IOException("Структура персонажа не совпала с профилем.");
        ulong actorIdentity = memory.ReadValue(address + 0x50, ValueKind.Int64).Bits;
        if ((actorIdentity >> 32) == 0) throw new IOException("Персонаж ещё не создан.");
        uint actorIndex = (uint)actorIdentity;
        uint objectCount = (uint)memory.ReadValue(moduleBase + GameProfile.ObjectCount, ValueKind.Int32).Bits;
        if (actorIndex >= objectCount) throw new IOException("Персонаж уничтожен.");
        ulong chunks = memory.ReadValue(moduleBase + GameProfile.ObjectChunks, ValueKind.Int64).Bits;
        ulong chunk = memory.ReadValue(chunks + (actorIndex >> 16) * 8, ValueKind.Int64).Bits;
        ulong entry = chunk + (actorIndex & 0xFFFF) * 24;
        if (memory.ReadValue(entry + 0x10, ValueKind.Int32).Bits != actorIdentity >> 32 ||
            (memory.ReadValue(entry + 8, ValueKind.Int32).Bits & 0x10200000) != 0)
            throw new IOException("Объект персонажа недоступен.");
        ulong actor = memory.ReadValue(entry, ValueKind.Int64).Bits;
        if (actor < 0x10000 || memory.ReadValue(actor + 0x650, ValueKind.Int64).Bits != address)
            throw new IOException("Объект персонажа не соответствует данным.");
        float maxHealth = BitConverter.UInt32BitsToSingle((uint)memory.ReadValue(address + 0x14C, ValueKind.Float32).Bits);
        if (!float.IsFinite(maxHealth) || maxHealth <= 0 || maxHealth > 1_000_000) throw new IOException("Параметры персонажа недоступны.");
        if (memory.ReadValue(moduleBase + HandleRva, ValueKind.Int32).Bits != handle) throw new IOException("Персонаж сменился. Подключись заново.");
        return new(address, handle, actorIdentity, actor);
    }
}
