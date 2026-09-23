using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Paystack;

namespace SusuCircle.Api.Common.Nomba;

public static class NombaServiceExtensions
{
    // Call from Program.cs:  builder.Services.AddPaymentProvider(builder.Configuration);
    //
    // Binds INombaClient to whichever payment provider "Payments:Provider" names
    // ("Nomba" or "Paystack"; Nomba remains the default so existing deployments
    // are unaffected). Both implement the same interface, so nothing downstream
    // — handlers, reconciliation, payouts — knows or cares which one is live.
    public static IServiceCollection AddPaymentProvider(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<NombaOptions>(config.GetSection(NombaOptions.SectionName));
        services.Configure<PaystackOptions>(config.GetSection(PaystackOptions.SectionName));

        var provider = config["Payments:Provider"] ?? "Nomba";
        var usePaystack = provider.Equals("Paystack", StringComparison.OrdinalIgnoreCase);

        // Registered unconditionally, even under Paystack: the Dev diagnostics
        // handlers (NombaCheck, CheckBalance) and the reconciliation sweep take
        // INombaTokenProvider as a constructor dependency, so dropping it would
        // break their resolution at runtime rather than at startup.
        services.AddHttpClient<INombaTokenProvider, NombaTokenProvider>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<NombaOptions>>().Value;
            http.BaseAddress = new Uri(opt.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        if (usePaystack)
        {
            services.AddHttpClient<INombaClient, PaystackClient>((sp, http) =>
            {
                var opt = sp.GetRequiredService<IOptions<PaystackOptions>>().Value;
                http.BaseAddress = new Uri(opt.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(30);
            });
        }
        else
        {
            services.AddHttpClient<INombaClient, NombaClient>((sp, http) =>
            {
                var opt = sp.GetRequiredService<IOptions<NombaOptions>>().Value;
                http.BaseAddress = new Uri(opt.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(30);
            });
        }

        return services;
    }
}
