using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;
using Shared.Contracts;
using UIBlazor;
using UIBlazor.Services;
using UIBlazor.VS;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddRadzenComponents();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<LocalStorageService>();
builder.Services.AddScoped<AiSettingsProvider>();
builder.Services.AddScoped<IVsBridge, VsBridgeProxy>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

WebAssemblyHost app = builder.Build();

await app.RunAsync();
