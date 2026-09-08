using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StalkerTrainer.Core;

namespace StalkerTrainer.Native;

public sealed class ProcessMemory : IMemoryReader, IGameMemory, IDisposable
{
    private const uint QueryRead = 0x0400 | 0x0010 | 0x100000; // query, VM_READ, SYNCHRONIZE
    private const uint WriteAccess = 0x0020 | 0x0008;
    private readonly SafeProcessHandle _handle;
    public int ProcessId { get; }
    public bool CanWrite { get; }
    public bool IsAlive => !_handle.IsClosed && WaitForSingleObject(_handle, 0) == 0x102;

    // UI keeps one process handle for its lifetime: no attaching an old address to a reused PID.
    public ProcessMemory(int processId, bool writable = true)
    {
        ProcessId = processId;
        CanWrite = writable;
        _handle = OpenProcess(QueryRead | (writable ? WriteAccess : 0), false, processId);
        if (_handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "Windows не разрешила доступ к памяти процесса.");
        }
        if (!IsWow64Process2(_handle, out var machine, out _))
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
        if (machine != 0) { _handle.Dispose(); throw new NotSupportedException("Нужен 64-битный процесс игры."); }
    }

    public IReadOnlyList<MemoryRegion> GetRegions(CancellationToken token)
    {
        EnsureAlive();
        var regions = new List<MemoryRegion>();
        ulong cursor = 0x10000;
        while (cursor < 0x00007FFFFFFF0000)
        {
            token.ThrowIfCancellationRequested();
            var size = VirtualQueryEx(_handle, (nuint)cursor, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>());
            if (size == 0)
            {
                EnsureAlive();
                var error = Marshal.GetLastWin32Error();
                if (error == 87) break; // End of the process address space.
                throw new Win32Exception(error, "Не удалось перечислить память процесса.");
            }
            if (IsWritableData(info)) regions.Add(new((ulong)info.BaseAddress, (ulong)info.RegionSize));
            var next = (ulong)info.BaseAddress + (ulong)info.RegionSize;
            if (next <= cursor) break;
            cursor = next;
        }
        return regions;
    }

    private static bool IsWritableData(MemoryBasicInformation info) =>
        info.State == 0x1000 && info.Type is 0x20000 or 0x40000 or 0x1000000 && (info.Protect & 0x100) == 0 &&
        (info.Protect & 0xFF) is 0x04 or 0x08; // RW data, including mapped data and module .data. Never executable pages.

    public unsafe int Read(ulong address, byte[] buffer, int count)
    {
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        EnsureAlive();
        fixed (byte* pointer = buffer)
        {
            ReadProcessMemory(_handle, (nuint)address, pointer, (nuint)count, out var read);
            return (int)Math.Min(read, (nuint)count);
        }
    }

    public MemoryValue ReadValue(ulong address, ValueKind kind)
    {
        var bytes = new byte[MemoryValue.SizeOf(kind)];
        if (Read(address, bytes, bytes.Length) != bytes.Length) throw new IOException($"Адрес 0x{address:X} больше не читается.");
        return MemoryValue.Read(bytes, kind);
    }

    public void WriteValue(ulong address, MemoryValue value, MemoryValue? expected = null)
    {
        EnsureAlive();
        if (!CanWrite) throw new InvalidOperationException("Подключение открыто только для чтения.");
        if (address < 0x10000) throw new ArgumentException("Некорректный адрес.");
        if (expected is { } check && check.Kind != value.Kind) throw new ArgumentException("Типы исходного и нового значения не совпадают.");
        if (VirtualQueryEx(_handle, (nuint)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0 || !IsWritableData(info))
            throw new IOException("Адрес больше не относится к доступной памяти данных.");
        var end = (ulong)info.BaseAddress + (ulong)info.RegionSize;
        if (address > end || (ulong)value.Size > end - address) throw new IOException("Значение выходит за границу области памяти.");
        if (expected is { } original && ReadValue(address, original.Kind) != original)
            throw new InvalidOperationException("Значение уже изменилось. Обнови строку и проверь адрес перед записью.");
        WriteBytes(address, value.ToBytes());
    }

    private unsafe void WriteBytes(ulong address, byte[] bytes)
    {
        fixed (byte* pointer = bytes)
        {
            if (!WriteProcessMemory(_handle, (nuint)address, pointer, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось записать значение в память игры.");
        }
    }

    private void EnsureAlive()
    {
        if (!IsAlive) throw new InvalidOperationException("Процесс завершён. Подключись заново и повтори поиск адресов.");
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public nuint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, nuint address, out MemoryBasicInformation buffer, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadProcessMemory(SafeProcessHandle process, nuint address, byte* buffer, nuint count, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool WriteProcessMemory(SafeProcessHandle process, nuint address, byte* buffer, nuint count, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
