using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HsbgCardLookup.Data;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>BgCard -> cached card-art thumbnail with transparent edges trimmed (so hero/spell
    /// frames fill the cell instead of floating in transparent padding).</summary>
    internal sealed class ThumbConverter : IValueConverter
    {
        private readonly int _decode;
        public ThumbConverter(int decode) { _decode = decode; }
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            var card = value as BgCard;
            if (card == null) return null;
            return ImageCache.LoadTrimmed(CardStore.ResolveImagePath(card, false), _decode);
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => null;
    }

    /// <summary>BgCard -> tier icon (or null when no tier).</summary>
    internal sealed class TierIconConverter : IValueConverter
    {
        private readonly int _decode;
        public TierIconConverter(int decode) { _decode = decode; }
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            var card = value as BgCard;
            if (card == null || !card.Tier.HasValue) return null;
            return ImageCache.Load(CardStore.TierIconPath(card.Tier.Value), _decode);
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => null;
    }

    /// <summary>null -> Collapsed, else Visible (used to hide empty grid cells).</summary>
    internal sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value == null ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => null;
    }

    /// <summary>BgCard -> subtitle line (tribe/type + conditional stats).</summary>
    internal sealed class SubtitleConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            var card = value as BgCard;
            return card == null ? "" : UiKit.SubtitleFor(card);
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => null;
    }
}
