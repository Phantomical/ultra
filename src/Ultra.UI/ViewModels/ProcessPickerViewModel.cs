// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ultra.UI.Services;

namespace Ultra.UI.ViewModels;

/// <summary>
/// The process picker: the running processes, filtered by a search text, from which the processes to attach to are
/// selected.
/// </summary>
internal sealed partial class ProcessPickerViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<ProcessInfo>> _listProcesses;
    private readonly int _excludedProcessId;
    private readonly HashSet<int> _initialIds = [];
    private List<ProcessInfo> _processes = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessPickerViewModel"/> class.
    /// </summary>
    /// <param name="processIdText">The process ids already entered (separated by commas, semicolons or spaces), selected initially.</param>
    /// <param name="listProcesses">Lists the running processes; <see cref="ProcessLister.List"/> by default.</param>
    /// <param name="excludedProcessId">A process left out of the list (the UI itself by default).</param>
    public ProcessPickerViewModel(string? processIdText, Func<IReadOnlyList<ProcessInfo>>? listProcesses = null, int? excludedProcessId = null)
    {
        _listProcesses = listProcesses ?? ProcessLister.List;
        _excludedProcessId = excludedProcessId ?? Environment.ProcessId;
        foreach (var id in ParseProcessIds(processIdText))
        {
            _initialIds.Add(id);
        }

        Refresh();
    }

    /// <summary>Gets the processes matching <see cref="Filter"/>.</summary>
    public ObservableCollection<ProcessInfo> FilteredProcesses { get; } = [];

    /// <summary>Gets the selected processes (bound to the selection of the list).</summary>
    public ObservableCollection<ProcessInfo> SelectedProcesses { get; } = [];

    /// <summary>Gets or sets the search text: matches the name, id, window title or path of a process.</summary>
    [ObservableProperty]
    private string _filter = "";

    /// <summary>Gets the number of processes listed, for the status line.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>
    /// Gets the selected process ids, separated by commas, as entered in the process id box.
    /// </summary>
    public string SelectedProcessIdText => string.Join(", ", SelectedProcesses.Select(x => x.Id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Parses process ids separated by commas, semicolons or spaces, ignoring anything that is not a positive number.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The process ids.</returns>
    public static IEnumerable<int> ParseProcessIds(string? text)
    {
        foreach (var part in (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                yield return id;
            }
        }
    }

    /// <summary>
    /// Lists the running processes again, keeping the selected processes that still run selected.
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        var selectedIds = _processes.Count == 0 ? _initialIds : SelectedProcesses.Select(x => x.Id).ToHashSet();
        _processes = _listProcesses().Where(x => x.Id != _excludedProcessId).ToList();
        ApplyFilter(selectedIds);
    }

    partial void OnFilterChanged(string value) => ApplyFilter(SelectedProcesses.Select(x => x.Id).ToHashSet());

    private void ApplyFilter(HashSet<int> selectedIds)
    {
        var filter = Filter.Trim();
        // The selection is kept even when the filter hides a selected process.
        var selected = _processes.Where(x => selectedIds.Contains(x.Id)).ToList();

        FilteredProcesses.Clear();
        foreach (var process in _processes)
        {
            if (Matches(process, filter) || selectedIds.Contains(process.Id))
            {
                FilteredProcesses.Add(process);
            }
        }

        SelectedProcesses.Clear();
        foreach (var process in selected)
        {
            SelectedProcesses.Add(process);
        }

        StatusText = filter.Length == 0
            ? $"{_processes.Count} processes"
            : $"{FilteredProcesses.Count} of {_processes.Count} processes";
    }

    private static bool Matches(ProcessInfo process, string filter) =>
        filter.Length == 0
        || process.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || process.Id.ToString(CultureInfo.InvariantCulture).StartsWith(filter, StringComparison.Ordinal)
        || process.WindowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (process.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
