using GpuTweak.Core.Models;

namespace GpuTweak.Core.Profiles;

public sealed record UserProfile(
    string Name,
    string GpuName,
    TuningSettings Settings,
    bool IsStable = false,
    bool AutoApplyOnStart = false,
    DateTimeOffset? CreatedAt = null);
