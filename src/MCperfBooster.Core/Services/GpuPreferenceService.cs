using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using MCperfBooster.Core.Utils;
using Microsoft.Win32;

namespace MCperfBooster.Core.Services;

/// <summary>
/// Where Windows keeps the graphics card it gives each program (Settings &gt; System &gt; Display &gt; Graphics): one
/// setting per full path of an exe, such as "GpuPreference=2;". Replaceable so tests never touch the registry.
/// </summary>
public interface IGpuPreferenceStore
{
    /// <summary>The setting Windows keeps for the exe at this full path, or null when it keeps none.</summary>
    string? Get(string exePath);

    /// <summary>Replaces the setting of the exe at this full path.</summary>
    void Set(string exePath, string setting);

    /// <summary>Removes the setting of the exe at this full path; nothing happens when there is none.</summary>
    void Remove(string exePath);
}

/// <summary>
/// The settings Windows itself reads: the values under HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences,
/// one per program, named by the full path of its exe. A program gets its card when it starts, so a change applies
/// from the next start of Minecraft. Outside Windows there are none: nothing is found and nothing is written.
/// </summary>
public sealed class WindowsGpuPreferenceStore : IGpuPreferenceStore
{
    internal const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private readonly string _keyPath;

    public WindowsGpuPreferenceStore()
        : this(UserGpuPreferencesKey)
    {
    }

    /// <param name="keyPath">Another key below HKEY_CURRENT_USER, for a check that must leave the real one alone.</param>
    internal WindowsGpuPreferenceStore(string keyPath) => _keyPath = keyPath;

    public string? Get(string exePath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath);
        object? value = key?.GetValue(exePath, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

        // Windows writes text. Anything else is named by its kind, which never reads like a setting, so it is left alone.
        return value == null ? null : value as string ?? $"({key!.GetValueKind(exePath)})";
    }

    public void Set(string exePath, string setting)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(exePath, setting, RegistryValueKind.String);
    }

    public void Remove(string exePath)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(exePath, throwOnMissingValue: false);
    }
}

/// <summary>
/// Gives a GraalVM the graphics card the user picked in Windows for the launcher's own Java. Windows keeps that choice
/// per full exe path, so on a laptop with two graphics cards Minecraft would otherwise run on GraalVM with whichever
/// card Windows picks by itself, usually the weaker one. Only a choice the user made is carried over, and of a setting
/// only the parts that pick the card: the rest is Windows' own (Auto HDR, the notes it keeps such as "AppStatus=4").
/// What was carried over is recorded in a file, so that exactly that is taken back again, and only while it is still
/// as it was written.
/// </summary>
public sealed class GpuPreferenceService
{
    /// <summary>The name of the record, which lives next to the runtimes it is about.</summary>
    public const string RecordFileName = "gpu-preferences.json";

    // "GpuPreference" is 0 for "let Windows decide", 1 for power saving and 2 for high performance; a card picked by
    // name (Windows 11) comes as "SpecificAdapter" next to a GpuPreference of its own value.
    private const string PreferenceKey = "GpuPreference";
    private const string AdapterKey = "SpecificAdapter";

    private readonly IGpuPreferenceStore _store;
    private readonly string _recordFile;

    public GpuPreferenceService(IGpuPreferenceStore store, string recordFile)
    {
        _store = store;
        _recordFile = recordFile;
    }

    /// <summary>
    /// Gives <paramref name="targetExe"/> the graphics card of the first of <paramref name="launcherExes"/> that has
    /// one picked. Nothing is written when none has, when the target has a card picked already, whoever picked it, or
    /// when the card was carried over to it before: what the user did with it since stays as they left it. Returns the
    /// parts carried over ("GpuPreference=2;"), or null when nothing was. Throws <see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="SecurityException"/> when the record or Windows'
    /// settings cannot be read or written.
    /// </summary>
    public string? CarryOver(string targetExe, IEnumerable<string> launcherExes)
    {
        Dictionary<string, string> record = ReadRecord();
        if (record.ContainsKey(targetExe))
            return null;

        string? current = _store.Get(targetExe);
        if (current != null && (!IsReadable(current) || HasGraphicsCardPart(current)))
            return null;

        string? choice = launcherExes.Select(exe => GraphicsCardChoice(_store.Get(exe))).FirstOrDefault(found => found != null);
        if (choice == null)
            return null;

        // On record before it is written: a choice in Windows' settings that is not on record could never be taken back.
        record[targetExe] = choice;
        WriteRecord(record);
        try
        {
            _store.Set(targetExe, Append(current, choice));
        }
        catch
        {
            // Not written, so not on record either: the next injection tries again. Should the record stay as it is,
            // the target only goes without the card until GraalVM is switched off and on again.
            record.Remove(targetExe);
            TryWriteRecord(record);
            throw;
        }

        return choice;
    }

