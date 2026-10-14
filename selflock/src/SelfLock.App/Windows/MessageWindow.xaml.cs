using System.Windows;
using System.Windows.Input;

namespace SelfLock.Windows;

public partial class MessageWindow
{
    private MessageWindow(string title, string text)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = text;
    }

    public static void Show(Window? owner, string title, string text)
    {
        var w = new MessageWindow(title, text);
        Dialogs.Place(w, owner);
        w.ShowDialog();
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
