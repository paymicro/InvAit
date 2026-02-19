namespace UIBlazor.Services.Settings;

public interface ICommonSettingsProvider : IBaseSettingsProvider
{
    CommonOptions Current { get; }
}