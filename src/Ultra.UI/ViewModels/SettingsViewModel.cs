// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ultra.UI.Services;

namespace Ultra.UI.ViewModels;

/// <summary>
/// An entry of the custom symbol path in the settings dialog.
/// </summary>
internal sealed partial class SymbolPathEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private bool _enabled = true;
}

/// <summary>
/// The settings dialog: symbol paths and output folder.
/// </summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IUiServices? _ui;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class from the current settings.
    /// </summary>
    /// <param name="settings">The settings to edit (not modified until <see cref="ApplyTo"/>).</param>
    /// <param name="ui">The window services, for the folder pickers.</param>
    public SettingsViewModel(AppSettings settings, IUiServices? ui)
    {
        _ui = ui;
        SymbolPaths = new ObservableCollection<SymbolPathEntryViewModel>(settings.SymbolPaths.Select(x => new SymbolPathEntryViewModel { Path = x.Path, Enabled = x.Enabled }));
        _includeDefaultSymbolPath = settings.IncludeDefaultSymbolPath;
        _outputDirectory = settings.OutputDirectory ?? "";

        SymbolPaths.CollectionChanged += OnSymbolPathsChanged;
        foreach (var entry in SymbolPaths)
        {
            entry.PropertyChanged += OnEntryChanged;
        }

        UpdateEffectiveSymbolPath();
    }

    /// <summary>Gets the custom symbol path entries, in search order.</summary>
    public ObservableCollection<SymbolPathEntryViewModel> SymbolPaths { get; }

    [ObservableProperty]
    private bool _includeDefaultSymbolPath;

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    private string _effectiveSymbolPath = "";

    /// <summary>Gets the output folder used when <see cref="OutputDirectory"/> is empty.</summary>
    public string DefaultOutputDirectory => AppSettings.DefaultOutputDirectory;

    /// <summary>Gets the default symbol path searched after the custom entries.</summary>
    public string DefaultSymbolPath => SymbolPathComposer.GetDefaultSymbolPath();

    /// <summary>
    /// Writes the edited settings to <paramref name="settings"/>.
    /// </summary>
    /// <param name="settings">The settings to update.</param>
    public void ApplyTo(AppSettings settings)
    {
        settings.SymbolPaths = SymbolPaths
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => new SymbolPathEntrySetting { Path = x.Path.Trim(), Enabled = x.Enabled })
            .ToList();
        settings.IncludeDefaultSymbolPath = IncludeDefaultSymbolPath;
        settings.OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory.Trim();
    }

    partial void OnIncludeDefaultSymbolPathChanged(bool value) => UpdateEffectiveSymbolPath();

    [RelayCommand]
    private void AddSymbolPath() => SymbolPaths.Add(new SymbolPathEntryViewModel());

    [RelayCommand]
    private async Task AddSymbolFolderAsync()
    {
        if (_ui is null) return;
        var folder = await _ui.PickFolderAsync("Add a folder containing symbols (.pdb)");
        if (folder is not null)
        {
            SymbolPaths.Add(new SymbolPathEntryViewModel { Path = folder });
        }
    }

    [RelayCommand]
    private void AddSymbolServer()
    {
        var cache = Path.Combine(Path.GetTempPath(), "SymbolCache");
        SymbolPaths.Add(new SymbolPathEntryViewModel { Path = $"SRV*{cache}*https://" });
    }

    [RelayCommand]
    private void RemoveSymbolPath(SymbolPathEntryViewModel? entry)
    {
        if (entry is not null)
        {
            SymbolPaths.Remove(entry);
        }
    }

    [RelayCommand]
    private void MoveSymbolPathUp(SymbolPathEntryViewModel? entry)
    {
        var index = entry is null ? -1 : SymbolPaths.IndexOf(entry);
        if (index > 0)
        {
            SymbolPaths.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveSymbolPathDown(SymbolPathEntryViewModel? entry)
    {
        var index = entry is null ? -1 : SymbolPaths.IndexOf(entry);
        if (index >= 0 && index < SymbolPaths.Count - 1)
        {
            SymbolPaths.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private async Task BrowseOutputDirectoryAsync()
    {
        if (_ui is null) return;
        var folder = await _ui.PickFolderAsync("Folder to write the profiles to");
        if (folder is not null)
        {
            OutputDirectory = folder;
        }
    }

    private void OnSymbolPathsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SymbolPathEntryViewModel entry in e.OldItems)
            {
                entry.PropertyChanged -= OnEntryChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SymbolPathEntryViewModel entry in e.NewItems)
            {
                entry.PropertyChanged += OnEntryChanged;
            }
        }

        UpdateEffectiveSymbolPath();
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e) => UpdateEffectiveSymbolPath();

    private void UpdateEffectiveSymbolPath()
    {
        var entries = SymbolPaths.Select(x => new SymbolPathEntrySetting { Path = x.Path, Enabled = x.Enabled });
        var composed = SymbolPathComposer.Compose(entries, IncludeDefaultSymbolPath);
        EffectiveSymbolPath = composed switch
        {
            null => DefaultSymbolPath,
            "" => "(none: only the local symbol cache is searched)",
            _ => composed,
        };
    }
}
