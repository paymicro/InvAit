using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;
using UIBlazor;

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
    .AddScoped<IContextService, ContextService>()
    .AddScoped<IMessageParser, MessageParser>()
    .AddScoped<IInternalExecutor, InternalExecutor>()
    .AddScoped<ISubAgentExecutor, SubAgentExecutor>()
    .AddScoped<BuiltInAgent>()
    .AddScoped<IToolManager, ToolManager>()
    .AddScoped<ISystemPromptBuilder, SystemPromptBuilder>()
    .AddScoped<IRetryHandler, RetryHandler>()
    .AddScoped<IToolCallHandler, ToolCallHandler>()
    .AddScoped<IContentFilter, ContentFilterService>()
    .AddTransient<DynamicEnvironmentHttpMessageHandler>()
    .AddScoped(sp =>
    {
        var handler = sp.GetRequiredService<DynamicEnvironmentHttpMessageHandler>();
        // В Blazor WASM стандартный сетевой стек работает на специальной обертке fetch:
        // Мы создаем системный хендлер, который умеет делать реальные запросы в WebView2/браузере
        // Связываем их в цепочку: DynamicHandler -> BrowserHandler
        handler.InnerHandler = new HttpClientHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
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
