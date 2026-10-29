using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkyView.Services;
using SkyView.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SkyView.Views;

public sealed partial class CityListPage : Page
{
    private readonly CityStore _store;
    private readonly IWeatherService _weather;

    public ObservableCollection<CityWeatherCard> Cards { get; } = [];

    public CityListPage()
    {
        InitializeComponent();

        _store = App.Services.GetRequiredService<CityStore>();
        _weather = App.Services.GetRequiredService<IWeatherService>();
        _ = LoadAllAsync();
    }

    private async Task LoadAllAsync()
    {
        var cities = _store.GetCities();

        Cards.Clear();
        foreach (var city in cities)
            Cards.Add(new CityWeatherCard(city));

        await Task.WhenAll(cities.Select(city =>
        {
            var card = Cards.FirstOrDefault(c => c.City == city);
            return card is null ? Task.CompletedTask : LoadCardAsync(card);
        }));
    }

    private async Task LoadCardAsync(CityWeatherCard card)
    {
        try
        {
            var current = await _weather.GetCurrentWithFallbackAsync(card.City).ConfigureAwait(false);
            card.UpdateOnUiThread(this.DispatcherQueue, c => c.Current = current);
        }
        catch (Exception ex)
        {
            card.UpdateOnUiThread(this.DispatcherQueue, c => c.Error = ex.Message);
        }
        finally
        {
            card.UpdateOnUiThread(this.DispatcherQueue, c => c.IsLoading = false);
        }
    }

    private void AddCity_Click(object sender, RoutedEventArgs e)
    {
        var city = NewCityBox.Text?.Trim();
        if (string.IsNullOrEmpty(city))
            return;

        if (Cards.Any(c => string.Equals(c.City, city, StringComparison.OrdinalIgnoreCase)))
        {
            NewCityBox.Text = string.Empty;
            return;
        }

        var card = new CityWeatherCard(city);
        Cards.Add(card);
        _store.Save(Cards.Select(c => c.City));
        NewCityBox.Text = string.Empty;

        _ = LoadCardAsync(card);
    }

    private async void Cards_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CityWeatherCard card)
            return;

        var viewModel = App.Services.GetRequiredService<WeatherViewModel>();
        viewModel.City = card.City;

        try
        {
            await viewModel.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            viewModel.ErrorMessage = ex.Message;
        }

        if (Frame.CanGoBack)
            Frame.GoBack();
        else
            Frame.Navigate(typeof(MainPage));
    }
}
