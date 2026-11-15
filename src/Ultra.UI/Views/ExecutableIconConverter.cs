// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using Avalonia.Data.Converters;
using Ultra.UI.Services;

namespace Ultra.UI.Views;

/// <summary>
/// Converts the path of an executable to its icon (<see cref="ExecutableIcons.GetIcon"/>), or <c>null</c>.
/// </summary>
internal sealed class ExecutableIconConverter : IValueConverter
{
    /// <summary>Gets the shared instance.</summary>
    public static ExecutableIconConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ExecutableIcons.GetIcon(value as string);

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
