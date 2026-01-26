using UIBlazor.Models;

namespace UIBlazor.Options;

public class ProfileOptions : BaseOptions
{
    public List<ConnectionProfile> Profiles { get => field; set => SetIfChanged(ref field, value); } = [];
    public string? ActiveProfileId { get => field; set => SetIfChanged(ref field, value); }
}
