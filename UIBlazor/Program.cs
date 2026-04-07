using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;
using UIBlazor;
using UIBlazor.Services;
using UIBlazor.Services.Settings;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddRadzenComponents()
    .AddScoped<IChatService, ChatService>()
    .AddScoped<ILocalStorageService, LocalStorageService>()
    .AddScoped<IMcpSettingsProvider, McpSettingsProvider>()
    .AddScoped<IProfileManager, ProfileService>()
    .AddScoped<ICommonSettingsProvider, CommonSettingsProvider>()
    .AddScoped<IVsBridge, VsBridge>()
    .AddScoped<ISkillService, SkillService>()
    .AddScoped<IRuleService, RuleService>()
    .AddScoped<IVsCodeContextService, VsCodeContextService>()
    .AddScoped<IMessageParser, MessageParser>()
    .AddScoped<IInternalExecutor, InternalExecutor>()
    .AddScoped<BuiltInAgent>()
    .AddScoped<IToolManager, ToolManager>()
    .AddScoped(sp =>
    {
        var client = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
        client.DefaultRequestHeaders.Add("X-Client-Name", "InvAit Visual Studio Plugin"); // Можно заменить в Extra Headers
        return client;
    })
    .AddLocalization();

var app = builder.Build();

var commonSettings = app.Services.GetRequiredService<ICommonSettingsProvider>();
await commonSettings.InitializeAsync();
var culture = new CultureInfo(commonSettings.Current.Culture);
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

await app.RunAsync();
