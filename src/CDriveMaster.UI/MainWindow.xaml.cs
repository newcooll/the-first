using System.Windows;
using CDriveMaster.Core.Services;

namespace CDriveMaster.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(new LocalDiskScanner(), new RecycleBinService());
    }
}