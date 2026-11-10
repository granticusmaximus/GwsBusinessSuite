namespace GwsBusinessSuite.App;

public partial class NativeOverwatchPage : ContentPage
{
    public NativeOverwatchPage()
    {
        InitializeComponent();
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
