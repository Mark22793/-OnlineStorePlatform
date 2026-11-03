using System.Text.Json.Serialization;
using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Store.Shared.Common.Web;

public static class ServiceDefaults
{
    /// <summary>
    /// Controllers with string enums, URL-segment API versioning (routes like <c>catalog/v{version:apiVersion}/...</c>).
    /// </summary>
    public static IServiceCollection AddStoreControllers(this IServiceCollection services)
    {
        services
            .AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services
            .AddApiVersioning(o =>
            {
                o.DefaultApiVersion = new ApiVersion(1, 0);
                o.ReportApiVersions = true;
                o.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddMvc();

        return services;
    }

    /// <summary>
    /// Contract-first Swagger UI: serves the committed OpenAPI contract(s) from contracts/ (copied to the build
    /// output by the .csproj) and shows one entry per version. Enabled in Development or with Swagger__Enabled=true.
    /// </summary>
    public static WebApplication UseContractSwaggerUi(this WebApplication app, params (string File, string Title)[] contracts)
    {
        if (!app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>("Swagger:Enabled"))
        {
            return app;
        }

        foreach (var (file, _) in contracts)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "contracts", file);
            app.MapGet($"/swagger/contracts/{file}", () => Results.File(path, "application/yaml"));
        }

        app.UseSwaggerUI(o =>
        {
            foreach (var (file, title) in contracts)
            {
                o.SwaggerEndpoint($"/swagger/contracts/{file}", title);
            }
        });

        return app;
    }
}
