using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using CapstoneController.ViewModels;

namespace CapstoneController.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Avalonia XAML in this project doesn't support x:Reference; wire PlacementTarget in code.
            var frequencyTextBox = this.FindControl<TextBox>("FrequencyTextBox");
            var frequencyPopup = this.FindControl<Popup>("FrequencyNumpadPopup");
            if (frequencyTextBox != null && frequencyPopup != null)
            {
                frequencyPopup.PlacementTarget = frequencyTextBox;
            }
        }

        private void FrequencyTextBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.OpenFrequencyNumpadCommand.Execute(null);
            }
        }

        private void FrequencyTextBox_GotFocus(object? sender, GotFocusEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.OpenFrequencyNumpadCommand.Execute(null);
            }
        }
    }
}