    /// <summary>
    /// Takes back what <see cref="CarryOver"/> wrote, for every runtime: the parts that pick the card go, the rest of
    /// the setting stays, and a setting with nothing else in it goes as a whole. A card the user picked since is theirs
    /// and stays. Never throws: what Windows refuses stays on record and is tried again the next time.
    /// </summary>
    public void RemoveCarriedOver()
    {
        Dictionary<string, string> record;
        try
        {
            record = ReadRecord();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again on the next injection without GraalVM.
            return;
        }

        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, choice) in record)
        {
            try
            {
                TakeBack(exe, choice);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                kept[exe] = choice;
            }
        }

        TryWriteRecord(kept);
    }

    private void TakeBack(string exe, string choice)
    {
        // Gone, or a card picked since: either way it is no longer what was written here.
        string? current = _store.Get(exe);
        if (current == null || !IsReadable(current) || !SameGraphicsCard(current, choice))
            return;

        string rest = string.Join(';', current.Split(';').Where(part => !IsGraphicsCardPart(part)));
        if (rest.Split(';').All(part => part.Trim().Length == 0))
            _store.Remove(exe);
        else
            _store.Set(exe, rest);
    }

    /// <summary>
    /// The graphics card a setting picks, as the parts that say so ("SpecificAdapter=…;GpuPreference=…;"), or null
    /// when it leaves the card to Windows: no such part, "GpuPreference=0", or a setting that does not read like one.
    /// </summary>
    internal static string? GraphicsCardChoice(string? setting)
    {
        if (setting == null || !IsReadable(setting))
            return null;

        string[] parts = setting.Split(';').Where(IsGraphicsCardPart).Select(part => $"{KeyOf(part)}={ValueOf(part)}").ToArray();
        bool picksACard = parts.Any(part =>
            KeyOf(part).Equals(PreferenceKey, StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(ValueOf(part), NumberStyles.None, CultureInfo.InvariantCulture, out long preference) && preference != 0);

        return picksACard ? string.Concat(parts.Select(part => part + ";")) : null;
    }

    /// <summary>The card a choice from <see cref="GraphicsCardChoice"/> picks, in words for the report.</summary>
    internal static string Describe(string choice)
    {
        string[] parts = choice.Split(';').Where(IsGraphicsCardPart).ToArray();
        if (parts.Any(part => KeyOf(part).Equals(AdapterKey, StringComparison.OrdinalIgnoreCase)))
            return "a specific graphics card";

        return parts.Where(part => KeyOf(part).Equals(PreferenceKey, StringComparison.OrdinalIgnoreCase)).Select(ValueOf).FirstOrDefault() switch
        {
            "1" => "power saving",
            "2" => "high performance",
            _ => "a chosen graphics card",
        };
    }

    /// <summary>Whether every part reads "name=value". Empty parts are fine: Windows writes ";SwapEffectUpgradeEnable=1;".</summary>
    internal static bool IsReadable(string setting) =>
        setting.Split(';').All(part => part.Trim().Length == 0 || KeyOf(part).Length > 0);

    private static bool HasGraphicsCardPart(string setting) => setting.Split(';').Any(IsGraphicsCardPart);

    private static bool IsGraphicsCardPart(string part)
    {
        string key = KeyOf(part);
        return key.Equals(PreferenceKey, StringComparison.OrdinalIgnoreCase) || key.Equals(AdapterKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameGraphicsCard(string setting, string choice)
    {
        static IEnumerable<string> CardParts(string text) => text.Split(';').Where(IsGraphicsCardPart)
            .Select(part => $"{KeyOf(part)}={ValueOf(part)}")
            .Order(StringComparer.OrdinalIgnoreCase);

        return CardParts(setting).SequenceEqual(CardParts(choice), StringComparer.OrdinalIgnoreCase);
    }

    // After what Windows keeps already, which ends in ";" when Windows wrote it.
    private static string Append(string? setting, string choice)
    {
        string kept = setting?.TrimEnd() ?? "";
        return kept.Length == 0 ? choice : kept.EndsWith(';') ? kept + choice : $"{kept};{choice}";
    }

    private static string KeyOf(string part)
    {
        int equals = part.IndexOf('=');
        return equals > 0 ? part[..equals].Trim() : "";
    }

    private static string ValueOf(string part)
    {
        int equals = part.IndexOf('=');
        return equals >= 0 ? part[(equals + 1)..].Trim() : "";
    }

    /// <summary>
    /// What was carried over, by target exe. A record that does not parse is forgotten (what it named stays in
    /// Windows' settings, where it does no harm); one that cannot be read throws.
    /// </summary>
    private Dictionary<string, string> ReadRecord()
    {
        var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_recordFile))
            return record;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_recordFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return record;

            foreach (JsonProperty entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } choice)
                    record[entry.Name] = choice;
            }
        }
        catch (JsonException)
        {
            record.Clear();
        }

        return record;
    }

    private void WriteRecord(Dictionary<string, string> record)
    {
        if (record.Count == 0)
        {
            if (File.Exists(_recordFile))
                File.Delete(_recordFile);
            return;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (exe, choice) in record)
                writer.WriteString(exe, choice);
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllText(_recordFile, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private void TryWriteRecord(Dictionary<string, string> record)
    {
        try
        {
            WriteRecord(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old record stays; what it names that is gone by now is simply dropped the next time.
        }
    }
}
