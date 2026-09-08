namespace StalkerTrainer.Core;

internal interface IGameMemory : IMemoryReader
{
    MemoryValue ReadValue(ulong address, ValueKind kind);
    void WriteValue(ulong address, MemoryValue value, MemoryValue? expected = null);
}
