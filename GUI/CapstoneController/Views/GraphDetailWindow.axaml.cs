using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CapstoneController.Views;

public partial class GraphDetailWindow : Window
{
    public GraphDetailWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
