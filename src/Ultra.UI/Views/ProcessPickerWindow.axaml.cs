// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ultra.UI.Services;
using Ultra.UI.ViewModels;

namespace Ultra.UI.Views;

/// <summary>
/// The process picker dialog. Closes with <c>true</c> when the user selects processes.
/// </summary>
internal sealed partial class ProcessPickerWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessPickerWindow"/> class.
    /// </summary>
    public ProcessPickerWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FilterBox.Focus();
        // Down from the search box moves to the list, to pick with the keyboard.
        FilterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && ProcessList.ItemCount > 0)
            {
                if (ProcessList.SelectedIndex < 0)
                {
                    ProcessList.SelectedIndex = 0;
                }

                ProcessList.ContainerFromIndex(ProcessList.SelectedIndex)?.Focus();
                e.Handled = true;
            }
        };
    }

    private void OnSelect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProcessPickerViewModel { SelectedProcesses.Count: > 0 })
        {
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnProcessDoubleTapped(object? sender, TappedEventArgs e)
    {
        var item = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is ProcessInfo process && DataContext is ProcessPickerViewModel viewModel)
        {
            viewModel.SelectedProcesses.Clear();
            viewModel.SelectedProcesses.Add(process);
            Close(true);
        }
    }
}
