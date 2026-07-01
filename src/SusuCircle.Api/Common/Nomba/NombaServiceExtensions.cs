using Microsoft.Extensions.Options;

namespace SusuCircle.Api.Common.Nomba;

public static class NombaServiceExtensions
{
    // Call from Program.cs:  builder.Services.AddNombaClient(builder.Configuration);
    public static IServiceCollection AddNombaClient(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<NombaOptions>(config.GetSection(NombaOptions.SectionName));

        services.AddHttpClient<INombaTokenProvider, NombaTokenProvider>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<NombaOptions>>().Value;
            http.BaseAddress = new Uri(opt.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<INombaClient, NombaClient>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<NombaOptions>>().Value;
            http.BaseAddress = new Uri(opt.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}