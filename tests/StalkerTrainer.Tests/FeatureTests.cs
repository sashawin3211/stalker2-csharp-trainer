using StalkerTrainer;
using StalkerTrainer.Core;

internal static class FeatureTests
{
    internal static IEnumerable<(string Name, Action Run)> All =>
    [
        ("feature flags restore original bits and preserve unrelated flags", () =>
        {
            var m = new GameFixture(); m.U32(m.Player + 0x13C, 2);
            var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.God, true); f.Set(FeatureId.NoReload, true); f.Set(FeatureId.InfiniteAmmo, true);
            Check(m.UInt(m.Player + 0x13C) == 15);
            m.U32(m.Player + 0x13C, 47);
            f.StopAll(); Check(m.UInt(m.Player + 0x13C) == 34);
        }),
        ("accuracy restores current weapon stats after cache recomputation", () =>
        {
            var m = new GameFixture(); m.AddWeapon(); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.Accuracy, true);
            Check(WeaponAccuracy.RadiusOffsets.All(o => m.Number(m.Cache + 8 + o) == 0));
            Check(m.Number(m.Modifier(0x27)) == 189); // The unrelated summary map is left alone.
            m.Float(m.Cache + 8 + 0x1F0, 80); f.Tick(); f.StopAll();
            Check(m.Number(m.Cache + 8 + 0x1F0) == 80 && m.Number(m.Cache + 8 + 0x280) == 300);
        }),
        ("holstering restores weapon radii and stops holding them", () =>
        {
            var m = new GameFixture(); m.AddWeapon(); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.Accuracy, true);
            m.U32(m.Equipment + 0x110, uint.MaxValue); f.Tick();
            Check(m.Number(m.Cache + 8 + 0x1F0) == 189);
            m.Float(m.Cache + 8 + 0x1F0, 42); f.StopAll(); Check(m.Number(m.Cache + 8 + 0x1F0) == 42);
        }),
        ("accuracy never restores values into a recycled weapon model", () =>
        {
            var m = new GameFixture(); m.AddWeapon(); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.Accuracy, true); m.U32(m.Item + 8, 99);
            f.StopAll(); Check(m.Number(m.Cache + 8 + 0x1F0) == 0);
        }),
        ("accuracy waits for dirty cache and captures rebuilt values", () =>
        {
            var m = new GameFixture(); m.AddWeapon(); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.Accuracy, true); m.U32(m.Cache, 1); m.Float(m.Cache + 8 + 0x1F0, 42);
            f.Tick(); Check(m.Number(m.Cache + 8 + 0x1F0) == 42);
            m.U32(m.Cache, 0); f.Tick(); Check(m.Number(m.Cache + 8 + 0x1F0) == 0);
            f.StopAll(); Check(m.Number(m.Cache + 8 + 0x1F0) == 42);
        }),
        ("invalid recoil radius blocks all accuracy writes", () =>
        {
            var m = new GameFixture(); m.AddWeapon(); var f = new TrainerFeatures(m, m.Base);
            m.Float(m.Cache + 8 + 0x280, float.NaN);
            int writes = m.Writes; Reject(() => f.Set(FeatureId.Accuracy, true)); Check(m.Writes == writes);
        }),
        ("reload blocks stale player writes and restoration", () =>
        {
            var m = new GameFixture(); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.God, true); m.U32(m.ActorEntry + 0x10, 8);
            int writes = m.Writes;
            Reject(f.Tick); Reject(f.StopAll); Check(m.Writes == writes);
        }),
        ("vitals clear conditions and respect changed maximum stamina", () =>
        {
            var m = new GameFixture(); var f = new TrainerFeatures(m, m.Base);
            m.Float(m.Player + 0x17C, 48); m.Float(m.Player + 0x16C, 25); m.Float(m.Player + 0x154, 140);
            f.Set(FeatureId.NoHunger, true); f.Set(FeatureId.NoRadiation, true); f.Set(FeatureId.Stamina, true);
            Check(m.Number(m.Player + 0x17C) == 0 && m.Number(m.Player + 0x16C) == 0 && m.Number(m.Player + 0x150) == 140);
            f.StopAll(); m.Float(m.Player + 0x150, 99); f.Tick(); Check(m.Number(m.Player + 0x150) == 99);
        }),
        ("corrupt modifier hash chain is rejected before writes", () =>
        {
            var m = new GameFixture(); var f = new TrainerFeatures(m, m.Base);
            m.U32(m.Buckets + 0x27 * 4, 999);
            int writes = m.Writes; Reject(() => new ModifierLocator(m).Find(f.Player, 0x27)); Check(m.Writes == writes);
        }),
        ("durability protects equipment but rejects recycled item identities", () =>
        {
            var m = new GameFixture(); m.AddWeapon(); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.Durability, true);
            m.Float(m.Model + 0x98, 0.7f); f.Tick(); Check(m.Number(m.Model + 0x98) == 0.9f);
            m.U32(m.Item + 8, 99); m.Float(m.Model + 0x98, 0.6f);
            Reject(f.Tick); Check(m.Number(m.Model + 0x98) == 0.6f);
        }),
        ("equipment removal never writes a cached durability address", () =>
        {
            var m = new GameFixture(); m.AddWeapon(); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.Durability, true);
            m.U32(m.Equipment + 0x110, uint.MaxValue); m.Float(m.Model + 0x98, 0.5f);
            f.Tick(); Check(m.Number(m.Model + 0x98) == 0.5f); f.StopAll();
        })
    ];
    private static void Check(bool ok) { if (!ok) throw new Exception("Feature assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected invalid game state to be rejected."); }
}

