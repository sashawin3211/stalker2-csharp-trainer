using StalkerTrainer.Core;

namespace StalkerTrainer;

internal enum FeatureId { God, InfiniteAmmo, NoReload, Durability, Accuracy, Stamina, NoHunger, NoRadiation, NoBleeding, NoDrowsiness }
internal sealed record FeatureDescription(FeatureId Id, string Name, string Detail);

internal sealed class TrainerFeatures
{
    internal static readonly FeatureDescription[] Descriptions =
    [
        new(FeatureId.God, "Бессмертие", "Штатный флаг неуязвимости персонажа"),
        new(FeatureId.InfiniteAmmo, "Бесконечные патроны", "Боезапас не расходуется при перезарядке"),
        new(FeatureId.NoReload, "Без перезарядки", "Патроны в магазине не расходуются"),
        new(FeatureId.Durability, "Без износа снаряжения", "Оружие, шлем и броня; без заклинивания"),
        new(FeatureId.Accuracy, "Суперточность", "Обнуление разброса и отдачи в параметрах стрельбы"),
        new(FeatureId.Stamina, "Бесконечная выносливость", "Восстановление до текущего максимума"),
        new(FeatureId.NoHunger, "Без голода", "Удержание голода на нуле"),
        new(FeatureId.NoRadiation, "Без радиации", "Удаление накопленной радиации"),
        new(FeatureId.NoBleeding, "Без кровотечения", "Удержание кровотечения на нуле"),
        new(FeatureId.NoDrowsiness, "Без сонливости", "Удержание сонливости на нуле")
    ];

    private readonly IGameMemory _memory;
    private readonly PlayerLocator _players;
    private readonly EquipmentLocator _equipment;
    private readonly ModifierLocator _modifiers;
    private readonly WeaponAccuracy _accuracy;
    private readonly PlayerLocation _player;
    private readonly HashSet<FeatureId> _enabled = [];
    private readonly Dictionary<FeatureId, uint> _originalFlags = [];
    private readonly Dictionary<uint, (ulong Model, float Value)> _durability = [];
    private readonly Dictionary<uint, (ulong Address, MemoryValue Original)> _overrides = [];
    private uint _weapon;
    private int _weaponDelay;
    internal string Status { get; private set; } = "Все функции выключены";
    internal bool HasEnabled => _enabled.Count != 0;
    internal bool IsEnabled(FeatureId id) => _enabled.Contains(id);
    internal PlayerLocation Player => _player;

    internal TrainerFeatures(IGameMemory memory, ulong moduleBase)
    {
        _memory = memory;
        _players = new(memory, moduleBase);
        _equipment = new(memory, moduleBase);
        _modifiers = new(memory);
        _accuracy = new(memory, moduleBase);
        _player = _players.Resolve();
        _weapon = _equipment.HeldHandle(_player);
    }

    private void ValidatePlayer()
    {
        if (_players.Resolve() != _player) throw new IOException("Персонаж сменился. Функции остановлены; подключись заново после загрузки сохранения.");
    }

    private static uint Flag(FeatureId id) => id switch
    {
        FeatureId.God => 1, FeatureId.InfiniteAmmo => 8, FeatureId.NoReload => 4, FeatureId.Durability => 16, _ => 0
    };

    internal void Set(FeatureId id, bool enabled)
    {
        ValidatePlayer();
        if (enabled == _enabled.Contains(id)) return;
        uint mask = Flag(id);
        if (enabled)
        {
            if (id == FeatureId.Accuracy && _accuracy.Resolve(_equipment.HeldHandle(_player)) is null)
                throw new InvalidOperationException("Возьми огнестрельное оружие в руки, затем включи точность.");
            if (mask != 0)
            {
                var before = _memory.ReadValue(_player.Address + 0x13C, ValueKind.Int32);
                _originalFlags[id] = (uint)before.Bits & mask;
                _memory.WriteValue(_player.Address + 0x13C, new(ValueKind.Int32, before.Bits | mask), before);
            }
            _enabled.Add(id);
        }
        else
        {
            if (mask != 0)
            {
                var before = _memory.ReadValue(_player.Address + 0x13C, ValueKind.Int32);
                var desired = ((uint)before.Bits & ~mask) | _originalFlags[id];
                _memory.WriteValue(_player.Address + 0x13C, new(ValueKind.Int32, desired), before);
                _originalFlags.Remove(id);
            }
            _enabled.Remove(id);
            if (id == FeatureId.Accuracy) _accuracy.Restore();
            if (id == FeatureId.Durability) _durability.Clear();
            RestoreUnusedOverrides();
        }
        Tick();
    }

    private bool WantsModifier(uint key) => key == 0x7E && IsEnabled(FeatureId.Durability);
    private void RestoreUnusedOverrides()
    {
        if (_equipment.HeldHandle(_player) != _weapon) { _overrides.Clear(); return; }
        foreach (var key in _overrides.Keys.ToArray())
        {
            if (WantsModifier(key)) continue;
            var item = _overrides[key];
            if (_modifiers.Find(_player, key) == item.Address)
            {
                var now = _memory.ReadValue(item.Address, ValueKind.Float32);
                if (now.Bits == 0) _memory.WriteValue(item.Address, item.Original, now);
            }
            _overrides.Remove(key);
        }
    }

