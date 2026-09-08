namespace StalkerTrainer.Core;

public sealed class WatchEntry
{
    public required string Name { get; init; }
    public required ulong Address { get; init; }
    public required MemoryValue Desired { get; set; }
    public MemoryValue? Current { get; set; }
    public bool Frozen { get; set; }
    public string Error { get; set; } = "";
}
