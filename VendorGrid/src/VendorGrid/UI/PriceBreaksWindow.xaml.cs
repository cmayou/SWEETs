using System.Windows;
using VendorGrid.Core;

namespace VendorGrid.UI
{
    public partial class PriceBreaksWindow : Window
    {
        public PriceBreaksWindow(ResultRowVM row)
        {
            InitializeComponent();
            TitleText.Text = row.Vendor.Name + " · " + row.Sku;
            SubText.Text = Loc.F("The highlighted break applies to your order of {0} pcs.", Money.Count(row.OrderQuantity));
            TierList.ItemsSource = row.Tiers;
        }

        void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
