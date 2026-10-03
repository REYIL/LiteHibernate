using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteHibernate.UI;

public partial class ApplicationListDialog : Window
{
    public ApplicationListDialog(string applicationName, bool restoring = false)
    {
        InitializeComponent();
        Title = DialogHeading.Text = restoring ? "Показать приложение" : "Скрыть приложение";
        RemovalPrompt.Text = restoring ? $"Показать «{applicationName}» в списке LiteHibernate?" : $"Скрыть «{applicationName}» из списка LiteHibernate?";
        if (restoring)
        {
            ConfirmRemovalButton.Content = "Показать";
            ConfirmRemovalButton.Foreground = new SolidColorBrush(Color.FromRgb(154, 217, 192));
            ConfirmRemovalButton.Background = new SolidColorBrush(Color.FromRgb(32, 53, 47));
            ConfirmRemovalButton.BorderBrush = new SolidColorBrush(Color.FromRgb(57, 95, 82));
        }
        Loaded += (_, _) => Keyboard.Focus(CancelRemovalButton);
    }

    public static bool ConfirmRemoval(AppRow row) => Confirm(row, false);
    public static bool ConfirmRestoration(AppRow row) => Confirm(row, true);
    private static bool Confirm(AppRow row, bool restoring)
    {
        var dialog = new ApplicationListDialog(row.Name, restoring);
        if (Application.Current?.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
