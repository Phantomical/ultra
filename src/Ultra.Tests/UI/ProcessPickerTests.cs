// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using Ultra.UI.Services;
using Ultra.UI.ViewModels;

namespace Ultra.Tests.UI;

/// <summary>
/// Tests for the process picker of the attach mode and the process listing behind it.
/// </summary>
[TestClass]
public class ProcessPickerTests
{
    private static readonly ProcessInfo Game = new(1234, "KSP_x64", @"E:\KSP\Kerbal Space Program\KSP_x64.exe", "Kerbal Space Program");
    private static readonly ProcessInfo Editor = new(4321, "devenv", @"C:\VS\devenv.exe", "ultra - Microsoft Visual Studio");
    private static readonly ProcessInfo Service = new(88, "svchost", null, "");
    private static readonly ProcessInfo Ui = new(777, "ultra-ui", @"C:\tools\ultra-ui.exe", "Ultra");

    [TestMethod]
    public void ListsTheCurrentProcessWithItsPath()
    {
        var processes = ProcessLister.List();

        var current = processes.Single(x => x.Id == Environment.ProcessId);
        using var self = Process.GetCurrentProcess();
        Assert.AreEqual(self.ProcessName, current.Name);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(self.MainModule!.FileName, current.Path, ignoreCase: true);
            Assert.IsFalse(processes.Any(x => x.Id is 0 or 4), "The idle and system processes cannot be profiled");
        }
    }

    [TestMethod]
    public void ListIsSortedByNameThenId()
    {
        var processes = ProcessLister.List();

        for (var i = 1; i < processes.Count; i++)
        {
            var byName = string.Compare(processes[i - 1].Name, processes[i].Name, StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(byName < 0 || (byName == 0 && processes[i - 1].Id < processes[i].Id), $"{processes[i - 1]} before {processes[i]}");
        }
    }

    [TestMethod]
    public void TheUiItselfIsLeftOut()
    {
        var picker = CreatePicker(null);

        CollectionAssert.DoesNotContain(picker.FilteredProcesses, Ui);
        Assert.AreEqual("3 processes", picker.StatusText);
    }

    [TestMethod]
    public void FilterMatchesNameIdTitleAndPath()
    {
        var picker = CreatePicker(null);

        picker.Filter = "ksp";
        CollectionAssert.AreEqual(new[] { Game }, picker.FilteredProcesses);
        Assert.AreEqual("1 of 3 processes", picker.StatusText);

        picker.Filter = "432";
        CollectionAssert.AreEqual(new[] { Editor }, picker.FilteredProcesses);

        picker.Filter = "visual studio";
        CollectionAssert.AreEqual(new[] { Editor }, picker.FilteredProcesses);

        picker.Filter = @"C:\VS";
        CollectionAssert.AreEqual(new[] { Editor }, picker.FilteredProcesses);

        picker.Filter = "  ";
        Assert.AreEqual(3, picker.FilteredProcesses.Count);
    }

    [TestMethod]
    public void EnteredIdsAreSelectedInitially()
    {
        var picker = CreatePicker("1234, 88; nope 99999");

        CollectionAssert.AreEquivalent(new[] { Game, Service }, picker.SelectedProcesses);
    }

    [TestMethod]
    public void SelectionSurvivesFilteringAndRefreshing()
    {
        var processes = new List<ProcessInfo> { Game, Editor, Service, Ui };
        var picker = new ProcessPickerViewModel(null, () => processes, excludedProcessId: Ui.Id);
        picker.SelectedProcesses.Add(Game);

        // A selected process stays listed (and selected) when the filter does not match it.
        picker.Filter = "devenv";
        CollectionAssert.AreEquivalent(new[] { Game, Editor }, picker.FilteredProcesses);
        CollectionAssert.AreEqual(new[] { Game }, picker.SelectedProcesses);

        picker.Filter = "";
        processes.Remove(Game);
        picker.SelectedProcesses.Add(Editor);
        picker.RefreshCommand.Execute(null);

        // The game exited: only the editor is still selected.
        CollectionAssert.AreEqual(new[] { Editor }, picker.SelectedProcesses);
        Assert.AreEqual(2, picker.FilteredProcesses.Count);
    }

    [TestMethod]
    public void SelectedIdsAreFormattedForTheProcessIdBox()
    {
        var picker = CreatePicker(null);
        picker.SelectedProcesses.Add(Game);
        picker.SelectedProcesses.Add(Service);

        Assert.AreEqual("1234, 88", picker.SelectedProcessIdText);
    }

    [TestMethod]
    public void ParseProcessIdsIgnoresWhatIsNotAnId()
    {
        CollectionAssert.AreEqual(new[] { 1, 22, 333 }, ProcessPickerViewModel.ParseProcessIds("1,22 ;x, -5 0 333").ToArray());
        Assert.AreEqual(0, ProcessPickerViewModel.ParseProcessIds(null).Count());
    }

    [TestMethod]
    public void ExtractsTheIconOfAnExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Icons are extracted on Windows only.");
        }

        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var pixels = ExecutableIcons.TryGetPixels(explorer, out var width, out var height);

        Assert.IsNotNull(pixels);
        Assert.IsTrue(width >= 16 && height >= 16, $"{width}x{height}");
        Assert.AreEqual(width * height * 4, pixels.Length);
        var alphas = Enumerable.Range(0, width * height).Select(i => pixels[i * 4 + 3]).ToList();
        Assert.IsTrue(alphas.Any(x => x == 255), "Some pixels are opaque");
        Assert.IsTrue(alphas.Any(x => x == 0), "The corners of the folder icon are transparent");
    }

    [TestMethod]
    public void NoIconForAFileWithoutOneOrAMissingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ultra-icons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var empty = Path.Combine(directory, "empty.exe");
            File.WriteAllBytes(empty, []);

            Assert.IsNull(ExecutableIcons.TryGetPixels(empty, out _, out _));
            Assert.IsNull(ExecutableIcons.TryGetPixels(Path.Combine(directory, "missing.exe"), out _, out _));
            Assert.IsNull(ExecutableIcons.GetIcon(null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessPickerViewModel CreatePicker(string? processIdText) =>
        new(processIdText, () => [Game, Editor, Service, Ui], excludedProcessId: Ui.Id);
}
