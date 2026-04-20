using System.Reflection;

namespace QuickReply.Desktop;

public static class AppIdentity
{
    private static readonly Assembly Assembly = Assembly.GetEntryAssembly() ?? typeof(AppIdentity).Assembly;

    public static string DisplayName =>
        Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? $"客服效率工具{Version}";

    public static string Version =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
