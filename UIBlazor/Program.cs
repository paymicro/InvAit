using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;
using UIBlazor;
using UIBlazor.Agents;
using UIBlazor.Services;
using UIBlazor.VS;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddRadzenComponents()
    .AddScoped<ChatService>()
    .AddScoped<LocalStorageService>()
    .AddScoped<AiSettingsProvider>()
    .AddScoped<CommonSettingsProvider>()
    .AddScoped<IVsBridge, VsBridge>()
    .AddScoped<BuiltInAgent>()
    .AddScoped<ToolManager>()
    .AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

WebAssemblyHost app = builder.Build();

await app.RunAsync();
