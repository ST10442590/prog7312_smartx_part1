using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartX.Client;
using SmartX.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Points at the Smart-X ingestion API rather than at the client's own origin,
// which is what the default template does. The CORS policy on the API side
// must list this client's origin for these calls to succeed.
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri("http://localhost:5145")
});

builder.Services.AddScoped<SmartXApiClient>();

await builder.Build().RunAsync();
