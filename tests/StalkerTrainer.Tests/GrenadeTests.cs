using StalkerTrainer;
using StalkerTrainer.Core;

internal static class GrenadeTests
{
    internal static IEnumerable<(string Name, Action Run)> All =>
    [
        ("grenades refill consumption, retain pickups and stop on disable", () =>
        {
            var m = new GameFixture(); var item = Add(m, 4); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.InfiniteGrenades, true);
            m.U32(item + 0x44, 3); f.Tick(); Check(m.UInt(item + 0x44) == 4);
            m.U32(item + 0x44, 7); f.Tick(); m.U32(item + 0x44, 6); f.Tick(); Check(m.UInt(item + 0x44) == 7);
            f.Set(FeatureId.InfiniteGrenades, false); m.U32(item + 0x44, 6); f.Tick(); Check(m.UInt(item + 0x44) == 6);
        }),
        ("single grenade gets a reserve without changing other consumables", () =>
        {
            var m = new GameFixture(); var item = Add(m, 1); var other = Add(m, 5, 44);
            ulong model = m.ReadValue(other + 0x30, ValueKind.Int64).Bits; m.U64(model, m.Base + 0x8EA08F0);
            var f = new TrainerFeatures(m, m.Base); f.Set(FeatureId.InfiniteGrenades, true);
            Check(m.UInt(item + 0x44) == 2 && m.UInt(other + 0x44) == 5); f.StopAll();
            Check(m.UInt(item + 0x44) == 2);
        }),
        ("grenades transferred to another owner are never refilled", () =>
        {
            var m = new GameFixture(); var item = Add(m, 4); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.InfiniteGrenades, true); m.U64(item + 0x20, 0x960000); m.U32(0x960068, 99); m.U32(item + 0x44, 3);
            f.Tick(); Check(m.UInt(item + 0x44) == 3);
            Check(new GrenadeInventory(m, m.Base).Find(f.Player).Count == 0);
        }),
        ("recycled grenade handles do not inherit old stack quantities", () =>
        {
            var m = new GameFixture(); var item = Add(m, 7); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.InfiniteGrenades, true); m.U32(item + 8, 0x3800002B); m.U32(item + 0x44, 2);
            for (int i = 0; i < 21; i++) f.Tick(); Check(m.UInt(item + 0x44) == 2);
        }),
        ("empty grenade stacks are not resurrected", () =>
        {
            var m = new GameFixture(); var item = Add(m, 4); var f = new TrainerFeatures(m, m.Base);
            f.Set(FeatureId.InfiniteGrenades, true); m.U32(item + 0x44, 0); f.Tick(); Check(m.UInt(item + 0x44) == 0);
        }),
        ("grenade ownership is rechecked at the write boundary", () =>
        {
            var m = new GameFixture(); var item = Add(m, 3); var f = new TrainerFeatures(m, m.Base);
            var inventory = new GrenadeInventory(m, m.Base); var expected = inventory.Find(f.Player).Single();
            m.U32(0x950068, 99); int writes = m.Writes; inventory.Refill(f.Player, expected, 4); Check(m.Writes == writes);
        }),
        ("grenade discovery handles linked chunks and rejects cycles", () =>
        {
            var m = new GameFixture(); var item = Add(m, 3, 4200); var f = new TrainerFeatures(m, m.Base);
            var inventory = new GrenadeInventory(m, m.Base); Check(inventory.Find(f.Player).Single().Address == item);
            m.U64(0xB00000, m.Base + GrenadeInventory.PoolRva);
            try { inventory.Find(f.Player); } catch (IOException) { return; }
            throw new Exception("Expected cyclic item pool rejection.");
        }),
        ("grenades require an existing stack and reject a stale player", () =>
        {
            var m = new GameFixture(); var f = new TrainerFeatures(m, m.Base);
            bool rejected = false;
            try { f.Set(FeatureId.InfiniteGrenades, true); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !f.IsEnabled(FeatureId.InfiniteGrenades) && m.Writes == 0);
            var item = Add(m, 3); f.Set(FeatureId.InfiniteGrenades, true); m.U32(item + 0x44, 2); m.U32(m.ActorEntry + 0x10, 8);
            try { f.Tick(); } catch (IOException) { Check(m.UInt(item + 0x44) == 2); return; }
            throw new Exception("Expected stale player rejection.");
        })
    ];

    private static ulong Add(GameFixture m, uint count, uint index = 43)
    {
        ulong pool = m.Base + GrenadeInventory.PoolRva;
        if (index >= 4096) { m.U64(pool, 0xB00000); pool = 0xB00000; }
        ulong item = pool + 0x10 + (index % 4096) * 0x88;
        ulong model = 0x900000 + index * 0x100;
        m.U32(item + 8, 0x30000000 | index); m.U64(item + 0x20, 0x950000); m.U32(0x950068, 0);
        m.U64(item + 0x30, model); m.U64(model, m.Base + GrenadeInventory.ModelVtableRva); m.U64(model + 0x18, item);
        m.U32(item + 0x38, 0x11BE); m.U32(item + 0x44, count);
        return item;
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Grenade assertion failed."); }
}
