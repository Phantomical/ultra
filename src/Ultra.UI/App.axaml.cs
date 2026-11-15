// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ultra.UI.Services;
using Ultra.UI.ViewModels;
using Ultra.UI.Views;

namespace Ultra.UI;

/// <summary>
/// The ultra UI application.
/// </summary>
public sealed partial class App : Application
{
    private ProfileServer? _server;
    private MainWindowViewModel? _viewModel;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new SettingsStore(SettingsStore.DefaultFilePath);
            var settings = store.Load();

            _server = new ProfileServer();
            _server.Start();

            _viewModel = new MainWindowViewModel(store, settings, _server);
            var window = new MainWindow { DataContext = _viewModel };
            _viewModel.Attach(window);
            desktop.MainWindow = window;

            foreach (var argument in desktop.Args ?? [])
            {
                if (File.Exists(argument))
                {
                    _viewModel.HandleDroppedFiles([Path.GetFullPath(argument)]);
                }
            }

            desktop.Exit += (_, _) =>
            {
                _viewModel.Dispose();
                _server.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
