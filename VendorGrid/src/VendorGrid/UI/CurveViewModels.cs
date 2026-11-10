using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace VendorGrid.UI
{
    /// <summary>One price curve (a vendor listing) on the cost-curve chart. IsVisible is toggled from the legend.</summary>
    public sealed class ChartSeriesVM : ObservableObject
    {
        bool _visible = true;

        static readonly string[] Glyphs = { "●", "■", "◆", "▲", "✚" };

        public ChartSeriesVM(string key, string name, string currency, Brush brush, List<ChartPoint> points, int index)
        {
            Key = key; Name = name; Currency = currency; Brush = brush; Points = points;
            Marker = ((index % 5) + 5) % 5;      // 0 circle, 1 square, 2 diamond, 3 triangle, 4 cross
            Dash = ((index % 3) + 3) % 3;        // 0 solid, 1 dashed, 2 dotted
        }

        /// <summary>Marker shape and line style differ per series so curves can be told apart without colour.</summary>
        public int Marker { get; private set; }
        public int Dash { get; private set; }
        public string MarkerGlyph { get { return Glyphs[Marker]; } }

        public string Key { get; private set; }
        public string Name { get; private set; }
        public string Currency { get; private set; }
        public Brush Brush { get; private set; }
        public List<ChartPoint> Points { get; private set; }
        public bool IsVisible { get { return _visible; } set { Set(ref _visible, value); } }
    }

    public struct ChartPoint
    {
        public ChartPoint(int quantity, decimal unitPrice) : this() { Quantity = quantity; UnitPrice = unitPrice; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    /// <summary>One cell of the volume discount matrix.</summary>
    public sealed class MatrixCellVM
    {
        public string Text { get; set; }
        public bool IsBest { get; set; }
    }

    public sealed class MatrixRowVM
    {
        public string Vendor { get; set; }
        public string Detail { get; set; }
        public Brush LogoBrush { get; set; }
        public System.Windows.Media.ImageSource LogoImage { get; set; }
        public bool HasLogo { get { return LogoImage != null; } }
        public bool NoLogo { get { return LogoImage == null; } }
        public string Code { get; set; }
        public List<MatrixCellVM> Cells { get; set; } = new List<MatrixCellVM>();
    }

    /// <summary>State of the "Cost curve &amp; tiers" tab.</summary>
    public sealed class CurveVM : ObservableObject
    {
        string _note = "";
        bool _hasData;

        public ObservableCollection<ChartSeriesVM> Series { get; } = new ObservableCollection<ChartSeriesVM>();
        public ObservableCollection<MatrixRowVM> MatrixRows { get; } = new ObservableCollection<MatrixRowVM>();
        public List<string> MatrixHeaders { get; private set; } = new List<string>();

        public bool HasData { get { return _hasData; } set { if (Set(ref _hasData, value)) OnPropertyChanged("IsEmpty"); } }
        public bool IsEmpty { get { return !_hasData; } }
        public string Note { get { return _note; } set { Set(ref _note, value ?? ""); } }

        public void SetHeaders(List<string> headers) { MatrixHeaders = headers; OnPropertyChanged("MatrixHeaders"); }
    }
}
