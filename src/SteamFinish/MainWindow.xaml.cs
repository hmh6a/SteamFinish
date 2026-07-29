using System.Windows;
using System.Windows.Input;
using SteamFinish.ViewModels;

namespace SteamFinish;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Keeps the timing boxes numeric so the binding never has to reject the value.</summary>
    private void OnNumericInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);
}
