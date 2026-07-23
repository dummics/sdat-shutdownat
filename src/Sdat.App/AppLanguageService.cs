using Microsoft.Windows.Globalization;
using Sdat.Core.Settings;
using Sdat.Windows.Persistence;

namespace Sdat.App;

internal static class AppLanguageService
{
    public static string AppliedPreference { get; private set; } = UiLanguagePreference.System;

    public static void ApplyBeforeResourcesLoad()
    {
        AppliedPreference = SqliteLanguagePreferenceReader.ReadOrSystemDefault(
            SqliteStoreOptions.CreateDefault());
        if (AppliedPreference != UiLanguagePreference.System)
        {
            ApplicationLanguages.PrimaryLanguageOverride = AppliedPreference;
        }
    }
}
