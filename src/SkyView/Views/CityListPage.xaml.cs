using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkyView.Services;
using SkyView.ViewModels;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SkyView.Views;

public sealed partial class CityListPage : Page
{
    private readonly CityStore _store;

    public ObservableCollection<string> Cities { get; } = [];

    public CityListPage()
    {
        InitializeComponent();

        _store = App.Services.GetRequiredService<CityStore>();
        ReloadCities();
    }

    private void ReloadCities()
    {
        Cities.Clear();
        foreach (var city in _store.GetCities())
            Cities.Add(city);
    }

    private void AddCity_Click(object sender, RoutedEventArgs e)
    {
        var city = NewCityBox.Text?.Trim();
        if (string.IsNullOrEmpty(city))
            return;

        if (Cities.Any(c => string.Equals(c, city, System.StringComparison.OrdinalIgnoreCase)))
        {
            NewCityBox.Text = string.Empty;
            return;
        }

        Cities.Add(city);
        _store.Save(Cities);
        NewCityBox.Text = string.Empty;
    }

    private void DeleteCity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string city } && Cities.Remove(city))
            _store.Save(Cities);
    }

    private async void CitiesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string city)
            return;

        var viewModel = App.Services.GetRequiredService<WeatherViewModel>();
        viewModel.City = city;

        await RefreshAsync(viewModel);

        if (Frame.CanGoBack)
            Frame.GoBack();
        else
            Frame.Navigate(typeof(MainPage));
    }

    private static async Task RefreshAsync(WeatherViewModel viewModel)
    {
        try
        {
            await viewModel.RefreshCommand.ExecuteAsync(null);
        }
        catch (System.Exception ex)
        {
            viewModel.ErrorMessage = ex.Message;
        }
    }
}
