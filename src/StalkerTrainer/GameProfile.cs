namespace StalkerTrainer;

// Profile for the installed Steam executable. Never enable it for a different hash.
internal static class GameProfile
{
    internal const string BuildId = "25382007";
    internal const string ExecutableSha256 = "61BC1E030740CEBC30CF1DAD0C86CF65E39E12FF0500225821D684181E08D56B";
    internal const ulong PlayerHandle = 0x9EEC140;
    internal const ulong PlayerPool = 0xA332940;
    internal const ulong PlayerVtable = 0x8EB2B10;
    internal const ulong ObjectChunks = 0xA0E68D0;
    internal const ulong ObjectCount = 0xA0E68E4;
    internal const ulong ItemPool = 0xA7859A0;
    internal const ulong WeaponModelVtable = 0x8EB1390;
    internal const ulong GrenadeModelVtable = 0x8EAF960;
    internal const ulong ReadOnlyDataStart = 0x7CCE000;
    internal const ulong WritableDataStart = 0x9E98000;
    internal const ulong Money = 0xA80DA90;
}
