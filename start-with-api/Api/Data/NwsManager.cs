﻿using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Api.Data;

namespace Api
{
    public class NwsManager(HttpClient httpClient, IMemoryCache cache, IWebHostEnvironment webHostEnvironment)
    {
        private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

        public async Task<Zone[]?> GetZonesAsync()
        {
            return await cache.GetOrCreateAsync("zones", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);

                // To get the live zone data from NWS, uncomment the following code and comment out the return statement below.
                // This is required if you are deploying to ACA.
                //var zones = await httpClient.GetFromJsonAsync<ZonesResponse>("https://api.weather.gov/zones?type=forecast", options);
                //return zones?.Features
                //            ?.Where(f => f.Properties?.ObservationStations?.Count > 0)
                //            .Select(f => (Zone)f)
                //            .Distinct()
                //            .ToArray() ?? [];

                // Deserialize the zones.json file from the wwwroot folder
                var zonesFilePath = Path.Combine(webHostEnvironment.WebRootPath, "zones.json");
                if (!File.Exists(zonesFilePath))
                {
                    return [];
                }

                using var zonesJson = File.OpenRead(zonesFilePath);
                var zones = await JsonSerializer.DeserializeAsync<ZonesResponse>(zonesJson, options);

                return zones?.Features
                            ?.Where(f => f.Properties?.ObservationStations?.Count > 0)
                            .Select(f => (Zone)f)
                            .Distinct()
                            .ToArray() ?? [];
            });
        }

        private static int forecastCount = 0;

        public async Task<Forecast[]> GetForecastByZoneAsync(string zoneId)
        {
            // Create an exception every 5 calls to simulate and error for testing
            forecastCount++;

            if (forecastCount % 5 == 0)
            {
                throw new Exception("Random exception thrown by NwsManager.GetForecastAsync");
            }

            var zoneIdSegment = HttpUtility.UrlEncode(zoneId);
            var zoneUrl = $"https://api.weather.gov/zones/forecast/{zoneIdSegment}/forecast";
            var forecasts = await httpClient.GetFromJsonAsync<ForecastResponse>(zoneUrl, options);
            return forecasts
                   ?.Properties
                   ?.Periods
                   ?.Select(p => (Forecast)p)
                   .ToArray() ?? [];
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class NwsManagerExtensions
    {
        public static IServiceCollection AddNwsManager(this IServiceCollection services)
        {
            services.AddHttpClient<Api.NwsManager>(client =>
            {
                client.BaseAddress = new Uri("https://api.weather.gov/");
                client.DefaultRequestHeaders.Add("User-Agent", "Microsoft - .NET Aspire Demo");
            });

            services.AddMemoryCache();

            //// Add default output caching
            //services.AddOutputCache(options =>
            //{
            //    options.AddBasePolicy(builder => builder.Cache());
            //});

            return services;
        }

        public static WebApplication? MapApiEndpoints(this WebApplication app)
        {
            app.UseOutputCache();

            app.MapGet("/zones", async (Api.NwsManager manager) =>
                {
                    var zones = await manager.GetZonesAsync();
                    return TypedResults.Ok(zones);
                })
                .CacheOutput(policy => policy.Expire(TimeSpan.FromHours(1)))
                .WithName("GetZones")
                .WithOpenApi();

            app.MapGet("/forecast/{zoneId}", async Task<Results<Ok<Api.Forecast[]>, NotFound>> (Api.NwsManager manager, string zoneId) =>
                {
                    try
                    {
                        var forecasts = await manager.GetForecastByZoneAsync(zoneId);
                        return TypedResults.Ok(forecasts);
                    }
                    catch (HttpRequestException)
                    {
                        return TypedResults.NotFound();
                    }
                })
                .CacheOutput(policy => policy.Expire(TimeSpan.FromMinutes(15)).SetVaryByRouteValue("zoneId"))
                .WithName("GetForecastByZone")
                .WithOpenApi();

            return app;
        }
    }
}