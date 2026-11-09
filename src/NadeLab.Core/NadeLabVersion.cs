using System.Reflection;

namespace NadeLab.Core;

public static class NadeLabVersion
{
    // MSBuild supplies this from Directory.Build.props for all shipped assemblies.
    public static string Current { get; } = typeof(NadeLabVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";
}
