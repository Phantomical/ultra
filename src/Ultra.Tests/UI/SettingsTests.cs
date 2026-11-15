// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.UI.Services;

namespace Ultra.Tests.UI;

/// <summary>
/// Tests for the persistent settings of the ultra UI and the symbol path built from them.
/// </summary>
[TestClass]
public class SettingsTests
{
    private string _tempDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ultra-settings-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void MissingFileGivesDefaults()
    {
        var settings = new SettingsStore(Path.Combine(_tempDirectory, "settings.json")).Load();

        Assert.IsTrue(settings.IncludeDefaultSymbolPath);
        Assert.AreEqual(0, settings.SymbolPaths.Count);
        Assert.IsNull(settings.OutputDirectory);
        Assert.IsTrue(settings.Launch.RunAsAdministrator);
    }

    [TestMethod]
    public void SettingsRoundTrip()
    {
        var store = new SettingsStore(Path.Combine(_tempDirectory, "sub", "settings.json"));
        var settings = new AppSettings
        {
            IncludeDefaultSymbolPath = false,
            OutputDirectory = @"D:\profiles",
            SymbolPaths =
            {
                new SymbolPathEntrySetting { Path = @"E:\KSP\mono\symbols" },
                new SymbolPathEntrySetting { Path = "SRV*C:\\cache*https://example.com/symbols", Enabled = false },
            },
            RecentPrograms = { @"C:\Games\KSP\KSP_x64.exe" },
            Launch =
            {
                ProgramPath = @"C:\Games\KSP\KSP_x64.exe",
                Arguments = "-popupwindow \"-logFile x.log\"",
                WorkingDirectory = @"C:\Games\KSP",
                EnvironmentVariables = { new EnvironmentVariableSetting { Name = "MONO_ENV_OPTIONS", Value = "--debug" } },
                RunAsAdministrator = false,
                SamplingInterval = "0.5",
            },
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.IsFalse(loaded.IncludeDefaultSymbolPath);
        Assert.AreEqual(@"D:\profiles", loaded.OutputDirectory);
        Assert.AreEqual(2, loaded.SymbolPaths.Count);
        Assert.AreEqual(@"E:\KSP\mono\symbols", loaded.SymbolPaths[0].Path);
        Assert.IsFalse(loaded.SymbolPaths[1].Enabled);
        CollectionAssert.AreEqual(settings.RecentPrograms, loaded.RecentPrograms);
        Assert.AreEqual(settings.Launch.Arguments, loaded.Launch.Arguments);
        Assert.AreEqual("MONO_ENV_OPTIONS", loaded.Launch.EnvironmentVariables.Single().Name);
        Assert.AreEqual("--debug", loaded.Launch.EnvironmentVariables.Single().Value);
        Assert.IsFalse(loaded.Launch.RunAsAdministrator);
        Assert.AreEqual("0.5", loaded.Launch.SamplingInterval);
        Assert.IsFalse(File.Exists(store.FilePath + ".tmp"));
    }

    [TestMethod]
    public void CorruptFileGivesDefaultsAndIsKept()
    {
        var file = Path.Combine(_tempDirectory, "settings.json");
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(file, "{ not json");

        var settings = new SettingsStore(file).Load();

        Assert.IsTrue(settings.IncludeDefaultSymbolPath);
        Assert.AreEqual("{ not json", File.ReadAllText(file + ".bad"));
    }

    [TestMethod]
    public void ExplicitNullsAreNormalized()
    {
        var file = Path.Combine(_tempDirectory, "settings.json");
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(file, """{ "symbolPaths": null, "launch": { "environmentVariables": [null] }, "recentPrograms": [null, ""] }""");

        var settings = new SettingsStore(file).Load();

        Assert.AreEqual(0, settings.SymbolPaths.Count);
        Assert.AreEqual(0, settings.Launch.EnvironmentVariables.Count);
        Assert.AreEqual(0, settings.RecentPrograms.Count);
    }

    [TestMethod]
    public void NoCustomEntriesUsesTheProfilerDefault()
    {
        Assert.IsNull(SymbolPathComposer.Compose([], includeDefault: true));
        Assert.IsNull(SymbolPathComposer.Compose([new SymbolPathEntrySetting { Path = @"C:\x", Enabled = false }], includeDefault: true));
    }

    [TestMethod]
    public void NoEntriesAndNoDefaultSearchesNothing()
    {
        Assert.AreEqual("", SymbolPathComposer.Compose([], includeDefault: false));
    }

    [TestMethod]
    public void CustomEntriesComeBeforeTheDefault()
    {
        var composed = SymbolPathComposer.Compose(
        [
            new SymbolPathEntrySetting { Path = @" E:\KSP\mono\symbols " },
            new SymbolPathEntrySetting { Path = @"C:\disabled", Enabled = false },
            new SymbolPathEntrySetting { Path = @"SRV*C:\cache*https://a;E:\KSP\mono\symbols;;" },
        ], includeDefault: true);

        Assert.AreEqual($@"E:\KSP\mono\symbols;SRV*C:\cache*https://a;{SymbolPathComposer.GetDefaultSymbolPath()}", composed);
        Assert.IsFalse(composed!.Contains(";;"), composed);
    }

    [TestMethod]
    public void DefaultSymbolPathHasNoEmptyElements()
    {
        var defaultPath = SymbolPathComposer.GetDefaultSymbolPath();

        Assert.IsFalse(defaultPath.StartsWith(';'), defaultPath);
        Assert.IsFalse(defaultPath.Contains(";;"), defaultPath);
        Assert.AreNotEqual("", defaultPath);
    }

    [TestMethod]
    public void CustomEntriesWithoutTheDefault()
    {
        var composed = SymbolPathComposer.Compose([new SymbolPathEntrySetting { Path = @"E:\symbols" }], includeDefault: false);

        Assert.AreEqual(@"E:\symbols", composed);
    }
}
