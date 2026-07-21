using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Sdat.Core.Scheduling;

namespace Sdat.App;

internal static class AppText
{
    private static readonly Lazy<ResourceLoader> Loader = new(() =>
        new ResourceLoader(Path.Combine(AppContext.BaseDirectory, "SDAT.pri")));

    public static string Get(string key, string fallback)
    {
        try
        {
            var value = Loader.Value.GetString(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    public static string Format(string key, string fallback, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key, fallback), arguments);

    public static string PowerAction(PowerActionType action) => action switch
    {
        PowerActionType.Shutdown => Get("ActionShutdown", "Shut down"),
        PowerActionType.Suspend => Get("ActionSuspend", "Suspend"),
        PowerActionType.Restart => Get("ActionRestart", "Restart"),
        _ => action.ToString(),
    };
}
