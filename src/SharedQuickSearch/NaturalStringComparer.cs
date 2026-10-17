using System.Globalization;

namespace SharedQuickSearch;

public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Current = new();

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        var i = 0; var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var sx = i; var sy = j;
                while (i < x.Length && x[i] == '0') i++;
                while (j < y.Length && y[j] == '0') j++;
                var ex = i; var ey = j;
                while (ex < x.Length && char.IsDigit(x[ex])) ex++;
                while (ey < y.Length && char.IsDigit(y[ey])) ey++;
                var lenX = ex - i; var lenY = ey - j;
                if (lenX != lenY) return lenX.CompareTo(lenY);
                var number = string.Compare(x, i, y, j, lenX, StringComparison.Ordinal);
                if (number != 0) return number;
                var zeros = (i - sx).CompareTo(j - sy);
                if (zeros != 0) return zeros;
                i = ex; j = ey;
                continue;
            }
            var cx = char.ToUpper(x[i], CultureInfo.CurrentCulture);
            var cy = char.ToUpper(y[j], CultureInfo.CurrentCulture);
            if (cx != cy) return cx.CompareTo(cy);
            i++; j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
