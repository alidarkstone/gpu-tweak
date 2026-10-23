using System.Text.Json;
using GpuTweak.Core.Safety;

namespace GpuTweak.Core.Profiles;

/// <summary>User profiles, stored as profiles.json next to the portable exe.</summary>
public sealed class ProfileStore
{
    private readonly string _path;
    private readonly List<UserProfile> _profiles = [];

    public ProfileStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "profiles.json");
        if (!File.Exists(_path)) return;
        try
        {
            _profiles.AddRange(JsonSerializer.Deserialize<List<UserProfile>>(File.ReadAllText(_path), JsonDefaults.Options) ?? []);
        }
        catch (JsonException)
        {
            File.Copy(_path, _path + ".broken", overwrite: true);
        }
    }

    public IReadOnlyList<UserProfile> Profiles => _profiles;

    public void Save(UserProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _profiles[index] = profile;
        else _profiles.Add(profile);

        // Only one profile can auto-apply.
        if (profile.AutoApplyOnStart)
        {
            for (var i = 0; i < _profiles.Count; i++)
            {
                if (_profiles[i] != profile && _profiles[i].AutoApplyOnStart)
                    _profiles[i] = _profiles[i] with { AutoApplyOnStart = false };
            }
        }
        Persist();
    }

    public void Delete(string name)
    {
        _profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Persist();
    }

    /// <summary>
    /// The profile to apply at startup: it must be marked stable, belong to this GPU,
    /// and the previous session must not have crashed.
    /// </summary>
    public UserProfile? GetStartupProfile(string gpuName, CrashReport? crash)
    {
        if (crash is not null) return null;
        return _profiles.FirstOrDefault(p =>
            p.AutoApplyOnStart && p.IsStable && p.GpuName.Equals(gpuName, StringComparison.OrdinalIgnoreCase));
    }

    private void Persist()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_profiles, JsonDefaults.Options));
        File.Move(temp, _path, overwrite: true);
    }
}
