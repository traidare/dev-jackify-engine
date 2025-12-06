using System;

namespace Wabbajack.Common;

/// <summary>
/// Global configuration for texconv.exe behavior.
/// Currently only controls whether GPU acceleration is disabled via the
/// --disable-gpu-texconv flag passed to jackify-engine.
/// </summary>
public static class TexconvConfig
{
    /// <summary>
    /// When true, GPU acceleration for texconv.exe is disabled.
    /// ProtonPrefixManager will skip GPU-specific environment variables and
    /// fall back to the legacy CPU-only behavior (DISPLAY="", no PRIME vars).
    /// </summary>
    public static bool DisableGpuTexconv { get; set; } = false;
}


