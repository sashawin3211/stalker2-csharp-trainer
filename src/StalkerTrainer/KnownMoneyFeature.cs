using System.Diagnostics;
using System.Security.Cryptography;
using StalkerTrainer.Core;
using StalkerTrainer.Native;

namespace StalkerTrainer;

internal sealed class KnownMoneyFeature(ProcessMemory memory, ulong address)
{
    // Located in writable module data; current build's value matched 105000 in game.
    // Resolve against the actual process module base, never against a fixed virtual address.
    internal const ulong RelativeAddress = GameProfile.Money;
    internal const string ExecutableSha256 = GameProfile.ExecutableSha256;
    internal ulong Address => address;

    internal static KnownMoneyFeature? TryCreate(Process process, ProcessMemory memory)
    {
        var module = process.MainModule ?? throw new InvalidOperationException("Главный модуль игры недоступен.");
        using var executable = File.OpenRead(module.FileName);
        var hash = Convert.ToHexString(SHA256.HashData(executable));
        if (!string.Equals(hash, ExecutableSha256, StringComparison.OrdinalIgnoreCase)) return null;
        if (RelativeAddress + 4 > (ulong)module.ModuleMemorySize)
            throw new InvalidOperationException("Адрес денег выходит за границы модуля игры.");
        return new(memory, checked((ulong)module.BaseAddress + RelativeAddress));
    }

    internal int Read()
    {
        var value = memory.ReadValue(address, ValueKind.Int32);
        return unchecked((int)value.Bits);
    }

    internal int Set(int amount)
    {
        if (amount is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(amount), "Сумма: от 0 до 1 000 000.");
        var before = memory.ReadValue(address, ValueKind.Int32);
        if (unchecked((int)before.Bits) < 0) throw new InvalidOperationException("Текущее значение денег некорректно. Загрузи сохранение и подключись заново.");
        memory.WriteValue(address, new MemoryValue(ValueKind.Int32, (uint)amount), before);
        return Read();
    }
}
