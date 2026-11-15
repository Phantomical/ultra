// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ultra.UI.Services;

/// <summary>
/// Loads and saves the <see cref="AppSettings"/> of the ultra UI as a JSON file.
/// </summary>
internal sealed class SettingsStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsStore"/> class.
    /// </summary>
    /// <param name="filePath">The settings file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <c>null</c>.</exception>
    public SettingsStore(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        FilePath = filePath;
    }

    /// <summary>
    /// Gets the settings file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// The default settings file, <c>%APPDATA%\ultra\ui-settings.json</c> on Windows.
    /// </summary>
    public static string DefaultFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ultra", "ui-settings.json");

    /// <summary>
    /// Loads the settings. A missing or unreadable file gives the default settings; an unreadable file is kept next to
    /// the settings file with a <c>.bad</c> extension so that it is not silently lost.
    /// </summary>
    /// <returns>The settings.</returns>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
            }

            return new AppSettings();
        }
    }

    /// <summary>
    /// Saves the settings, replacing the settings file atomically.
    /// </summary>
    /// <param name="settings">The settings to save.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <c>null</c>.</exception>
    /// <exception cref="IOException">The settings file could not be written.</exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFile = FilePath + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(tempFile, FilePath, overwrite: true);
    }

    private static void Normalize(AppSettings settings)
    {
        // A hand-edited file can contain explicit nulls.
        settings.SymbolPaths ??= new();
        settings.SymbolPaths.RemoveAll(x => x is null);
        settings.Launch ??= new();
        settings.Launch.EnvironmentVariables ??= new();
        settings.Launch.EnvironmentVariables.RemoveAll(x => x is null);
        settings.RecentPrograms ??= new();
        settings.RecentPrograms.RemoveAll(string.IsNullOrWhiteSpace);
    }
}

/// <summary>
/// The JSON serialization context of the settings file (source generated, so that no reflection is needed).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