    private void OverrideModifier(uint key)
    {
        if (_modifiers.Find(_player, key) is not { } address) return;
        var before = _memory.ReadValue(address, ValueKind.Float32);
        float value = BitConverter.UInt32BitsToSingle((uint)before.Bits);
        if (!float.IsFinite(value) || value < 0 || value > 1_000_000) throw new IOException("Некорректное значение модификатора.");
        if (!_overrides.TryGetValue(key, out var saved) || saved.Address != address || before.Bits != 0)
            _overrides[key] = (address, before);
        if (before.Bits != 0) _memory.WriteValue(address, new(ValueKind.Float32, 0), before);
    }

    private void HoldFloat(uint offset, float target)
    {
        var desired = new MemoryValue(ValueKind.Float32, BitConverter.SingleToUInt32Bits(target));
        var before = _memory.ReadValue(_player.Address + offset, ValueKind.Float32);
        float value = BitConverter.UInt32BitsToSingle((uint)before.Bits);
        if (!float.IsFinite(value) || value < 0 || value > 1_000_000) throw new IOException("Состояние персонажа вне допустимого диапазона.");
        if (before != desired) _memory.WriteValue(_player.Address + offset, desired, before);
    }

    internal void Tick()
    {
        ValidatePlayer();
        uint weapon = _equipment.HeldHandle(_player);
        if (IsEnabled(FeatureId.Accuracy)) _accuracy.Tick(weapon);
        if (weapon != _weapon)
        {
            // The game recomputes these cached stats on a weapon switch. Never restore old weapon stats into the new one.
            _overrides.Clear(); _weapon = weapon; _weaponDelay = 3;
        }
        uint flags = 0;
        foreach (var id in _enabled) flags |= Flag(id);
        if (flags != 0)
        {
            var before = _memory.ReadValue(_player.Address + 0x13C, ValueKind.Int32);
            if ((before.Bits & flags) != flags) _memory.WriteValue(_player.Address + 0x13C, new(ValueKind.Int32, before.Bits | flags), before);
        }
        if (IsEnabled(FeatureId.Stamina))
        {
            float maximum = BitConverter.UInt32BitsToSingle((uint)_memory.ReadValue(_player.Address + 0x154, ValueKind.Float32).Bits);
            if (!float.IsFinite(maximum) || maximum <= 0 || maximum > 1_000_000) throw new IOException("Максимум выносливости некорректен.");
            HoldFloat(0x150, maximum);
        }
        if (IsEnabled(FeatureId.NoHunger)) HoldFloat(0x17C, 0);
        if (IsEnabled(FeatureId.NoRadiation)) HoldFloat(0x16C, 0);
        if (IsEnabled(FeatureId.NoBleeding)) HoldFloat(0x158, 0);
        if (IsEnabled(FeatureId.NoDrowsiness)) HoldFloat(0x184, 0);
        int gearCount = 0;
        if (IsEnabled(FeatureId.Durability))
        {
            var items = _equipment.ReadEquipped(_player);
            foreach (var item in items)
            {
                if (!_durability.TryGetValue(item.Handle, out var saved) || saved.Model != item.Model || item.Condition > saved.Value)
                    _durability[item.Handle] = (item.Model, item.Condition);
                else if (item.Condition < saved.Value)
                {
                    // Re-resolve item identity before a write; addresses can be recycled on an equipment change.
                    if (_equipment.ReadItem(item.Handle, item.Slot) is not { } again || again.Model != item.Model) continue;
                    _memory.WriteValue(item.DurabilityAddress, new(ValueKind.Float32, BitConverter.SingleToUInt32Bits(saved.Value)),
                        new(ValueKind.Float32, BitConverter.SingleToUInt32Bits(again.Condition)));
                }
            }
            foreach (var handle in _durability.Keys.Except(items.Select(i => i.Handle)).ToArray()) _durability.Remove(handle);
            gearCount = items.Count;
        }
        if (_weaponDelay > 0) _weaponDelay--;
        else
        {
            if (IsEnabled(FeatureId.Durability)) OverrideModifier(0x7E);
        }
        Status = $"Активно: {_enabled.Count} из {Descriptions.Length}" + (gearCount > 0 ? $"  •  Защищено предметов: {gearCount}" : "");
    }

    internal void StopAll()
    {
        ValidatePlayer();
        // Disable as a group so clearing one feature never ticks and reapplies the others during shutdown.
        var before = _memory.ReadValue(_player.Address + 0x13C, ValueKind.Int32);
        uint value = (uint)before.Bits;
        foreach (var (id, original) in _originalFlags) value = (value & ~Flag(id)) | original;
        if (value != before.Bits) _memory.WriteValue(_player.Address + 0x13C, new(ValueKind.Int32, value), before);
        _enabled.Clear(); _originalFlags.Clear(); _durability.Clear();
        _accuracy.Restore();
        RestoreUnusedOverrides();
        Status = "Все функции выключены";
    }
}