internal sealed class GameFixture : IGameMemory
{
    private readonly Dictionary<ulong, byte> _bytes = [];
    private readonly uint[] _keys = [0x27, 0x4F, 0x84, 0x85, 0x7E];
    public ulong Base => 0x140000000;
    public ulong Player => Base + PlayerLocator.PoolRva + 0x10;
    public ulong Equipment => 0x300000;
    public ulong ActorEntry => 0x210000 + 5 * 24;
    public ulong Buckets => 0x600000;
    public ulong Item => Base + GameProfile.ItemPool + 0x10 + 42 * 0x88;
    public ulong Model => 0x700000;
    public ulong Cache => 0x800000;
    public int Writes { get; private set; }
    public GameFixture()
    {
        U32(Base + PlayerLocator.HandleRva, 0); U64(Player, Base + GameProfile.PlayerVtable); U32(Player + 0x10, 0);
        U64(Player + 0x50, (7UL << 32) | 5); U32(Base + GameProfile.ObjectCount, 100);
        U64(Base + GameProfile.ObjectChunks, 0x200000); U64(0x200000, 0x210000);
        U64(ActorEntry, 0x100000); U32(ActorEntry + 8, 0); U32(ActorEntry + 0x10, 7); U64(0x100650, Player);
        Float(Player + 0x14C, 100); Float(Player + 0x154, 100); U32(Player + 0x13C, 0);
        U64(Player + 0x678, Equipment); U64(Player + 0x668, 0x400000);
        foreach (uint offset in new uint[] { 0x110, 0x1F0, 0x208, 0x220, 0x250, 0x268 }) U32(Equipment + offset, uint.MaxValue);
        U32(0x400068, (uint)_keys.Length); U32(0x400094, 0); U64(0x400060, 0x500000); U64(0x4000A0, Buckets); U32(0x4000A8, 256);
        for (uint i = 0; i < 256; i++) U32(Buckets + i * 4, uint.MaxValue);
        for (int i = 0; i < _keys.Length; i++)
        {
            uint key = _keys[i]; U32(Buckets + key * 4, (uint)i);
            U32(0x500000 + (ulong)i * 16, key); Float(Modifier(key), key == 0x27 ? 189 : key == 0x4F ? 300 : 1);
            U32(0x500008 + (ulong)i * 16, uint.MaxValue);
        }
    }
    public void AddWeapon()
    {
        U32(Equipment + 0x110, 42); U32(Item + 8, 42); U64(Item + 0x30, Model); U64(Model + 0x18, Item);
        U64(Model + 0x28, Base + 0x8EA6E20); U64(Base + 0x8EA6E28, Base + 0x20D027A);
        Put(Base + 0x20D027A, [0xF3, 0x0F, 0x10, 0x41, 0x70, 0xC3]); Float(Model + 0x98, 0.9f);
        U64(Model, Base + GameProfile.WeaponModelVtable); U64(Model + 0x90, Cache); U32(Cache, 0);
        Float(Cache + 8 + 0x1F0, 189); Float(Cache + 8 + 0x1F4, 100); Float(Cache + 8 + 0x280, 300);
    }
    public ulong Modifier(uint key) => 0x500004 + (ulong)Array.IndexOf(_keys, key) * 16;
    public void U32(ulong p, uint n) => Put(p, BitConverter.GetBytes(n));
    public void U64(ulong p, ulong n) => Put(p, BitConverter.GetBytes(n));
    public void Float(ulong p, float n) => Put(p, BitConverter.GetBytes(n));
    public uint UInt(ulong p) => (uint)ReadValue(p, ValueKind.Int32).Bits;
    public float Number(ulong p) => BitConverter.UInt32BitsToSingle(UInt(p));
    private void Put(ulong p, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) _bytes[p + (ulong)i] = bytes[i]; }
    public int Read(ulong p, byte[] buffer, int count) { for (int i = 0; i < count; i++) buffer[i] = _bytes.GetValueOrDefault(p + (ulong)i); return count; }
    public MemoryValue ReadValue(ulong p, ValueKind kind) { byte[] bytes = new byte[MemoryValue.SizeOf(kind)]; Read(p, bytes, bytes.Length); return MemoryValue.Read(bytes, kind); }
    public void WriteValue(ulong p, MemoryValue value, MemoryValue? expected = null)
    {
        if (expected is { } old && ReadValue(p, old.Kind) != old) throw new InvalidOperationException("Value changed.");
        Writes++; Put(p, value.ToBytes());
    }
    public IReadOnlyList<MemoryRegion> GetRegions(CancellationToken token) => throw new NotSupportedException();
}
