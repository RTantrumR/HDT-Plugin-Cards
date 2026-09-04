using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HsbgCardLookup.Data;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>null -> Collapsed, else Visible (used to hide empty grid cells).</summary>
    internal sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value == null ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => null;
    }
}
