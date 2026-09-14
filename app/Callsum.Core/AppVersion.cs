using System.Reflection;

namespace Callsum.Core;

/// <summary>Версия приложения в том виде, в каком её показывают человеку.</summary>
public static class AppVersion
{
    /// <summary>Что написано в сборке: суффикс предрелиза там есть, а в числовой версии — нет.</summary>
    public static string Current(Assembly assembly) => Readable(
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assembly.GetName().Version);

    /// <summary>
    /// Привести версию сборки к тому, что стоит показать в окне.
    ///
    /// Показывается информационная версия, а не числовая: «1.1.0-rc.1» в числовую
    /// не помещается вовсе, а именно суффиксом сборка, которую обкатывают на себе,
    /// отличается от той, что ушла коллегам. При этом сборка дописывает к этой
    /// версии хеш коммита через «+» — человеку он ничего не говорит.
    /// </summary>
    public static string Readable(string? informational, Version? numeric)
    {
        var written = (informational ?? "").Trim();
        var mark = written.IndexOf('+');
        if (mark >= 0)
        {
            written = written[..mark];
        }

        return written.Length > 0 ? written : numeric?.ToString(3) ?? "";
    }
}
